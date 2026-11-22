using System.Text.Json;
using TrueMoon.Tales.Studio.Persistence;

namespace TrueMoon.Tales.Studio.Backend;

internal sealed partial class StudioBackendService
{
    public async Task<BackendAgentRequestsReply> ReadAgentRequestsAsync(string sessionKey, string requestId, CancellationToken cancellationToken = default)
    {
        Authorize(sessionKey);
        if (requestId is null) return Failure("invalid_agent_request_id");
        var id = requestId.Length == 0 ? null : requestId;
        if (id is not null && !BackendAgentRequests.ValidId(id)) return Failure("invalid_agent_request_id");
        ProjectFileDocument current;
        try
        {
            current = await store.OpenAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (ProjectRecoveryRequiredException) { return Failure("recovery_required"); }
        catch (ProjectFileConflictException) { return Failure("project_file_conflict"); }
        catch (UnauthorizedAccessException) { return Failure("access_denied"); }
        catch (Exception error) when (error is FileNotFoundException or DirectoryNotFoundException) { return Failure("project_not_found"); }
        catch (Exception error) when (error is JsonException or InvalidDataException or InvalidOperationException or ArgumentException or NotSupportedException)
        { return Failure("invalid_project"); }
        catch (IOException) { return Failure("project_io_error"); }
        if (current.SchemaVersion != 2) return Failure("migration_required");
        try
        {
            var paths = BackendProjectAccess.Read(options);
            return new() { Json = await BackendAgentRequests.ReadAsync(paths, current.Project.Id, id, cancellationToken).ConfigureAwait(false) };
        }
        catch (BackendCommandException error) { return Failure(error.Code); }
        catch (UnauthorizedAccessException) { return Failure("access_denied"); }
        catch (Exception error) when (error is JsonException or InvalidOperationException or ArgumentException or InvalidDataException)
        { return Failure("invalid_agent_request_record"); }
        catch (IOException) { return Failure("agent_request_io_error"); }

        static BackendAgentRequestsReply Failure(string code) => new() { ErrorCode = code };
    }
}
