using System.ComponentModel;
using System.Text;
using System.Text.Json;
using TrueMoon.Tales.Studio.Contracts.Projects;
using TrueMoon.Tales.Studio.Contracts.Serialization;
using TrueMoon.Tales.Studio.Persistence;

namespace TrueMoon.Tales.Studio.Backend;

internal sealed partial class StudioBackendService
{
    public async Task<BackendTextReply> ReadAdaptationTextAsync(string sessionKey, string reference, int firstLine, int lastLine,
        CancellationToken cancellationToken = default)
    {
        Authorize(sessionKey);
        using var scope = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        scope.CancelAfter(TimeSpan.FromSeconds(8));
        try
        {
            var fragment = await BackendAdaptation.ReadTextAsync(new(options), reference, firstLine, lastLine, scope.Token).ConfigureAwait(false);
            return new() { FragmentJson = JsonSerializer.Serialize(fragment, BackendAdaptation.Json) };
        }
        catch (ScenarioEditException error) { return new() { ErrorCode = error.Code }; }
        catch (UnauthorizedAccessException) { return new() { ErrorCode = "access_denied" }; }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested) { return new() { ErrorCode = "text_timeout" }; }
        catch (Exception error) when (error is IOException or InvalidDataException or ArgumentException or JsonException)
        { return new() { ErrorCode = "text_unavailable" }; }
    }

    public Task<BackendProjectReply> EditScenarioAsync(string sessionKey, string commandJson, long expectedRevision, string fingerprint,
        CancellationToken cancellationToken = default) => ExecuteAsync(sessionKey, async () =>
    {
        RequireWriter();
        var baseline = await ObserveAsync(expectedRevision, fingerprint, cancellationToken).ConfigureAwait(false);
        if (baseline.SchemaVersion != 2) throw new ProjectMigrationRequiredException();
        try
        {
            var command = ScenarioCommand.Decode(commandJson);
            if (command is RemoveVideoTake removal)
                return await RemoveTakeAsync(baseline, removal, cancellationToken).ConfigureAwait(false);
            if (command is UndoMontage or RedoMontage)
            {
                if (baseline.Project.Revision >= int.MaxValue) throw new InvalidDataException("Revision exceeds the editor range.");
                var undo = command is UndoMontage;
                var target = _montageHistory.Target(baseline.Fingerprint, undo);
                var restored = await store.RestoreSnapshotAsync(baseline, target, expectedRevision,
                    undo ? "studio:undo-montage" : "studio:redo-montage",
                    [BackendAgentBreakdown.AppliedProperty, ScenarioActionWrite.ReceiptsProperty, BackendBrief.Property,
                        BriefWrite.ReceiptsProperty], cancellationToken).ConfigureAwait(false);
                _montageHistory.Complete(restored.Fingerprint, undo);
                return Reply(restored);
            }
            var draft = command switch
            {
                BindAdaptationText edit => await BackendAdaptation.BindTextAsync(baseline.Project, edit, new(options), cancellationToken).ConfigureAwait(false),
                SaveAnimaticStill edit => BackendAdaptation.SetStill(baseline.Project, edit, new(options)),
                SaveSceneJoin edit => BackendSceneJoins.Apply(baseline.Project, edit),
                SaveFrameNote edit => await BackendFrameNotes.ApplyAsync(baseline.Project, edit,
                    (anchor, location) => ObserveFrameNoteAsync(baseline, anchor, location, cancellationToken)).ConfigureAwait(false),
                AddReferenceVersion edit => await BackendReferenceLibrary.AddAsync(baseline.Project, edit, new(options), cancellationToken).ConfigureAwait(false),
                AssignSceneReference edit => BackendReferenceLibrary.Assign(baseline.Project, edit),
                RecordTakeReference edit => await BackendReferenceLibrary.RecordInputAsync(baseline.Project, edit, new(options), cancellationToken).ConfigureAwait(false),
                LinkDraftFinalTake edit => await BackendTakePairs.LinkAsync(baseline.Project, edit,
                    media ?? throw new ScenarioEditException("decoder_unavailable"), cancellationToken).ConfigureAwait(false),
                MoveScenarioAction edit => BackendScenarioEdit.MoveAction(baseline.Project, edit, ScenarioActive(baseline)),
                MontageTrackCommand track => await BackendTrackEdits.ApplyAsync(baseline.Project, track, ProbeTrackAsync,
                    options, cancellationToken).ConfigureAwait(false),
                _ => await BackendScenarioEdit.ApplyAsync(baseline.Project, command, ProbeAsync, cancellationToken).ConfigureAwait(false)
            };
            if (ReferenceEquals(draft, baseline.Project)) return Reply(baseline);
            if (draft.Revision >= int.MaxValue) throw new InvalidDataException("Revision exceeds the editor range.");
            if (Encoding.Unicode.GetByteCount(ProjectContractJson.Serialize(draft)) > MaxProjectBytes - 32)
                return Failure("project_too_large");
            if (command is PlaceClip or AssignTake or SplitMontageClip or RemoveMontageClip or SetBlackTransition or MontageTrackCommand)
                return await SaveMontageAsync(baseline, draft, expectedRevision, "studio:montage", cancellationToken).ConfigureAwait(false);
            return Reply(await store.SaveAsync(baseline, draft, expectedRevision, "studio:scenario", cancellationToken).ConfigureAwait(false));
        }
        catch (ScenarioEditException error) { return Failure(error.Code); }
        catch (Win32Exception) { return Failure("decoder_unavailable"); }
    }, cancellationToken);

    public Task<BackendScenarioActionReply> SaveScenarioActionAsync(string sessionKey, string formJson,
        CancellationToken cancellationToken = default) => ExecuteAsync<BackendScenarioActionReply>(sessionKey, async () =>
    {
        RequireWriter();
        var bodySize = formJson is null ? 0 : Encoding.UTF8.GetByteCount(formJson);
        if (formJson is null || bodySize is 0 or > ScenarioSourcePatch.MaxBodyBytes)
            return ActionFailure("invalid_scenario_edit");
        if (JsonSerializer.Deserialize<ScenarioActionWrite>(formJson, AgentRequestDraft.Json) is not { Valid: true } write ||
            write.SourcePatch is null && bodySize > ScenarioActionWrite.MaxBodyBytes)
            return ActionFailure("invalid_scenario_edit");

        var baseline = await store.OpenAsync(cancellationToken).ConfigureAwait(false);
        if (baseline.SchemaVersion != 2) throw new ProjectMigrationRequiredException();
        var keyHash = write.KeyHash();
        if (ScenarioActionWrite.FindReceipt(baseline.Project, keyHash) is { } prior)
            return prior.Matches(write) ? ActionReply(prior) : ActionFailure("scenario_action_key_conflict");
        if (baseline.Project.Revision != write.ExpectedRevision ||
            !string.Equals(baseline.Fingerprint, write.Fingerprint, StringComparison.OrdinalIgnoreCase))
            return ActionFailure("revision_conflict");

        var command = write.Command!;
        ProjectContract draft;
        try
        {
            draft = command switch
            {
                MoveScenarioAction move => BackendScenarioEdit.MoveAction(baseline.Project, move, ScenarioActive(baseline)),
                MontageTrackCommand track => await BackendTrackEdits.ApplyAsync(baseline.Project, track, ProbeTrackAsync,
                    options, cancellationToken).ConfigureAwait(false),
                _ => await BackendScenarioEdit.ApplyAsync(baseline.Project, command, ProbeAsync, cancellationToken).ConfigureAwait(false)
            };
        }
        catch (Exception error) when (command is (ImportVideo or PlaceClip) && error is (FileNotFoundException or DirectoryNotFoundException))
        { return ActionFailure("media_not_found"); }
        catch (Win32Exception) when (command is ImportVideo or PlaceClip)
        { return ActionFailure("decoder_unavailable"); }
        var changed = !ReferenceEquals(draft, baseline.Project);
        var receipt = ScenarioActionReceipt.Create(write, draft, changed);
        if (!changed) return ActionReply(receipt);

        draft = ScenarioActionWrite.Record(draft, keyHash, receipt);
        if (Encoding.Unicode.GetByteCount(ProjectContractJson.Serialize(draft)) > MaxProjectBytes - 32)
            return ActionFailure("project_too_large");
        if (command is PlaceClip or MontageTrackCommand)
            await SaveMontageAsync(baseline, draft, write.ExpectedRevision, write.Actor.Trim(), cancellationToken).ConfigureAwait(false);
        else
            await store.SaveAsync(baseline, draft, write.ExpectedRevision, write.Actor.Trim(), cancellationToken).ConfigureAwait(false);
        return ActionReply(receipt);

        static BackendScenarioActionReply ActionReply(ScenarioActionReceipt receipt) => new() { ReceiptJson = JsonSerializer.Serialize(receipt, AgentRequestDraft.Json) };
    }, cancellationToken, ActionFailure);

    private static BackendScenarioActionReply ActionFailure(string code) => new() { ErrorCode = code };

    private async Task<BackendProjectReply> RemoveTakeAsync(ProjectFileDocument baseline, RemoveVideoTake command, CancellationToken token)
    {
        if (!command.Confirmed) return Failure("take_confirmation_required");
        if (_transfers.Busy || _exports.HasActiveJob) return Failure("take_jobs_running");
        if (!OperatingSystem.IsWindowsVersionAtLeast(6, 2)) return Failure("take_recycle_unavailable");
        var resolver = new BackendMediaResolver(options);
        var paths = BackendProjectAccess.Read(options);
        var saved = baseline;
        BackendTakeRemoval.Removal removal;
        string path;
        if (BackendTakeRemoval.Find(baseline.Project, command.SourceId, command.TakeId) is { } previous)
        {
            BackendTakeRemoval.CheckLinks(baseline.Project, previous.TakeId, previous.AssetId, previous.Reference, resolver);
            removal = previous;
            path = BackendTakeRemoval.AuthorizedPath(previous.Reference, paths, resolver);
            if (!File.Exists(path)) return Reply(baseline);
        }
        else
        {
            var draft = BackendTakeRemoval.RemoveFromLibrary(baseline.Project, command.SourceId, command.TakeId, out var reference, resolver);
            (removal, path) = await BackendTakeRemoval.CheckFileAsync(baseline.Project, command.SourceId, command.TakeId,
                reference, paths, resolver, token).ConfigureAwait(false);
            draft = BackendTakeRemoval.Record(draft, removal);
            if (Encoding.Unicode.GetByteCount(ProjectContractJson.Serialize(draft)) > MaxProjectBytes - 32)
                return Failure("project_too_large");
            saved = await store.SaveAsync(baseline, draft, baseline.Project.Revision, "studio:remove-take", token).ConfigureAwait(false);
        }
        try { await BackendTakeRemoval.RecycleAsync(removal, path, WindowsRecycleBin.MoveFile).ConfigureAwait(false); }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or System.Runtime.InteropServices.COMException)
        { return Failure("take_recycle_pending"); }
        return Reply(saved);
    }

    private Task<BackendMediaReply> ObserveFrameNoteAsync(ProjectFileDocument baseline, FrameNoteAnchor anchor,
        FrameNoteLocation location, CancellationToken token) => anchor.AssetId is { } asset
        ? FrameAsync(options.SessionKey, asset, anchor.SourceFrame, baseline.Project.Revision, baseline.Fingerprint, token)
        : StoryboardStillAsync(options.SessionKey, location.SegmentId!, anchor.ActionId!, baseline.Project.Revision, baseline.Fingerprint, token);

    private async Task<BackendVideoInfo> ProbeAsync(string reference, CancellationToken token)
    {
        if (media is null) throw new ScenarioEditException("decoder_unavailable");
        try { return await media.ProbeReferenceAsync(reference, token).ConfigureAwait(false); }
        catch (InvalidDataException) { throw new ScenarioEditException("invalid_media"); }
    }

    private async Task<double> ProbeTrackAsync(string reference, int stream, CancellationToken token)
    {
        if (media is null) throw new ScenarioEditException("decoder_unavailable");
        try { return await media.ProbeAudioReferenceAsync(reference, stream, token).ConfigureAwait(false); }
        catch (InvalidDataException) { throw new ScenarioEditException("montage_audio_source"); }
        catch (Win32Exception) { throw new ScenarioEditException("decoder_unavailable"); }
        catch (FileNotFoundException) { throw new ScenarioEditException("montage_audio_source"); }
    }
}
