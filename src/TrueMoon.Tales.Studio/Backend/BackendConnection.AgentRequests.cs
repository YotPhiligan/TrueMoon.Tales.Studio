using System.Text.Json;

namespace TrueMoon.Tales.Studio.Backend;

internal sealed partial class BackendConnection
{
    internal Task<byte[]> ReadAgentRequestsAsync(string? id, CancellationToken token) =>
        SendAgentRequestAsync(ct => service.ReadAgentRequestsAsync(options.SessionKey, id ?? "", ct),
            response => BackendAgentRequests.ValidateResponse(response, id), token);

    internal Task<byte[]> CreateAgentRequestAsync(AgentRequestDraft draft, CancellationToken token)
    {
        var json = JsonSerializer.Serialize(draft, AgentRequestDraft.Json);
        if (json.Length > AgentRequestDraft.MaxIpcChars) throw new BackendCommandException("agent_request_input_too_large");
        return SendAgentRequestAsync(ct => service.CreateAgentRequestAsync(options.SessionKey, json, ct),
            response => BackendAgentRequests.ValidateReceipt(response, draft), token);
    }

    internal Task<byte[]> UpdateAgentRequestAsync(string id, AgentRequestUpdate update, CancellationToken token)
    {
        var json = JsonSerializer.SerializeToUtf8Bytes(update, AgentRequestDraft.Json);
        if (json.Length > AgentRequestUpdate.MaxIpcBytes) throw new BackendCommandException("agent_request_input_too_large");
        return SendAgentRequestAsync(ct => service.UpdateAgentRequestAsync(options.SessionKey, id, json, ct),
            response => BackendAgentRequests.ValidateUpdateReceipt(response, id, update), token);
    }

    private async Task<byte[]> SendAgentRequestAsync(Func<CancellationToken, Task<BackendAgentRequestsReply>> send,
        Action<JsonElement> validate, CancellationToken token)
    {
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(token);
        deadline.CancelAfter(TimeSpan.FromSeconds(12));
        var identity = await TransportAsync(() => service.HelloAsync(options.SessionKey, deadline.Token)).ConfigureAwait(false);
        ValidateIdentity(identity, processes.Owned?.IsServing(identity.ProcessId) == true);
        var reply = await TransportAsync(() => send(deadline.Token)).ConfigureAwait(false);
        if (reply?.ErrorCode is null) throw new InvalidDataException("Invalid agent requests IPC response.");
        if (reply.ErrorCode.Length > 0) throw new BackendCommandException(reply.ErrorCode);
        if (reply.Json.Length is < 1 or > BackendAgentRequests.MaxResponseBytes)
            throw new InvalidDataException("Invalid agent requests payload size.");
        try
        {
            using var document = JsonDocument.Parse(reply.Json, new JsonDocumentOptions { AllowDuplicateProperties = false });
            validate(document.RootElement);
        }
        catch (JsonException error) { throw new InvalidDataException("Invalid agent requests payload.", error); }
        return reply.Json;
    }
}
