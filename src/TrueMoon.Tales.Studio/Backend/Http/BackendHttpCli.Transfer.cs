using System.Net.Http.Json;
using System.Text.Json;
using static TrueMoon.Tales.Studio.Backend.Http.BackendHttpServer;

namespace TrueMoon.Tales.Studio.Backend.Http;

internal static partial class BackendHttpCli
{
    private const string TransferPrepareCommand = "backend-transfer-prepare";
    private const string TransferJobCommand = "backend-transfer-job";
    private const string TransferStageCommand = "backend-transfer-stage";
    private const string TransferLaunchCommand = "backend-transfer-launch";
    private const string TransferStatusCommand = "backend-transfer-status";
    private const string TransferStopCommand = "backend-transfer-stop";
    private const string TransferCollectCommand = "backend-transfer-collect";

    private static bool IsTransferCommand(string? command) =>
        command is TransferPrepareCommand or TransferJobCommand or TransferStageCommand or
            TransferLaunchCommand or TransferStatusCommand or TransferStopCommand or TransferCollectCommand;

    private static bool TryTransferRequest(string command, Dictionary<string, string> options, out string route, out HttpContent? content)
    {
        content = null;
        var runRoute = command switch
        {
            TransferLaunchCommand => TransferLaunchRoute,
            TransferStatusCommand => TransferStatusRoute,
            TransferStopCommand => TransferStopRoute,
            TransferCollectCommand => TransferCollectRoute,
            _ => null
        };
        if (runRoute is not null)
        {
            route = runRoute;
            return TryTransferRunRequest(options, command == TransferStatusCommand, out content);
        }

        route = TransferPrepareRoute;
        if (!TryBaseline(options, out var revision, out var fingerprint)) return false;
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

    private static bool TryTransferRunRequest(Dictionary<string, string> options, bool status, out HttpContent? content)
    {
        content = null;
        long revision = 0;
        string fingerprint = "";
        if (!options.Remove("--id", out var id) || !BackendFullTransfer.ValidJobId(id) ||
            !options.Remove("--request-sha256", out var sha) || !BackendConnection.IsHash(sha) ||
            (!status && !TryBaseline(options, out revision, out fingerprint)) || options.Count != 0)
            return false;
        content = JsonContent.Create(new TransferRunBody
        {
            Id = id, RequestSha256 = sha, ExpectedRevision = status ? null : revision, Fingerprint = status ? null : fingerprint
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

    private static void ValidateTransferRun(JsonElement json, TransferRunBody request, string command)
    {
        if (command != TransferCollectCommand)
        {
            if (json.Deserialize<FullTransferRunState>(TransferRequestJson) is not { } state ||
                !BackendConnection.MatchesTransferRun(state, request.Id, request.RequestSha256) ||
                command == TransferLaunchCommand && state.State is ("prepared" or "staged"))
                throw new InvalidDataException("Full Transfer run state does not match the request.");
            return;
        }

        var project = ReadProject(json, out var fingerprint);
        ValidateRevision(project.Revision, fingerprint, request.ExpectedRevision!.Value, request.Fingerprint!);
        var take = project.Sources.SelectMany(source => source.Takes).SingleOrDefault(row => row.Id == BackendFullTransfer.FinalTakeId(request.Id));
        if (take is null || BackendTakePairs.Read(take) is not { Method: BackendTakePairs.FullTransfer } ||
            !take.Properties.TryGetValue(BackendFullTransfer.TakeOriginProperty, out var origin) || origin.ValueKind != JsonValueKind.Object ||
            !origin.TryGetProperty("job_id", out var id) || id.GetString() != request.Id ||
            !origin.TryGetProperty("request_sha256", out var sha) ||
            !string.Equals(sha.GetString(), request.RequestSha256, StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException("Full Transfer collection did not return the requested take.");
    }

    private static string? TransferReconciliationHint(object? payload)
    {
        if (payload is TransferRunBody run)
            return $" Проверьте запуск через backend-transfer-status --id {run.Id} --request-sha256 {run.RequestSha256} " +
                "и перечитайте проект через backend-project перед сбором дубля.";

        var id = payload switch
        {
            TransferPrepareBody prepare => BackendFullTransfer.JobId(prepare.Form.ClientKey),
            TransferJobBody job => job.Id,
            _ => null
        };
        return id is null ? null : $" Перечитайте проект через backend-project и запросите квитанцию через backend-transfer-job --id {id}.";
    }
}
