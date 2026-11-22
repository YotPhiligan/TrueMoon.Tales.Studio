using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.AspNetCore.Http;

namespace TrueMoon.Tales.Studio.Backend.Http;

internal sealed partial class BackendHttpServer
{
    internal const string AgentContextRoute = "/api/backend/v1/agent-context";
    private static readonly JsonSerializerOptions AgentContextJson = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower,
        UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow,
        AllowDuplicateProperties = false
    };

    internal sealed record AgentContextBody(string? Block);

    private async Task HandleAgentContextAsync(HttpContext context, CancellationToken token)
    {
        if (!HttpMethods.IsPost(context.Request.Method)) { await Error(context, 405, "method_not_allowed"); return; }
        if (!context.Request.HasJsonContentType()) { await Error(context, 415, "json_required"); return; }
        var request = await context.Request.ReadFromJsonAsync<AgentContextBody>(AgentContextJson, token).ConfigureAwait(false);
        if (request is null || !BackendAgentContext.ValidSelector(request.Block)) { await Error(context, 400, "invalid_request"); return; }
        var result = await backend.ReadAgentContextAsync(request.Block, token).ConfigureAwait(false);
        context.Response.ContentType = "application/json; charset=utf-8";
        using var writer = new Utf8JsonWriter(context.Response.BodyWriter);
        writer.WriteStartObject();
        writer.WriteNumber("api_version", 1);
        writer.WriteString("snapshot_sha256", result.SnapshotSha256);
        writer.WritePropertyName("context");
        writer.WriteRawValue(result.ContextJson, skipInputValidation: true);
        writer.WriteEndObject();
        writer.Flush();
        await context.Response.BodyWriter.FlushAsync(token).ConfigureAwait(false);
    }
}
