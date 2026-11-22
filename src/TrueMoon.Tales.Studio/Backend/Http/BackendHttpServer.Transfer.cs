using System.Text.Json;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Http.Features;

namespace TrueMoon.Tales.Studio.Backend.Http;

internal sealed partial class BackendHttpServer
{
    internal const string TransferPrepareRoute = "/api/backend/v1/transfer-prepare";
    internal const string TransferJobRoute = "/api/backend/v1/transfer-job";
    internal const string TransferStageRoute = "/api/backend/v1/transfer-stage";
    internal const int MaxTransferBodyBytes = 256 * 1024;
    internal const int MaxTransferReceiptBytes = 4096;
    internal static readonly JsonSerializerOptions TransferRequestJson = new(Json)
    {
        AllowDuplicateProperties = false,
        RespectRequiredConstructorParameters = true
    };

    private static bool IsTransferRoute(string? path) => path is TransferPrepareRoute or TransferJobRoute or TransferStageRoute;

    private async Task HandleTransferAsync(HttpContext context, string path, CancellationToken token)
    {
        if (!HttpMethods.IsPost(context.Request.Method)) { await Error(context, 405, "method_not_allowed"); return; }
        if (!context.Request.HasJsonContentType()) { await Error(context, 415, "json_required"); return; }

        FullTransferReceipt receipt;
        if (path == TransferPrepareRoute)
        {
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
}
