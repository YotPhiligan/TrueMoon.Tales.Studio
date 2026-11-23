using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Http.Features;

namespace TrueMoon.Tales.Studio.Backend.Http;

internal sealed partial class BackendHttpServer
{
    internal const string ScenarioActionRoute = "/api/backend/v1/scenario-action";
    internal const string ScenarioSegmentRoute = "/api/backend/v1/scenario-segment";
    internal const string ScenarioVideoRoute = "/api/backend/v1/scenario-video";

    private async Task HandleScenarioActionAsync(HttpContext context, CancellationToken token)
    {
        if (!HttpMethods.IsPost(context.Request.Method)) { await Error(context, 405, "method_not_allowed"); return; }
        if (!context.Request.HasJsonContentType()) { await Error(context, 415, "json_required"); return; }
        if (context.Request.ContentLength > ScenarioActionWrite.MaxBodyBytes)
        { await Error(context, 413, "invalid_request"); return; }
        if (context.Features.Get<IHttpMaxRequestBodySizeFeature>() is { IsReadOnly: false } limit)
            limit.MaxRequestBodySize = ScenarioActionWrite.MaxBodyBytes;
        var write = await context.Request.ReadFromJsonAsync<ScenarioActionWrite>(AgentRequestDraft.Json, token).ConfigureAwait(false);
        if (write is not { Valid: true }) { await Error(context, 422, "invalid_scenario_edit"); return; }
        var route = write.Video is not null ? ScenarioVideoRoute : write.Segment is not null ? ScenarioSegmentRoute : ScenarioActionRoute;
        if (context.Request.Path != route)
        { await Error(context, 422, "invalid_scenario_edit"); return; }
        var receipt = await backend.SaveScenarioActionAsync(write, token).ConfigureAwait(false);
        await context.Response.WriteAsJsonAsync(receipt, Json, token).ConfigureAwait(false);
    }
}
