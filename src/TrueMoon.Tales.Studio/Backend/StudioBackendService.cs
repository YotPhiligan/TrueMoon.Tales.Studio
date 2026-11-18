using System.Reflection;
using System.Text;
using System.Text.Json;
using TrueMoon.Mithril;
using TrueMoon.Tales.Studio.Contracts.Serialization;
using TrueMoon.Tales.Studio.Persistence;

namespace TrueMoon.Tales.Studio.Backend;

internal sealed partial class StudioBackendService(BackendOptions options, ProjectStore store, UnitLaunchSettings launch, BackendMediaReader? media = null) : IStudioBackend, IDisposable
{
    internal const int ProtocolVersion = 6;
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
            var document = await store.OpenAsync(cancellationToken).ConfigureAwait(false);
            var json = ProjectContractJson.Serialize(document.Project);
            // Enerit's string codec uses UTF-16. Reserve space below its 1 MiB frame limit for the envelope.
            if (Encoding.Unicode.GetByteCount(json) > MaxProjectBytes) return Failure("project_too_large");
            return new() { SchemaVersion = document.SchemaVersion, Fingerprint = document.Fingerprint, ProjectJson = json, ScenarioActive = ScenarioActive(document) };
        }
        catch (ProjectRecoveryRequiredException) { return Failure("recovery_required"); }
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
