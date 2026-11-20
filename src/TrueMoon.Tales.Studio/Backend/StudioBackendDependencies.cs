using System.Text.Json;
using TrueMoon.Tales.Studio.Backend.Export;
using TrueMoon.Tales.Studio.Persistence;

namespace TrueMoon.Tales.Studio.Backend;

internal sealed partial class StudioBackendService
{
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
