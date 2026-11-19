using System.Text.Json;
using TrueMoon.Tales.Studio.Backend.Export;
using TrueMoon.Tales.Studio.Persistence;

namespace TrueMoon.Tales.Studio.Backend;

internal sealed partial class StudioBackendService
{
    private readonly BackendExportJobs _exports = new(options);

    public Task<BackendExportReply> WorkingReviewAsync(string sessionKey, string operation, string key, long revision,
        string fingerprint, int startFrame, int endFrame, CancellationToken cancellationToken = default) =>
        ExecuteExportAsync(sessionKey, write: false, token => operation switch
        {
            "start" => _exports.StartAsync(key, "draft", revision, fingerprint,
                ct => ExportBaselineAsync(revision, fingerprint, ct), token, working: true,
                range: startFrame == 0 && endFrame == 0 ? null : new(startFrame, endFrame)),
            "status" => _exports.StatusAsync(key, token, working: true),
            "cancel" => _exports.CancelAsync(key, revision, token, working: true),
            _ => throw new BackendExportException("invalid_export")
        }, cancellationToken);

    public Task<BackendExportReply> CheckExportAsync(string sessionKey, string profile, long expectedRevision,
        string fingerprint, CancellationToken cancellationToken = default) =>
        ExecuteExportAsync(sessionKey, write: false,
            token => _exports.CheckAsync(profile, ct => ExportBaselineAsync(expectedRevision, fingerprint, ct), token), cancellationToken);

    public Task<BackendExportReply> FindExportAsync(string sessionKey, string clientKey, CancellationToken cancellationToken = default) =>
        ExecuteExportAsync(sessionKey, write: false, token => _exports.StatusAsync(BackendExportJobs.JobId(clientKey), token), cancellationToken);

    private async Task<ProjectFileDocument> ExportBaselineAsync(long revision, string fingerprint, CancellationToken token)
    {
        var document = await ObserveAsync(revision, fingerprint, token).ConfigureAwait(false);
        if (document.SchemaVersion != 2) throw new ProjectMigrationRequiredException();
        return document;
    }

    public Task<BackendExportReply> StartExportAsync(string sessionKey, string clientKey, string profile, long expectedRevision,
        string fingerprint, CancellationToken cancellationToken = default) => ExecuteExportAsync(sessionKey, write: true,
            token => _exports.StartAsync(clientKey, profile, expectedRevision, fingerprint,
                ct => ExportBaselineAsync(expectedRevision, fingerprint, ct), token), cancellationToken);

    public Task<BackendExportReply> ExportStatusAsync(string sessionKey, string id, CancellationToken cancellationToken = default) =>
        ExecuteExportAsync(sessionKey, write: false, token => _exports.StatusAsync(id, token), cancellationToken);

    public Task<BackendExportReply> CancelExportAsync(string sessionKey, string id, long expectedJobRevision, CancellationToken cancellationToken = default) =>
        ExecuteExportAsync(sessionKey, write: true, token => _exports.CancelAsync(id, expectedJobRevision, token), cancellationToken);

    public Task<BackendExportReply> RecoverExportAsync(string sessionKey, string id, CancellationToken cancellationToken = default) =>
        ExecuteExportAsync(sessionKey, write: true, token => _exports.RecoverAsync(id, token), cancellationToken);

    public Task<BackendExportReply> ExportChunkAsync(string sessionKey, string id, string artifactSha256, long artifactOffset,
        CancellationToken cancellationToken = default) =>
        ExecuteExportAsync(sessionKey, write: false, token => _exports.ChunkAsync(id, artifactSha256, artifactOffset, token), cancellationToken);

    private async Task<BackendExportReply> ExecuteExportAsync(string key, bool write,
        Func<CancellationToken, Task<BackendExportReply>> execute, CancellationToken token)
    {
        Authorize(key);
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(token);
        deadline.CancelAfter(TimeSpan.FromSeconds(10));
        var acquired = false;
        try
        {
            if (write)
            {
                await _gate.WaitAsync(deadline.Token).ConfigureAwait(false);
                acquired = true;
                RequireWriter();
            }
            ObjectDisposedException.ThrowIf(_disposed, this);
            return await execute(deadline.Token).ConfigureAwait(false);
        }
        catch (BackendExportException error) { return new() { ErrorCode = error.Code }; }
        catch (BackendReadinessException error) { return new() { ErrorCode = error.ExportCode, JobJson = error.Issue.Encode() }; }
        catch (InvalidDataException error) when (error.InnerException is DisabledLegacyOverlapException)
        { return new() { ErrorCode = "invalid_export", JobJson = new BackendReadinessIssue("legacy_root_overlap", 0, null).Encode() }; }
        catch (WriterRequiredException error) { return new() { ErrorCode = error.Message }; }
        catch (ProjectRevisionConflictException) { return new() { ErrorCode = "revision_conflict" }; }
        catch (ProjectMigrationRequiredException) { return new() { ErrorCode = "migration_required" }; }
        catch (ProjectRecoveryRequiredException) { return new() { ErrorCode = "recovery_required" }; }
        catch (UnauthorizedAccessException) { return new() { ErrorCode = "access_denied" }; }
        catch (OperationCanceledException) when (!token.IsCancellationRequested) { return new() { ErrorCode = "export_timeout" }; }
        catch (Exception error) when (error is JsonException or InvalidDataException or ArgumentException or FormatException or KeyNotFoundException or OverflowException)
        { return new() { ErrorCode = "invalid_export" }; }
        catch (Exception error) when (error is IOException or InvalidOperationException or NotSupportedException or System.ComponentModel.Win32Exception)
        { return new() { ErrorCode = "export_unavailable" }; }
        finally { if (acquired) _gate.Release(); }
    }
}
