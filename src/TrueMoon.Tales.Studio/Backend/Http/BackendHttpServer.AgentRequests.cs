using Microsoft.AspNetCore.Http;

namespace TrueMoon.Tales.Studio.Backend.Http;

internal sealed partial class BackendHttpServer
{
    internal const string AgentRequestsRoute = "/api/backend/v1/agent-requests";

    private static bool IsAgentRequestsRoute(string? path) =>
        path == AgentRequestsRoute || path?.StartsWith(AgentRequestsRoute + "/", StringComparison.Ordinal) == true;

    private async Task HandleAgentRequestsAsync(HttpContext context, string path, CancellationToken token)
    {
        if (!HttpMethods.IsGet(context.Request.Method)) { await Error(context, 405, "method_not_allowed"); return; }
        var id = path == AgentRequestsRoute ? null : path[(AgentRequestsRoute.Length + 1)..];
        if (id is not null && !BackendAgentRequests.ValidId(id))
        { await Error(context, 422, "invalid_agent_request_id"); return; }

        var json = await backend.ReadAgentRequestsAsync(id, token).ConfigureAwait(false);
        context.Response.ContentType = "application/json; charset=utf-8";
        context.Response.ContentLength = json.Length;
        await context.Response.Body.WriteAsync(json, token).ConfigureAwait(false);
    }
}
