using System.Reflection;
using System.Text.Json;
using TrueMoon.Mithril;
using TrueMoon.Tales.Studio.Persistence;

namespace TrueMoon.Tales.Studio.Backend;

internal sealed partial class StudioBackendService(BackendOptions options, ProjectStore store, UnitLaunchSettings launch,
    BackendMediaReader? media = null, BackendProxyStore? proxies = null) : IStudioBackend, IDisposable
{
    internal const int ProtocolVersion = 35;
    internal const int MaxProjectBytes = 768 * 1024;
    internal static string ProductVersion => typeof(StudioBackendService).Assembly
        .GetCustomAttribute<AssemblyInformationalVersionAttribute>()!.InformationalVersion.Split('+')[0];

    public Task<BackendIdentity> HelloAsync(string sessionKey, CancellationToken cancellationToken = default)
    {
        Authorize(sessionKey);
        cancellationToken.ThrowIfCancellationRequested();
        return Task.FromResult(new BackendIdentity
        {
            Role = "backend", ProtocolVersion = ProtocolVersion, ProjectContractVersion = 1,
            ProductVersion = ProductVersion, ProcessId = Environment.ProcessId, ParentProcessId = launch.ParentProcessId ?? 0
        });
    }

    public async Task<BackendProjectReply> OpenProjectAsync(string sessionKey, CancellationToken cancellationToken = default)
    {
        Authorize(sessionKey);
        try
        {
            return Reply(await store.OpenAsync(cancellationToken).ConfigureAwait(false));
        }
        catch (ProjectRecoveryRequiredException) { return Failure("recovery_required"); }
        catch (ProjectFileConflictException) { return Failure("project_file_conflict"); }
        catch (UnauthorizedAccessException) { return Failure("access_denied"); }
        catch (FileNotFoundException) { return Failure("project_not_found"); }
        catch (DirectoryNotFoundException) { return Failure("project_not_found"); }
        catch (Exception error) when (error is JsonException or InvalidDataException or InvalidOperationException or ArgumentException)
        { return Failure("invalid_project"); }
        catch (IOException) { return Failure("project_io_error"); }
    }

    private void Authorize(string key)
    {
        if (!string.Equals(key, options.SessionKey, StringComparison.Ordinal)) throw new UnauthorizedAccessException("Invalid backend session.");
    }

    private static BackendProjectReply Failure(string code) => new() { ErrorCode = code };
}
