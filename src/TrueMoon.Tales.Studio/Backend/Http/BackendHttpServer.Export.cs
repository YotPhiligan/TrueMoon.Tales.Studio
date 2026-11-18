using System.Text.Json;
using Microsoft.AspNetCore.Http;
using TrueMoon.Tales.Studio.Backend.Export;

namespace TrueMoon.Tales.Studio.Backend.Http;

internal sealed partial class BackendHttpServer
{
    internal const string ExportStartRoute = "/api/backend/v1/export-start";
    internal const string ExportStatusRoute = "/api/backend/v1/export-status";
    internal const string ExportCancelRoute = "/api/backend/v1/export-cancel";
    internal const string ExportRecoverRoute = "/api/backend/v1/export-recover";
    internal const string ExportChunkRoute = "/api/backend/v1/export-chunk";

    private static readonly JsonSerializerOptions ExportRequestJson = new(Json) { AllowDuplicateProperties = false };

    private async Task HandleExportAsync(HttpContext context, string path, CancellationToken token)
    {
        if (!HttpMethods.IsPost(context.Request.Method)) { await Error(context, 405, "method_not_allowed"); return; }
        if (!context.Request.HasJsonContentType()) { await Error(context, 415, "json_required"); return; }
        using var document = await JsonDocument.ParseAsync(context.Request.Body, cancellationToken: token).ConfigureAwait(false);
        var payload = document.RootElement;
        if (payload.ValueKind != JsonValueKind.Object) { await Error(context, 400, "invalid_request"); return; }

        BackendExportStatus job;
        switch (path)
        {
            case ExportStartRoute:
                if (payload.Deserialize<ExportStartBody>(ExportRequestJson) is not { Valid: true } start)
                { await Error(context, 400, "invalid_request"); return; }
                job = await backend.StartExportAsync(start.ClientKey, start.Profile, start.ExpectedRevision, start.Fingerprint, token).ConfigureAwait(false);
                break;
            case ExportCancelRoute:
                if (payload.Deserialize<ExportCancelBody>(ExportRequestJson) is not { Valid: true, ExpectedJobRevision: > 0 } cancel)
                { await Error(context, 400, "invalid_request"); return; }
                job = await backend.CancelExportAsync(cancel.Id, cancel.ExpectedJobRevision, token).ConfigureAwait(false);
                break;
            case ExportChunkRoute:
                if (payload.Deserialize<ExportChunkBody>(ExportRequestJson) is not { Valid: true, Offset: >= 0 } chunk ||
                    chunk.Offset >= BackendExportJobs.MaxArtifactSize || !BackendConnection.IsHash(chunk.ArtifactSha256))
                { await Error(context, 400, "invalid_request"); return; }
                var bytes = await backend.ExportChunkAsync(chunk.Id, chunk.ArtifactSha256, chunk.Offset, token).ConfigureAwait(false);
                context.Response.ContentType = "application/octet-stream";
                context.Response.ContentLength = bytes.Length;
                await context.Response.Body.WriteAsync(bytes, token).ConfigureAwait(false);
                return;
            default:
                if (payload.Deserialize<ExportIdBody>(ExportRequestJson) is not { Valid: true } lookup)
                { await Error(context, 400, "invalid_request"); return; }
                job = path == ExportStatusRoute
                    ? await backend.ExportStatusAsync(lookup.Id, token).ConfigureAwait(false)
                    : await backend.RecoverExportAsync(lookup.Id, token).ConfigureAwait(false);
                break;
        }
        await context.Response.WriteAsJsonAsync(job, Json, token).ConfigureAwait(false);
    }

    private sealed class ExportStartBody
    {
        public required string ClientKey { get; init; }
        public required string Profile { get; init; }
        public required long ExpectedRevision { get; init; }
        public required string Fingerprint { get; init; }

        internal bool Valid => BackendConnection.IsExportClientKey(ClientKey) && Profile is ("draft" or "project" or "final") &&
            ExpectedRevision >= 0 && BackendConnection.IsHash(Fingerprint);
    }

    private class ExportIdBody
    {
        public required string Id { get; init; }
        internal bool Valid => BackendConnection.IsExportId(Id);
    }

    private sealed class ExportCancelBody : ExportIdBody
    {
        public required long ExpectedJobRevision { get; init; }
    }

    private sealed class ExportChunkBody : ExportIdBody
    {
        public required string ArtifactSha256 { get; init; }
        public required long Offset { get; init; }
    }
}
