using System.Text.Json;
using TrueMoon.Tales.Studio.Persistence;

namespace TrueMoon.Tales.Studio.Backend;

internal sealed partial class StudioBackendService
{
    public async Task<BackendAgentContextReply> ReadAgentContextAsync(string sessionKey, string block, CancellationToken cancellationToken = default)
    {
        Authorize(sessionKey);
        var selector = string.IsNullOrEmpty(block) ? null : block;
        if (!BackendAgentContext.ValidSelector(selector)) return Failure("invalid_agent_context");
        try
        {
            var current = await store.OpenAsync(cancellationToken).ConfigureAwait(false);
            if (current.SchemaVersion != 2) return Failure("migration_required");
            return await BackendAgentContext.ReadAsync(current, options, selector, cancellationToken).ConfigureAwait(false);
        }
        catch (BackendCommandException error) { return Failure(error.Code); }
        catch (ProjectRecoveryRequiredException) { return Failure("recovery_required"); }
        catch (ProjectFileConflictException) { return Failure("project_file_conflict"); }
        catch (UnauthorizedAccessException) { return Failure("access_denied"); }
        catch (Exception error) when (error is FileNotFoundException or DirectoryNotFoundException) { return Failure("project_not_found"); }
        catch (Exception error) when (error is JsonException or InvalidDataException or InvalidOperationException or ArgumentException or NotSupportedException)
        { return Failure("invalid_project"); }
        catch (IOException) { return Failure("project_io_error"); }

        static BackendAgentContextReply Failure(string code) => new() { ErrorCode = code };
    }
}
