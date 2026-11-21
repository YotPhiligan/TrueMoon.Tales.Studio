using System.ComponentModel;
using System.Text.Json;
using TrueMoon.Tales.Studio.Contracts.Serialization;
using TrueMoon.Tales.Studio.Backend.Export;
using TrueMoon.Tales.Studio.Persistence;

namespace TrueMoon.Tales.Studio.Backend;

/// <summary>Receipt of a Full Transfer CPU preparation, or a classified refusal.</summary>
public sealed class BackendPreparationReply
{
    /// <summary>Serialized receipt. Empty on failure.</summary>
    public string ReceiptJson { get; set; } = "";
    /// <summary>Stable backend refusal code. Empty on success.</summary>
    public string ErrorCode { get; set; } = "";
}

internal sealed partial class StudioBackendService
{
    private readonly BackendFullTransferRuns _transfers = new();

    public async Task<BackendPreparationReply> FullTransferRunAsync(string sessionKey, string jobId, string requestSha, string operation,
        long expectedRevision, string fingerprint, CancellationToken cancellationToken = default)
    {
        Authorize(sessionKey);
        using var scope = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, _mediaStopping.Token);
        scope.CancelAfter(TimeSpan.FromSeconds(42));
        var token = scope.Token;
        var admitted = false;
        try
        {
            await _gate.WaitAsync(token).ConfigureAwait(false);
            admitted = true;
            ObjectDisposedException.ThrowIf(_disposed, this);
            if (operation is not ("status" or "launch" or "stop")) throw new ScenarioEditException("invalid_transfer");
            if (operation != "status") RequireWriter();
            if (operation == "stop" && await _transfers.StopOwnedAsync(jobId, requestSha).ConfigureAwait(false) is { } stopped)
                return new() { ReceiptJson = JsonSerializer.Serialize(stopped, BackendAdaptation.Json) };
            var baseline = operation == "status"
                ? await store.OpenAsync(token).ConfigureAwait(false)
                : await ObserveAsync(expectedRevision, fingerprint, token).ConfigureAwait(false);
            if (baseline.SchemaVersion != 2) throw new ProjectMigrationRequiredException();
            var job = BackendFullTransfer.OpenRun(options, baseline.Project, jobId, requestSha);
            if (BackendTakeRemoval.Find(baseline.Project, job.Request.SourceId, BackendFullTransfer.FinalTakeId(job.Id)) is not null)
                throw new ScenarioEditException("transfer_take_removed");
            if (operation == "launch" && Path.Exists(job.Run)) throw new ScenarioEditException("transfer_already_launched");
            var state = operation == "launch"
                ? await _transfers.LaunchAsync(job, options,
                    async scratch => await BackendFullTransfer.OpenOrStageAsync(options, baseline, jobId, requestSha,
                        async ct => { await ObserveAsync(expectedRevision, fingerprint, ct).ConfigureAwait(false); }, token,
                        forLaunch: true, scratchInput: scratch).ConfigureAwait(false),
                    ct => RegisterOwnedOutputAsync(job, ct), token).ConfigureAwait(false)
                : _transfers.Read(job);
            return new() { ReceiptJson = JsonSerializer.Serialize(state, BackendAdaptation.Json) };
        }
        catch (WriterRequiredException error) { return Failure(error.Message); }
        catch (ScenarioEditException error) { return Failure(error.Code); }
        catch (ProjectRevisionConflictException) { return Failure("revision_conflict"); }
        catch (ProjectRecoveryRequiredException) { return Failure("recovery_required"); }
        catch (ProjectMigrationRequiredException) { return Failure("migration_required"); }
        catch (UnauthorizedAccessException) { return Failure("access_denied"); }
        catch (Exception error) when (error is FileNotFoundException or DirectoryNotFoundException) { return Failure("transfer_missing"); }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested) { return Failure("transfer_timeout"); }
        catch (Exception error) when (error is InvalidDataException or JsonException or KeyNotFoundException or InvalidOperationException or ArgumentException)
        { return Failure("transfer_damaged"); }
        catch (IOException) { return Failure("project_io_error"); }
        finally { if (admitted) _gate.Release(); }

        static BackendPreparationReply Failure(string code) => new() { ErrorCode = code };
    }

    public Task<BackendProjectReply> CollectFullTransferAsync(string sessionKey, string jobId, string requestSha, long expectedRevision,
        string fingerprint, CancellationToken cancellationToken = default) => ExecuteAsync(sessionKey, async () =>
    {
        RequireWriter();
        var baseline = await ObserveAsync(expectedRevision, fingerprint, cancellationToken).ConfigureAwait(false);
        if (baseline.SchemaVersion != 2) throw new ProjectMigrationRequiredException();
        var job = BackendFullTransfer.OpenRun(options, baseline.Project, jobId, requestSha);
        if (BackendTakeRemoval.Find(baseline.Project, job.Request.SourceId, BackendFullTransfer.FinalTakeId(job.Id)) is not null)
            throw new ScenarioEditException("transfer_take_removed");
        if (_transfers.Read(job).State != "completed") throw new ScenarioEditException("transfer_run_incomplete");
        return await RegisterOutputAsync(job, baseline, cancellationToken).ConfigureAwait(false);
    }, cancellationToken);

    private async Task<string?> RegisterOwnedOutputAsync(BackendFullTransfer.RunJob job, CancellationToken token)
    {
        using var scope = CancellationTokenSource.CreateLinkedTokenSource(token, _mediaStopping.Token);
        await _gate.WaitAsync(scope.Token).ConfigureAwait(false);
        try
        {
            RequireWriter();
            var baseline = await store.OpenAsync(scope.Token).ConfigureAwait(false);
            var reply = await RegisterOutputAsync(job, baseline, scope.Token).ConfigureAwait(false);
            return reply.ErrorCode.Length == 0 ? null : reply.ErrorCode;
        }
        catch (ScenarioEditException error) { return error.Code; }
        catch (ProjectRevisionConflictException) { return "revision_conflict"; }
        catch (ProjectFileConflictException) { return "project_file_conflict"; }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or InvalidOperationException) { return "registration_pending"; }
        finally { _gate.Release(); }
    }

    private Task<BackendProjectReply> RegisterOutputAsync(BackendFullTransfer.RunJob job, ProjectFileDocument baseline, CancellationToken token)
    {
        if (media is null) throw new ScenarioEditException("decoder_unavailable");
        if (baseline.SchemaVersion != 2 || baseline.Project.Id != job.Request.ProjectId) throw new ScenarioEditException("transfer_input_changed");
        return BackendFullTransfer.CollectAsync(job, baseline.Project, media, new BackendMediaResolver(options), async draft =>
        {
            if (ReferenceEquals(draft, baseline.Project)) return Reply(baseline);
            if (System.Text.Encoding.Unicode.GetByteCount(ProjectContractJson.Serialize(draft)) > MaxProjectBytes - 32)
                return Failure("project_too_large");
            return Reply(await store.SaveAsync(baseline, draft, baseline.Project.Revision, "studio:full-transfer", token).ConfigureAwait(false));
        }, token);
    }

    // Writer admission reconciles finished jobs only. The launch claim is never replayed.
    private async Task<ProjectFileDocument> RecoverTransferOutputsAsync(ProjectFileDocument baseline, CancellationToken token)
    {
        if (baseline.SchemaVersion != 2 || media is null || _transfers.Busy) return baseline;
        var paths = BackendProjectAccess.Read(options);
        foreach (var job in BackendFullTransfer.RecoverableRuns(paths, baseline.Project))
        {
            token.ThrowIfCancellationRequested();
            try
            {
                var takeId = BackendFullTransfer.FinalTakeId(job.Id);
                var scratch = BackendFullTransfer.CompletedScratch(job);
                if (BackendTakeRemoval.Find(baseline.Project, job.Request.SourceId, takeId) is null &&
                    !baseline.Project.Sources.Any(source => source.Takes.Any(take => take.Id == takeId)))
                {
                    if (!BackendFullTransfer.HasPublishedOutput(job))
                    {
                        if (scratch is null) continue;
                        await BackendFullTransfer.PublishOutputAsync(job, scratch, token).ConfigureAwait(false);
                    }
                    var saved = await RegisterOutputAsync(job, baseline, token).ConfigureAwait(false);
                    if (saved.ErrorCode.Length == 0)
                        baseline = await store.OpenAsync(token).ConfigureAwait(false);
                }
                if (scratch is not null && BackendFullTransfer.HasPublishedOutput(job))
                    StudioReviewSession.ReleaseCompletedFullTransfer(scratch);
            }
            catch (ProjectRecoveryRequiredException) { throw; }
            catch (Exception error) when (error is IOException or UnauthorizedAccessException or JsonException or
                InvalidDataException or InvalidOperationException or KeyNotFoundException or Win32Exception)
            { Console.Error.WriteLine($"Studio Full Transfer: could not reconcile {job.Id}."); }
        }
        return baseline;
    }

    public async Task<BackendPreparationReply> FullTransferJobAsync(string sessionKey, string jobId, string? expectedRequestSha,
        long expectedRevision, string fingerprint, CancellationToken cancellationToken = default)
    {
        Authorize(sessionKey);
        using var scope = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, _mediaStopping.Token);
        scope.CancelAfter(TimeSpan.FromSeconds(42));
        var token = scope.Token;
        var admitted = false;
        try
        {
            await _gate.WaitAsync(token).ConfigureAwait(false);
            admitted = true;
            ObjectDisposedException.ThrowIf(_disposed, this);
            if (expectedRequestSha is not null) RequireWriter();
            var baseline = await ObserveAsync(expectedRevision, fingerprint, token).ConfigureAwait(false);
            if (baseline.SchemaVersion != 2) throw new ProjectMigrationRequiredException();
            var receipt = await BackendFullTransfer.OpenOrStageAsync(options, baseline, jobId, expectedRequestSha,
                async ct => { await ObserveAsync(expectedRevision, fingerprint, ct).ConfigureAwait(false); }, token).ConfigureAwait(false);
            return new() { ReceiptJson = JsonSerializer.Serialize(receipt, BackendAdaptation.Json) };
        }
        catch (WriterRequiredException error) { return Failure(error.Message); }
        catch (ScenarioEditException error) { return Failure(error.Code); }
        catch (ProjectRevisionConflictException) { return Failure("revision_conflict"); }
        catch (ProjectRecoveryRequiredException) { return Failure("recovery_required"); }
        catch (ProjectMigrationRequiredException) { return Failure("migration_required"); }
        catch (UnauthorizedAccessException) { return Failure("access_denied"); }
        catch (FileNotFoundException) { return Failure("transfer_missing"); }
        catch (DirectoryNotFoundException) { return Failure("transfer_missing"); }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested) { return Failure("transfer_timeout"); }
        catch (Exception error) when (error is InvalidDataException or JsonException or KeyNotFoundException or InvalidOperationException or ArgumentException)
        { return Failure("transfer_damaged"); }
        catch (IOException) { return Failure("project_io_error"); }
        finally { if (admitted) _gate.Release(); }

        static BackendPreparationReply Failure(string code) => new() { ErrorCode = code };
    }

    public async Task<BackendPreparationReply> PrepareFullTransferAsync(string sessionKey, string formJson, long expectedRevision,
        string fingerprint, CancellationToken cancellationToken = default)
    {
        Authorize(sessionKey);
        using var scope = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, _mediaStopping.Token);
        scope.CancelAfter(TimeSpan.FromSeconds(42));
        var token = scope.Token;
        var admitted = false;
        try
        {
            await _gate.WaitAsync(token).ConfigureAwait(false);
            admitted = true;
            ObjectDisposedException.ThrowIf(_disposed, this);
            RequireWriter();
            var baseline = await ObserveAsync(expectedRevision, fingerprint, token).ConfigureAwait(false);
            if (baseline.SchemaVersion != 2) throw new ProjectMigrationRequiredException();
            if (media is null) throw new ScenarioEditException("decoder_unavailable");
            FullTransferForm form;
            try { form = BackendFullTransfer.Decode(formJson); }
            catch (JsonException) { return Failure("invalid_transfer"); }
            var receipt = await BackendFullTransfer.PrepareAsync(options, baseline, form, media,
                async ct => { await ObserveAsync(expectedRevision, fingerprint, ct).ConfigureAwait(false); }, token).ConfigureAwait(false);
            return new() { ReceiptJson = JsonSerializer.Serialize(receipt, BackendAdaptation.Json) };
        }
        catch (WriterRequiredException error) { return Failure(error.Message); }
        catch (ScenarioEditException error) { return Failure(error.Code); }
        catch (ProjectRevisionConflictException) { return Failure("revision_conflict"); }
        catch (ProjectRecoveryRequiredException) { return Failure("recovery_required"); }
        catch (ProjectMigrationRequiredException) { return Failure("migration_required"); }
        catch (UnauthorizedAccessException) { return Failure("access_denied"); }
        catch (Win32Exception) { return Failure("decoder_unavailable"); }
        catch (Exception error) when (error is FileNotFoundException or DirectoryNotFoundException) { return Failure("transfer_missing"); }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested) { return Failure("transfer_timeout"); }
        catch (Exception error) when (error is InvalidDataException or JsonException or KeyNotFoundException or InvalidOperationException or ArgumentException or TimeoutException)
        { return Failure("transfer_damaged"); }
        catch (IOException) { return Failure("project_io_error"); }
        finally { if (admitted) _gate.Release(); }

        static BackendPreparationReply Failure(string code) => new() { ErrorCode = code };
    }
}
