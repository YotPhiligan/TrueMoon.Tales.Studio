using System.Text;
using System.Text.Json;
using TrueMoon.Tales.Studio.Contracts.Projects;
using TrueMoon.Tales.Studio.Contracts.Serialization;
using TrueMoon.Tales.Studio.Persistence;

namespace TrueMoon.Tales.Studio.Backend;

internal sealed partial class StudioBackendService
{
    private readonly SemaphoreSlim _gate = new(1, 1);
    private readonly MontageUndoHistory _montageHistory = new();
    private ProjectWriterLease? _writer;
    private bool _disposed;

    public Task<BackendProjectReply> AcquireWriterAsync(string sessionKey, long expectedRevision, string fingerprint, CancellationToken cancellationToken = default) =>
        ExecuteAsync(sessionKey, async () =>
        {
            RequireWriteMode();
            var lease = _writer is null ? ProjectWriterLease.Acquire(BackendProjectAccess.Read(options)) : null;
            try
            {
                var baseline = await ObserveAsync(expectedRevision, fingerprint, cancellationToken).ConfigureAwait(false);
                var reply = Reply(baseline);
                if (reply.ErrorCode.Length == 0) { _writer ??= lease; lease = null; }
                return reply;
            }
            finally { lease?.Dispose(); }
        }, cancellationToken);

    public Task<BackendProjectReply> MoveClipAsync(string sessionKey, string clipId, int startFrame, long expectedRevision, string fingerprint, CancellationToken cancellationToken = default) =>
        ExecuteAsync(sessionKey, async () =>
        {
            RequireWriter();
            var baseline = await ObserveAsync(expectedRevision, fingerprint, cancellationToken).ConfigureAwait(false);
            if (baseline.SchemaVersion != 2) throw new ProjectMigrationRequiredException();
            if (baseline.Project.Revision >= int.MaxValue) throw new InvalidDataException("Revision exceeds the current scene UI range.");
            var draft = BackendMove.Apply(baseline.Project, ScenarioActive(baseline), clipId, startFrame);
            if (ReferenceEquals(draft, baseline.Project)) return Reply(baseline);
            if (Encoding.Unicode.GetByteCount(ProjectContractJson.Serialize(draft)) > MaxProjectBytes - 32)
                return Failure("project_too_large");
            return await SaveMontageAsync(baseline, draft, expectedRevision, "studio:enerit", cancellationToken).ConfigureAwait(false);
        }, cancellationToken);

    public Task<BackendProjectReply> TrimClipAsync(string sessionKey, string clipId, double sourceInFrame, double sourceOutFrame,
        int startFrame, long expectedRevision, string fingerprint, CancellationToken cancellationToken = default) =>
        ExecuteAsync(sessionKey, async () =>
        {
            RequireWriter();
            var baseline = await ObserveAsync(expectedRevision, fingerprint, cancellationToken).ConfigureAwait(false);
            if (baseline.SchemaVersion != 2) throw new ProjectMigrationRequiredException();
            if (baseline.Project.Revision >= int.MaxValue) throw new InvalidDataException("Revision exceeds the current scene UI range.");
            // Validate layout and detect an exact logical no-op before opening media or changing representation.
            var active = ScenarioActive(baseline);
            var draft = BackendTrim.Apply(baseline.Project, active, clipId, sourceInFrame, sourceOutFrame, startFrame, double.MaxValue);
            if (ReferenceEquals(draft, baseline.Project)) return Reply(baseline);
            var clip = baseline.Project.Montage.Clips.Single(row => row.Id == clipId);
            var take = baseline.Project.Sources.Single(source => source.Id == clip.SourceId).Takes.SingleOrDefault(row => row.Id == clip.TakeId);
            if (take?.VideoAssetId is not { Length: > 0 } assetId) throw new InvalidTrimException();
            var info = await ReadMediaAsync(sessionKey, assetId, -1, expectedRevision, fingerprint, cancellationToken, waitForAdmission: true).ConfigureAwait(false);
            if (info.ErrorCode.Length != 0) return Failure(info.ErrorCode);
            var capacity = info.FrameCount / info.Fps * baseline.Project.Fps;
            draft = BackendTrim.Apply(baseline.Project, active, clipId, sourceInFrame, sourceOutFrame, startFrame, capacity);
            if (Encoding.Unicode.GetByteCount(ProjectContractJson.Serialize(draft)) > MaxProjectBytes - 32)
                return Failure("project_too_large");
            return await SaveMontageAsync(baseline, draft, expectedRevision, "studio:enerit", cancellationToken).ConfigureAwait(false);
        }, cancellationToken);

    public Task<BackendProjectReply> MigrateProjectAsync(string sessionKey, long expectedRevision, string fingerprint, CancellationToken cancellationToken = default) =>
        ExecuteAsync(sessionKey, async () =>
        {
            RequireWriter();
            var baseline = await ObserveAsync(expectedRevision, fingerprint, cancellationToken).ConfigureAwait(false);
            if (baseline.SchemaVersion == 2) return Reply(baseline);
            if (Reply(baseline).ErrorCode.Length != 0) return Failure("project_too_large");
            return Reply(await store.MigrateAsync(baseline, expectedRevision, "studio:enerit", cancellationToken).ConfigureAwait(false));
        }, cancellationToken);

