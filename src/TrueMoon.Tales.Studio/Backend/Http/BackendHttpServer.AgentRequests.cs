using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Http.Features;

namespace TrueMoon.Tales.Studio.Backend.Http;

internal sealed partial class BackendHttpServer
{
    internal const string AgentRequestsRoute = "/api/backend/v1/agent-requests";

    private static bool IsAgentRequestsRoute(string? path) =>
        path == AgentRequestsRoute || path?.StartsWith(AgentRequestsRoute + "/", StringComparison.Ordinal) == true;

    private async Task HandleAgentRequestsAsync(HttpContext context, string path, CancellationToken token)
    {
        if (path == AgentRequestsRoute && HttpMethods.IsPost(context.Request.Method))
        {
            if (!context.Request.HasJsonContentType()) { await Error(context, 415, "json_required"); return; }
            if (context.Request.ContentLength > AgentRequestDraft.MaxBodyBytes)
            { await Error(context, 413, "agent_request_input_too_large"); return; }
            if (context.Features.Get<IHttpMaxRequestBodySizeFeature>() is { IsReadOnly: false } limit)
                limit.MaxRequestBodySize = AgentRequestDraft.MaxBodyBytes;
            var draft = await context.Request.ReadFromJsonAsync<AgentRequestDraft>(AgentRequestDraft.Json, token).ConfigureAwait(false);
            if (draft is not { Valid: true }) { await Error(context, 422, "invalid_agent_request"); return; }
            await ReplyAgentRequestAsync(context, await backend.CreateAgentRequestAsync(draft, token).ConfigureAwait(false), token).ConfigureAwait(false);
            return;
        }
        if (!HttpMethods.IsGet(context.Request.Method)) { await Error(context, 405, "method_not_allowed"); return; }
        var id = path == AgentRequestsRoute ? null : path[(AgentRequestsRoute.Length + 1)..];
        if (id is not null && !BackendAgentRequests.ValidId(id))
        { await Error(context, 422, "invalid_agent_request_id"); return; }

        await ReplyAgentRequestAsync(context, await backend.ReadAgentRequestsAsync(id, token).ConfigureAwait(false), token).ConfigureAwait(false);
    }

    private static async Task ReplyAgentRequestAsync(HttpContext context, byte[] json, CancellationToken token)
    {
        context.Response.ContentType = "application/json; charset=utf-8";
        context.Response.ContentLength = json.Length;
        await context.Response.Body.WriteAsync(json, token).ConfigureAwait(false);
    }
}
