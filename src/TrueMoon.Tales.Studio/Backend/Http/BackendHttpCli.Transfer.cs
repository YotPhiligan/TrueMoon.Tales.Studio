using System.Net.Http.Json;
using System.Text.Json;
using static TrueMoon.Tales.Studio.Backend.Http.BackendHttpServer;

namespace TrueMoon.Tales.Studio.Backend.Http;

internal static partial class BackendHttpCli
{
    private const string TransferPrepareCommand = "backend-transfer-prepare";
    private const string TransferJobCommand = "backend-transfer-job";
    private const string TransferStageCommand = "backend-transfer-stage";

    private static bool IsTransferCommand(string? command) =>
        command is TransferPrepareCommand or TransferJobCommand or TransferStageCommand;

    private static bool TryTransferRequest(string command, Dictionary<string, string> options, long revision, string fingerprint,
        out string route, out HttpContent? content)
    {
        content = null;
        route = TransferPrepareRoute;
        if (command == TransferPrepareCommand)
        {
            if (!options.Remove("--form", out var path) || options.Count != 0 || ReadTransferForm(path) is not { } form)
                return false;
            content = JsonContent.Create(new TransferPrepareBody { ExpectedRevision = revision, Fingerprint = fingerprint, Form = form },
                options: Json);
            return true;
        }

        if (!options.Remove("--id", out var id) || !BackendFullTransfer.ValidJobId(id))
            return false;
        route = TransferJobRoute;
        string? requestSha = null;
        if (command == TransferStageCommand)
        {
            if (!options.Remove("--request-sha256", out requestSha) || !BackendConnection.IsHash(requestSha))
                return false;
            route = TransferStageRoute;
        }
        if (options.Count != 0) return false;
        content = JsonContent.Create(new TransferJobBody
        {
            ExpectedRevision = revision, Fingerprint = fingerprint, Id = id, RequestSha256 = requestSha
        }, options: Json);
        return true;
    }

    private static FullTransferForm? ReadTransferForm(string path)
    {
        try
        {
            using var file = File.OpenRead(path);
            if (file.Length is < 1 or > MaxTransferBodyBytes) return null;
            var bytes = new byte[(int)file.Length];
            file.ReadExactly(bytes);
            ReadOnlySpan<byte> json = bytes;
            if (json.StartsWith("\uFEFF"u8)) json = json[3..];
            var form = JsonSerializer.Deserialize<FullTransferForm>(json, TransferRequestJson);
            return form is { DraftTakeId.Length: > 0 } && BackendFullTransfer.ValidClientKey(form.ClientKey) ? form : null;
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or ArgumentException or JsonException or NotSupportedException)
        { return null; }
    }

    private static void ValidateTransferReceipt(JsonElement json, TransferBody request)
    {
        if (json.Deserialize<FullTransferReceipt>(TransferRequestJson) is not { } receipt ||
            !(request switch
            {
                TransferPrepareBody prepare => BackendConnection.MatchesTransferPreparation(receipt, prepare.Form,
                    request.ExpectedRevision, request.Fingerprint),
                TransferJobBody job => BackendConnection.MatchesTransferJob(receipt, job.Id, job.RequestSha256,
                    request.ExpectedRevision, request.Fingerprint),
                _ => false
            }))
            throw new InvalidDataException("Full Transfer receipt does not match the request.");
    }

    private static string? TransferReconciliationHint(object? payload)
    {
        var id = payload switch
        {
            TransferPrepareBody prepare => BackendFullTransfer.JobId(prepare.Form.ClientKey),
            TransferJobBody job => job.Id,
            _ => null
        };
        return id is null ? null : $" Перечитайте проект через backend-project и запросите квитанцию через backend-transfer-job --id {id}.";
    }
}