    public Task<BackendProjectReply> RecoverProjectAsync(string sessionKey, CancellationToken cancellationToken = default) =>
        ExecuteAsync(sessionKey, async () =>
        {
            RequireWriteMode();
            _writer ??= ProjectWriterLease.Acquire(BackendProjectAccess.Read(options));
            return Reply(options.RecoveryFingerprint is { } fingerprint
                ? await store.RecoverObservedAsync(fingerprint, cancellationToken).ConfigureAwait(false)
                : await store.RecoverAsync(cancellationToken).ConfigureAwait(false));
        }, cancellationToken);

    private async Task<ProjectFileDocument> ObserveAsync(long revision, string fingerprint, CancellationToken token)
    {
        if (revision < 0 || fingerprint is null || fingerprint.Length != 64 || !fingerprint.All(Uri.IsHexDigit))
            throw new ArgumentException("Invalid expected revision or fingerprint.");
        var current = await store.OpenAsync(token).ConfigureAwait(false);
        if (current.Project.Revision != revision || !string.Equals(current.Fingerprint, fingerprint, StringComparison.OrdinalIgnoreCase))
            throw new ProjectRevisionConflictException(revision, current.Project.Revision);
        return current;
    }

    private async Task<BackendProjectReply> ExecuteAsync(string key, Func<Task<BackendProjectReply>> operation, CancellationToken token)
    {
        Authorize(key);
        await _gate.WaitAsync(token).ConfigureAwait(false);
        try
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            return await operation().ConfigureAwait(false);
        }
        catch (WriterRequiredException error) { return Failure(error.Message); }
        catch (ProjectRevisionConflictException) { return Failure("revision_conflict"); }
        catch (ProjectMigrationRequiredException) { return Failure("migration_required"); }
        catch (ProjectRecoveryConflictException) { return Failure("recovery_conflict"); }
        catch (ProjectRecoveryRequiredException) { return Failure("recovery_required"); }
        catch (ProjectStoreBusyException) { return Failure("writer_busy"); }
        catch (InvalidMoveException) { return Failure("invalid_move"); }
        catch (InvalidTrimException) { return Failure("invalid_trim"); }
        catch (ScenarioEditException error) { return Failure(error.Code); }
        catch (UnauthorizedAccessException) { return Failure("access_denied"); }
        catch (FileNotFoundException) { return Failure("project_not_found"); }
        catch (DirectoryNotFoundException) { return Failure("project_not_found"); }
        catch (Exception error) when (error is JsonException or InvalidDataException or InvalidOperationException or ArgumentException or NotSupportedException)
        { return Failure("invalid_project"); }
        catch (IOException) { return Failure("project_io_error"); }
        finally { _gate.Release(); }
    }

    private void RequireWriteMode() { if (!options.WriteEnabled) throw new WriterRequiredException("write_not_enabled"); }
    private void RequireWriter() { RequireWriteMode(); if (_writer is null) throw new WriterRequiredException("writer_required"); }

    internal static bool ScenarioActive(ProjectFileDocument document)
    {
        var original = document.OriginalDocument;
        if (document.SchemaVersion == 2 && !original.TryGetProperty("legacy_document", out original))
            return document.Project.Properties.TryGetValue("scenario_active", out var active) && active.GetBoolean();
        return original.TryGetProperty("timeline", out var timeline) && timeline.TryGetProperty("scenario", out var scenario) &&
            scenario.ValueKind == JsonValueKind.Object && scenario.EnumerateObject().Any();
    }

    private async Task<BackendProjectReply> SaveMontageAsync(ProjectFileDocument baseline, ProjectContract draft,
        long expectedRevision, string actor, CancellationToken token)
    {
        BackendBlackTransitions.Validate(draft.Montage);
        var saved = await store.SaveAsync(baseline, draft, expectedRevision, actor, token).ConfigureAwait(false);
        _montageHistory.Record(baseline, saved);
        return Reply(saved);
    }

    private BackendProjectReply Reply(ProjectFileDocument document)
    {
        var json = ProjectContractJson.Serialize(document.Project);
        // Enerit's string codec uses UTF-16. Reserve space for the reply envelope.
        if (Encoding.Unicode.GetByteCount(json) > MaxProjectBytes) return Failure("project_too_large");
        var (undo, redo) = _montageHistory.Available(document.Fingerprint);
        return new() { SchemaVersion = document.SchemaVersion, Fingerprint = document.Fingerprint, ProjectJson = json,
            ScenarioActive = ScenarioActive(document), CanUndoMontage = undo, CanRedoMontage = redo };
    }
    public void Dispose()
    {
        _mediaStopping.Cancel();
        proxies?.Dispose();
        try { StopPlayback(_playback); }
        finally { StopPlayback(_reviewAudio); }
        _mediaGate.Wait();
        _exports.Dispose();
        // Drain a command before relinquishing the session lease, including durable commit completion.
        _gate.Wait();
        try
        {
            _disposed = true;
            try { _transfers.Dispose(); }
            finally { _writer?.Dispose(); _writer = null; }
        }
        finally { _gate.Release(); _mediaGate.Release(); }
    }
    private sealed class WriterRequiredException(string code) : InvalidOperationException(code);
}
