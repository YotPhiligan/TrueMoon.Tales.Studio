using System.Text.Json;

namespace TrueMoon.Tales.Studio.Backend;

internal sealed partial class BackendConnection
{
    internal async Task<byte[]> ReadAgentRequestsAsync(string? id, CancellationToken token)
    {
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(token);
        deadline.CancelAfter(TimeSpan.FromSeconds(12));
        var identity = await TransportAsync(() => service.HelloAsync(options.SessionKey, deadline.Token)).ConfigureAwait(false);
        ValidateIdentity(identity, processes.Owned?.IsServing(identity.ProcessId) == true);
        var reply = await TransportAsync(() => service.ReadAgentRequestsAsync(options.SessionKey, id ?? "", deadline.Token)).ConfigureAwait(false);
        if (reply?.ErrorCode is null) throw new InvalidDataException("Invalid agent requests IPC response.");
        if (reply.ErrorCode.Length > 0) throw new BackendCommandException(reply.ErrorCode);
        if (reply.Json.Length is < 1 or > BackendAgentRequests.MaxResponseBytes)
            throw new InvalidDataException("Invalid agent requests payload size.");
        try
        {
            using var document = JsonDocument.Parse(reply.Json, new JsonDocumentOptions { AllowDuplicateProperties = false });
            BackendAgentRequests.ValidateResponse(document.RootElement, id);
        }
        catch (JsonException error) { throw new InvalidDataException("Invalid agent requests payload.", error); }
        return reply.Json;
    }
}
