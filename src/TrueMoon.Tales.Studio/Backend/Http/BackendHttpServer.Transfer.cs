using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Http.Features;

namespace TrueMoon.Tales.Studio.Backend.Http;

internal sealed partial class BackendHttpServer
{
    internal const string TransferPrepareRoute = "/api/backend/v1/transfer-prepare";
    internal const string TransferJobRoute = "/api/backend/v1/transfer-job";
    internal const string TransferStageRoute = "/api/backend/v1/transfer-stage";
    internal const string TransferLaunchRoute = "/api/backend/v1/transfer-launch";
    internal const string TransferStatusRoute = "/api/backend/v1/transfer-status";
    internal const string TransferStopRoute = "/api/backend/v1/transfer-stop";
    internal const string TransferCollectRoute = "/api/backend/v1/transfer-collect";
    internal const int MaxTransferBodyBytes = 256 * 1024;
    internal const int MaxTransferReceiptBytes = 4096;
    internal static readonly JsonSerializerOptions TransferRequestJson = new(Json)
    {
        AllowDuplicateProperties = false,
        RespectRequiredConstructorParameters = true
    };

    private static bool IsTransferRoute(string? path) => path is TransferPrepareRoute or TransferJobRoute or TransferStageRoute or
        TransferLaunchRoute or TransferStatusRoute or TransferStopRoute or TransferCollectRoute;

    private async Task HandleTransferAsync(HttpContext context, string path, CancellationToken token)
    {
        if (!HttpMethods.IsPost(context.Request.Method)) { await Error(context, 405, "method_not_allowed"); return; }
        if (!context.Request.HasJsonContentType()) { await Error(context, 415, "json_required"); return; }

        if (path is TransferLaunchRoute or TransferStatusRoute or TransferStopRoute or TransferCollectRoute)
        {
            await HandleTransferRunAsync(context, path, token).ConfigureAwait(false);
            return;
        }

        FullTransferReceipt receipt;
        if (path == TransferPrepareRoute)
        {
            if (context.Request.ContentLength > MaxTransferBodyBytes)
            { await Error(context, 413, "invalid_request"); return; }
            if (context.Features.Get<IHttpMaxRequestBodySizeFeature>() is { IsReadOnly: false } limit)
                limit.MaxRequestBodySize = MaxTransferBodyBytes;
            var request = await JsonSerializer.DeserializeAsync<TransferPrepareBody>(context.Request.Body, TransferRequestJson, token)
                .ConfigureAwait(false);
            if (request is not { ValidBaseline: true, Form: not null }) { await Error(context, 400, "invalid_request"); return; }
            receipt = await backend.PrepareFullTransferAsync(request.Form, request.ExpectedRevision, request.Fingerprint, token)
                .ConfigureAwait(false);
        }
        else
        {
            var request = await JsonSerializer.DeserializeAsync<TransferJobBody>(context.Request.Body, TransferRequestJson, token)
                .ConfigureAwait(false);
            if (request is not { ValidBaseline: true } || !BackendFullTransfer.ValidJobId(request.Id) ||
                (path == TransferStageRoute ? !BackendConnection.IsHash(request.RequestSha256) : request.RequestSha256 is not null))
            { await Error(context, 400, "invalid_request"); return; }
            receipt = await backend.FullTransferJobAsync(request.Id, request.RequestSha256, request.ExpectedRevision,
                request.Fingerprint, token).ConfigureAwait(false);
        }

        await context.Response.WriteAsJsonAsync(receipt, Json, token).ConfigureAwait(false);
    }

    private async Task HandleTransferRunAsync(HttpContext context, string path, CancellationToken token)
    {
        var request = await JsonSerializer.DeserializeAsync<TransferRunBody>(context.Request.Body, TransferRequestJson, token)
            .ConfigureAwait(false);
        var status = path == TransferStatusRoute;
        if (request is null || !BackendFullTransfer.ValidJobId(request.Id) || !BackendConnection.IsHash(request.RequestSha256) ||
            (status ? request.ExpectedRevision is not null || request.Fingerprint is not null :
                request.ExpectedRevision is not >= 0 || !BackendConnection.IsHash(request.Fingerprint)))
        { await Error(context, 400, "invalid_request"); return; }

        if (path == TransferCollectRoute)
        {
            var project = await backend.CollectFullTransferAsync(request.Id, request.RequestSha256,
                request.ExpectedRevision!.Value, request.Fingerprint!, token).ConfigureAwait(false);
            await context.Response.WriteAsJsonAsync(BackendHttpSnapshot.From(project), Json, token).ConfigureAwait(false);
            return;
        }

        var operation = path switch { TransferLaunchRoute => "launch", TransferStopRoute => "stop", _ => "status" };
        var state = await backend.FullTransferRunAsync(request.Id, request.RequestSha256, operation,
            request.ExpectedRevision.GetValueOrDefault(), request.Fingerprint ?? "", token).ConfigureAwait(false);
        await context.Response.WriteAsJsonAsync(state, Json, token).ConfigureAwait(false);
    }

    internal abstract class TransferBody
    {
        public required long ExpectedRevision { get; init; }
        public required string Fingerprint { get; init; }
        internal bool ValidBaseline => ExpectedRevision >= 0 && BackendConnection.IsHash(Fingerprint);
    }

    internal sealed class TransferPrepareBody : TransferBody
    {
        public required FullTransferForm Form { get; init; }
    }

    internal sealed class TransferJobBody : TransferBody
    {
        public required string Id { get; init; }
        public string? RequestSha256 { get; init; }
    }

    internal sealed class TransferRunBody
    {
        public required string Id { get; init; }
        public required string RequestSha256 { get; init; }
        [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
        public long? ExpectedRevision { get; init; }
        [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
        public string? Fingerprint { get; init; }
    }
}
