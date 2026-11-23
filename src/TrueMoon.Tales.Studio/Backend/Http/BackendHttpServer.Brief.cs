using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Http.Features;

namespace TrueMoon.Tales.Studio.Backend.Http;

internal sealed partial class BackendHttpServer
{
    internal const string BriefRoute = "/api/backend/v1/brief";

    private async Task HandleBriefAsync(HttpContext context, CancellationToken token)
    {
        if (HttpMethods.IsGet(context.Request.Method))
        {
            await ReplyAgentRequestAsync(context, await backend.ReadBriefAsync(token).ConfigureAwait(false), token).ConfigureAwait(false);
            return;
        }
        if (!HttpMethods.IsPatch(context.Request.Method)) { await Error(context, 405, "method_not_allowed"); return; }
        if (!context.Request.HasJsonContentType()) { await Error(context, 415, "json_required"); return; }
        if (context.Request.ContentLength > BackendBrief.MaxBodyBytes)
        { await Error(context, 413, "brief_input_too_large"); return; }
        if (context.Features.Get<IHttpMaxRequestBodySizeFeature>() is { IsReadOnly: false } limit)
            limit.MaxRequestBodySize = BackendBrief.MaxBodyBytes;
        var write = await context.Request.ReadFromJsonAsync<BriefWrite>(AgentRequestDraft.Json, token).ConfigureAwait(false);
        if (write is not { Valid: true }) { await Error(context, 422, "invalid_brief_patch"); return; }
        await ReplyAgentRequestAsync(context, await backend.UpdateBriefAsync(write, token).ConfigureAwait(false), token).ConfigureAwait(false);
    }
}
