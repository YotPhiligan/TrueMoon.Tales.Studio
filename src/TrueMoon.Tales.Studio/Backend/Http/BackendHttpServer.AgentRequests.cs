using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Http.Features;

namespace TrueMoon.Tales.Studio.Backend.Http;

internal sealed partial class BackendHttpServer
{
    internal const string AgentRequestsRoute = "/api/backend/v1/agent-requests";
    internal const string BreakdownSuffix = "/breakdown";

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
        var post = HttpMethods.IsPost(context.Request.Method);
        if (!post && !HttpMethods.IsGet(context.Request.Method)) { await Error(context, 405, "method_not_allowed"); return; }
        var id = path == AgentRequestsRoute ? null : path[(AgentRequestsRoute.Length + 1)..];
        var breakdown = id?.EndsWith(BreakdownSuffix, StringComparison.Ordinal) == true;
        if (breakdown) id = id![..^BreakdownSuffix.Length];
        if (id is not null && !BackendAgentRequests.ValidId(id))
        { await Error(context, 422, "invalid_agent_request_id"); return; }

        if (breakdown)
        {
            await HandleBreakdownAsync(context, id!, post, token).ConfigureAwait(false);
            return;
        }
        if (post)
        {
            if (!context.Request.HasJsonContentType()) { await Error(context, 415, "json_required"); return; }
            if (context.Request.ContentLength > AgentRequestUpdate.MaxBodyBytes)
            { await Error(context, 413, "agent_request_input_too_large"); return; }
            if (context.Features.Get<IHttpMaxRequestBodySizeFeature>() is { IsReadOnly: false } limit)
                limit.MaxRequestBodySize = AgentRequestUpdate.MaxBodyBytes;
            var update = await context.Request.ReadFromJsonAsync<AgentRequestUpdate>(AgentRequestDraft.Json, token).ConfigureAwait(false);
            if (update is not { Valid: true }) { await Error(context, 422, "invalid_agent_request_update"); return; }
            await ReplyAgentRequestAsync(context, await backend.UpdateAgentRequestAsync(id!, update, token).ConfigureAwait(false), token).ConfigureAwait(false);
            return;
        }
        await ReplyAgentRequestAsync(context, await backend.ReadAgentRequestsAsync(id, token).ConfigureAwait(false), token).ConfigureAwait(false);
    }

    internal sealed record ApplyBreakdownBody
    {
        public required long ExpectedRevision { get; init; }
        public required string Fingerprint { get; init; }
        public string Actor { get; init; } = "author";
        internal bool Valid => ExpectedRevision is >= 0 and < int.MaxValue && BackendConnection.IsHash(Fingerprint) && AgentRequestDraft.ValidActor(Actor);
    }

    private async Task HandleBreakdownAsync(HttpContext context, string id, bool post, CancellationToken token)
    {
        if (!post)
        {
            await ReplyAgentRequestAsync(context, await backend.PreviewAgentBreakdownAsync(id, token).ConfigureAwait(false), token).ConfigureAwait(false);
            return;
        }
        if (!context.Request.HasJsonContentType()) { await Error(context, 415, "json_required"); return; }
        var body = await context.Request.ReadFromJsonAsync<ApplyBreakdownBody>(AgentRequestDraft.Json, token).ConfigureAwait(false);
        if (body is not { Valid: true }) { await Error(context, 422, "invalid_agent_breakdown"); return; }
        var saved = await backend.ApplyAgentBreakdownAsync(id, body.ExpectedRevision, body.Fingerprint, body.Actor, token).ConfigureAwait(false);
        await context.Response.WriteAsJsonAsync(BackendHttpSnapshot.From(saved), Json, token).ConfigureAwait(false);
    }

    private static async Task ReplyAgentRequestAsync(HttpContext context, byte[] json, CancellationToken token)
    {
        context.Response.ContentType = "application/json; charset=utf-8";
        context.Response.ContentLength = json.Length;
        await context.Response.Body.WriteAsync(json, token).ConfigureAwait(false);
    }
}
