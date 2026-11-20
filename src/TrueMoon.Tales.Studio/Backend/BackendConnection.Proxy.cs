using System.Text.Json;

namespace TrueMoon.Tales.Studio.Backend;

internal sealed partial class BackendConnection
{
    internal async Task<ProxyStatus> TakeProxyAsync(string operation, string source, string take, string asset,
        OpenedBackendProject baseline, CancellationToken token)
    {
        var reply = await SendExportAsync(ct => service.TakeProxyAsync(options.SessionKey, operation, source, take,
            baseline.Project.Revision, baseline.Fingerprint, ct), token).ConfigureAwait(false);
        if (reply.Data.Length != 0 || reply.JobJson.Length > 4096 ||
            JsonSerializer.Deserialize<ProxyStatus>(reply.JobJson, BackendAdaptation.Json) is not { } status ||
            status.AssetId != asset ||
            status.State is not ("running" or "ready" or "original" or "cancelled" or "failed") ||
            status.State == "ready" && status.Receipt is not { Enabled: true } ||
            status.Receipt is { } receipt && !receipt.Valid(asset))
            throw new InvalidDataException("Invalid proxy response.");
        return status;
    }
}
