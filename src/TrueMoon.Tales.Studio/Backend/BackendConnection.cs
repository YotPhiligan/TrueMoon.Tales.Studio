using TrueMoon.Tales.Studio.Contracts.Projects;
using TrueMoon.Tales.Studio.Contracts.Serialization;
using System.Text;
using TrueMoon.Enerit;

namespace TrueMoon.Tales.Studio.Backend;

internal sealed record OpenedBackendProject(BackendIdentity Identity, int SchemaVersion, string Fingerprint, ProjectContract Project, bool ScenarioActive = false);

internal interface IBackendProjectReader
{
    Task<OpenedBackendProject> OpenAsync(CancellationToken cancellationToken = default);
}

internal sealed partial class BackendConnection(IStudioBackend service, BackendOptions options, BackendProcessFactory processes) : IBackendProjectReader
{
    internal Task<OpenedBackendProject> AcquireWriterAsync(OpenedBackendProject baseline, CancellationToken token = default) =>
        CommandAsync(ct => service.AcquireWriterAsync(options.SessionKey, baseline.Project.Revision, baseline.Fingerprint, ct), token);
    internal Task<OpenedBackendProject> MigrateAsync(OpenedBackendProject baseline, CancellationToken token = default) =>
        CommandAsync(ct => service.MigrateProjectAsync(options.SessionKey, baseline.Project.Revision, baseline.Fingerprint, ct), token);
    internal Task<OpenedBackendProject> RecoverAsync(CancellationToken token = default) =>
        CommandAsync(ct => service.RecoverProjectAsync(options.SessionKey, ct), token);
    internal Task<OpenedBackendProject> MoveAsync(string clipId, int start, long revision, string fingerprint, CancellationToken token = default) =>
        CommandAsync(ct => service.MoveClipAsync(options.SessionKey, clipId, start, revision, fingerprint, ct), token);

    private async Task<OpenedBackendProject> CommandAsync(Func<CancellationToken, Task<BackendProjectReply>> send, CancellationToken token)
    {
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(token);
        deadline.CancelAfter(TimeSpan.FromSeconds(12));
        var identity = await TransportAsync(() => service.HelloAsync(options.SessionKey, deadline.Token)).ConfigureAwait(false);
        ValidateIdentity(identity, processes.Owned?.IsServing(identity.ProcessId) == true);
        // Exactly one invocation. A timeout or disconnected reply never causes write replay.
        var reply = await TransportAsync(() => send(deadline.Token)).ConfigureAwait(false);
        if (reply is { ErrorCode.Length: > 0 }) throw new BackendCommandException(reply.ErrorCode);
        return Decode(identity, reply);
    }
    public async Task<OpenedBackendProject> OpenAsync(CancellationToken cancellationToken = default)
    {
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        deadline.CancelAfter(TimeSpan.FromSeconds(12));
        var identity = await WaitForReadyAsync(deadline.Token).ConfigureAwait(false);
        ValidateIdentity(identity, processes.Owned?.IsServing(identity.ProcessId) == true);
        // Opening is a single call. Reconnect is a new explicit read, never a replay of an operation.
        var reply = await TransportAsync(() => service.OpenProjectAsync(options.SessionKey, deadline.Token)).ConfigureAwait(false);
        if (reply is { ErrorCode.Length: > 0 }) throw new BackendCommandException(reply.ErrorCode);
        return Decode(identity, reply);
    }

    private async Task<BackendIdentity> WaitForReadyAsync(CancellationToken token)
    {
        while (true)
        {
            token.ThrowIfCancellationRequested();
            if (processes.Owned is { IsAlive: false }) throw new IOException("Backend process exited. Open the project in a new Studio instance.");
            try { return await service.HelloAsync(options.SessionKey, token).ConfigureAwait(false); }
            catch (InvocationException error) { throw new IOException("Backend readiness failed; open a new session.", error); }
            catch (Exception error) when (!token.IsCancellationRequested && error is IOException or TimeoutException or OperationCanceledException)
            { await Task.Delay(100, token).ConfigureAwait(false); }
        }
    }

    private static async Task<T> TransportAsync<T>(Func<Task<T>> call)
    {
        try { return await call().ConfigureAwait(false); }
        catch (Exception error) when (error is InvocationException or TimeoutException)
        { throw new IOException("Связь с backend потеряна. Отмените черновик и перечитайте проект для сверки результата.", error); }
    }

    internal static void ValidateIdentity(BackendIdentity identity, bool owned)
    {
        if (identity is null || !owned || identity.Role != "backend" || identity.ProtocolVersion != StudioBackendService.ProtocolVersion ||
            identity.ProjectContractVersion != 1 || identity.ProductVersion != StudioBackendService.ProductVersion ||
            identity.ParentProcessId != Environment.ProcessId)
            throw new InvalidDataException("Backend identity or contract version mismatch.");
    }

    internal static OpenedBackendProject Decode(BackendIdentity identity, BackendProjectReply reply)
    {
        if (reply is null || reply.ErrorCode is null || reply.Fingerprint is null || reply.ProjectJson is null)
            throw new InvalidDataException("Null backend project envelope.");
        if (reply.ErrorCode.Length != 0) throw new InvalidDataException($"Открытие проекта: {reply.ErrorCode}.");
        if (reply.SchemaVersion is not (1 or 2) || reply.Fingerprint.Length != 64 || !reply.Fingerprint.All(Uri.IsHexDigit))
            throw new InvalidDataException("Invalid backend project envelope.");
        if (Encoding.Unicode.GetByteCount(reply.ProjectJson) > StudioBackendService.MaxProjectBytes)
            throw new InvalidDataException("Backend project exceeds the negotiated payload limit.");
        return new(identity, reply.SchemaVersion, reply.Fingerprint, ProjectContractJson.Deserialize(reply.ProjectJson), reply.ScenarioActive);
    }
}

internal sealed class BackendCommandException(string code) : IOException(code == "revision_conflict"
    ? "Конфликт ревизии или содержимого. Отмените черновик и перечитайте проект."
    : $"Backend: {code}. Операция не повторяется автоматически.")
{
    internal string Code { get; } = code;
}
