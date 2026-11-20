using System.Text.Json;
using TrueMoon.Tales.Studio.Backend.Export;
using TrueMoon.Tales.Studio.Persistence;

namespace TrueMoon.Tales.Studio.Backend;

internal sealed partial class StudioBackendService
{
    public async Task<BackendExportReply> CompareJobSnapshotsAsync(string sessionKey, string beforeId, string afterId,
        long expectedRevision, string fingerprint, CancellationToken cancellationToken = default)
    {
        Authorize(sessionKey);
        if (beforeId == afterId || !BackendFullTransfer.ValidSnapshotId(beforeId) || !BackendFullTransfer.ValidSnapshotId(afterId))
            return Failure("job_snapshot_ids");
        try
        {
            var current = await ObserveAsync(expectedRevision, fingerprint, cancellationToken).ConfigureAwait(false);
            if (current.SchemaVersion != 2) return Failure("migration_required");
            var project = BackendProjectAccess.Read(options);
            var before = BackendFullTransfer.ReadSnapshot(project.ProjectDirectory, beforeId, current.Project.Id, expectedRevision);
            cancellationToken.ThrowIfCancellationRequested();
            var after = BackendFullTransfer.ReadSnapshot(project.ProjectDirectory, afterId, current.Project.Id, expectedRevision);
            await ObserveAsync(expectedRevision, fingerprint, cancellationToken).ConfigureAwait(false);
            var json = JsonSerializer.Serialize(new JobSnapshotComparison(before, after), BackendAdaptation.Json);
            return json.Length <= MaxProjectBytes / 2 ? new() { JobJson = json } : Failure("job_snapshot_invalid");
        }
        catch (ProjectRevisionConflictException) { return Failure("revision_conflict"); }
        catch (ProjectRecoveryRequiredException) { return Failure("recovery_required"); }
        catch (Exception error) when (error is FileNotFoundException or DirectoryNotFoundException)
        { return Failure("job_snapshot_missing"); }
        catch (UnauthorizedAccessException) { return Failure("access_denied"); }
        catch (Exception error) when (error is IOException or InvalidDataException or JsonException or ScenarioEditException or
            InvalidOperationException or ArgumentException or KeyNotFoundException or FormatException)
        { return Failure("job_snapshot_invalid"); }

        static BackendExportReply Failure(string code) => new() { ErrorCode = code };
    }

    public async Task<BackendExportReply> CheckDependenciesAsync(string sessionKey, long comparedRevision, long expectedRevision,
        string fingerprint, CancellationToken cancellationToken = default)
    {
        Authorize(sessionKey);
        if (comparedRevision < 0 || comparedRevision >= expectedRevision) return Failure("invalid_dependency_revision");
        try
        {
            var current = await ObserveAsync(expectedRevision, fingerprint, cancellationToken).ConfigureAwait(false);
            if (current.SchemaVersion != 2) return Failure("migration_required");
            var previous = await store.ReadHistoryAsync(current.Project.Id, comparedRevision, cancellationToken).ConfigureAwait(false);
            if (previous.SchemaVersion != 2) return Failure("dependency_schema");
            var report = BackendDependencies.Compare(previous, current,
                () => BackendExportJobs.ReadCompletedAt(options, previous, cancellationToken), cancellationToken);
            await ObserveAsync(expectedRevision, fingerprint, cancellationToken).ConfigureAwait(false);
            return new() { JobJson = report.Encode() };
        }
        catch (ProjectRevisionConflictException) { return Failure("revision_conflict"); }
        catch (ProjectRecoveryRequiredException) { return Failure("recovery_required"); }
        catch (Exception error) when (error is FileNotFoundException or DirectoryNotFoundException)
        { return Failure("dependency_history_missing"); }
        catch (UnauthorizedAccessException) { return Failure("access_denied"); }
        catch (Exception error) when (error is IOException or InvalidDataException or JsonException or ScenarioEditException or
            InvalidOperationException or ArgumentException or KeyNotFoundException)
        { return Failure("dependency_invalid"); }

        static BackendExportReply Failure(string code) => new() { ErrorCode = code };
    }
}
