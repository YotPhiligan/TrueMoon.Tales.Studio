using System.Globalization;
using System.Net;
using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Text.Json;
using TrueMoon.Tales.Studio.Backend.Export;
using static TrueMoon.Tales.Studio.Backend.Http.BackendHttpServer;

namespace TrueMoon.Tales.Studio.Backend.Http;

internal static partial class BackendHttpCli
{
    private const string ExportStartCommand = "backend-export-start", ExportStatusCommand = "backend-export-status",
        ExportCancelCommand = "backend-export-cancel", ExportDownloadCommand = "backend-export-download";
    private const int MaxExportStatusBytes = BackendExportStorage.MaxStatusBytes;

    private static bool IsExportCommand(string? command) =>
        command is ExportStartCommand or ExportStatusCommand or ExportCancelCommand or ExportDownloadCommand;

    private static bool TryExportRequest(string command, Dictionary<string, string> options, out string route,
        out HttpContent? content, out string? output, out string? artifactSha)
    {
        output = artifactSha = null;
        content = null;
        route = ExportStatusRoute;
        if (command == ExportStartCommand)
        {
            if (!TryBaseline(options, out var revision, out var fingerprint) ||
                !options.Remove("--profile", out var profile) || !options.Remove("--client-key", out var key) || options.Count != 0)
                return false;
            var start = new ExportStartBody { ClientKey = key, Profile = profile, ExpectedRevision = revision, Fingerprint = fingerprint };
            if (!start.Valid) return false;
            route = ExportStartRoute;
            content = JsonContent.Create(start, options: Json);
            return true;
        }
        if (!options.Remove("--id", out var id) || !BackendConnection.IsExportId(id)) return false;
        if (command == ExportCancelCommand)
        {
            if (!options.Remove("--job-revision", out var number) || options.Count != 0 ||
                !long.TryParse(number, NumberStyles.None, CultureInfo.InvariantCulture, out var revision) || revision <= 0)
                return false;
            route = ExportCancelRoute;
            content = JsonContent.Create(new ExportCancelBody { Id = id, ExpectedJobRevision = revision }, options: Json);
            return true;
        }
        if (command == ExportDownloadCommand &&
            (!options.Remove("--sha256", out artifactSha) || !BackendConnection.IsHash(artifactSha) ||
             !options.Remove("--output", out var destination) || !TryNewOutputPath(destination, ".mp4", out output)))
            return false;
        if (options.Count != 0) return false;
        content = JsonContent.Create(new ExportIdBody { Id = id }, options: Json);
        return true;
    }

    private static string? ExportReconciliationHint(object? payload) => payload switch
    {
        ExportStartBody start => $" Проверьте состояние через backend-export-status --id {BackendExportJobs.JobId(start.ClientKey)}.",
        ExportIdBody job => $" Проверьте состояние через backend-export-status --id {job.Id}.",
        _ => null
    };

    private static BackendExportStatus ReadExportStatus(JsonElement snapshot, object payload)
    {
        if (snapshot.ValueKind != JsonValueKind.Object || !snapshot.TryGetProperty("project_revision", out _) ||
            snapshot.Deserialize<BackendExportStatus>(Json) is not { Animatic: false, RangeStart: null } job ||
            !BackendConnection.ValidExportStatus(job))
            throw new InvalidDataException("Invalid export status.");

        if (payload is ExportStartBody start &&
            (job.Id != BackendExportJobs.JobId(start.ClientKey) || job.ProjectRevision != start.ExpectedRevision ||
             !BackendExportStorage.EqualHash(job.ProjectFingerprint, start.Fingerprint) || job.Profile != start.Profile))
            throw new InvalidDataException("Export status does not match the requested snapshot.");
        if (payload is ExportIdBody lookup && job.Id != lookup.Id)
            throw new InvalidDataException("Unexpected export ID.");
        if (payload is ExportCancelBody cancel && (job.Revision <= cancel.ExpectedJobRevision || job.State is not ("stopping" or "cancelled")))
            throw new InvalidDataException("Invalid export cancellation reply.");
        return job;
    }

    private static async Task<int> DownloadExportAsync(HttpClient client, Uri statusUri, BackendExportStatus job,
        string sha, string output, CancellationToken interrupt)
    {
        if (job.State != "completed" || job.ArtifactSizeBytes is not { } size ||
            !BackendExportStorage.EqualHash(job.ArtifactSha256!, sha))
            return Fail("export_not_ready", "Готовый MP4 с указанным SHA недоступен.", exitCode: 3);

        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(interrupt);
        deadline.CancelAfter(TimeSpan.FromMinutes(5));
        var token = deadline.Token;
        var address = new Uri(statusUri, ExportChunkRoute);
        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        var buffer = new byte[BackendExportJobs.ChunkSize];
        string digest = "";
        try
        {
            var result = await PublishOutputAsync(output, ".studio-export-", async file =>
            {
                for (long offset = 0; offset < size;)
                {
                    using var requestDeadline = CancellationTokenSource.CreateLinkedTokenSource(token);
                    requestDeadline.CancelAfter(RequestTimeout);
                    var requestToken = requestDeadline.Token;
                    var chunk = new ExportChunkBody { Id = job.Id, ArtifactSha256 = sha, Offset = offset };
                    using var request = new HttpRequestMessage(HttpMethod.Post, address) { Content = JsonContent.Create(chunk, options: Json) };
                    using var response = await client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, requestToken).ConfigureAwait(false);
                    if (response.StatusCode != HttpStatusCode.OK)
                        return await HttpFailureAsync(response, false, requestToken, ExportReconciliationHint(chunk)).ConfigureAwait(false);
                    var bytes = buffer.AsMemory(0, (int)Math.Min(buffer.Length, size - offset));
                    await ReadExportChunkAsync(response.Content, bytes, requestToken).ConfigureAwait(false);
                    hash.AppendData(bytes.Span);
                    await file.WriteAsync(bytes, token).ConfigureAwait(false);
                    offset += bytes.Length;
                }
                digest = Convert.ToHexStringLower(hash.GetHashAndReset());
                return BackendExportStorage.EqualHash(digest, sha)
                    ? 0 : Fail("artifact_sha_mismatch", "SHA-256 полученного MP4 расходится со статусом экспорта.");
            }, token).ConfigureAwait(false);
            if (result != 0) return result;
        }
        catch (IOException) when (Path.Exists(output))
        { return Fail("output_exists", "По указанному пути уже есть файл или каталог."); }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException)
        { return Fail("output_unavailable", "Не удалось сохранить MP4 в указанной папке."); }

        Console.WriteLine(JsonSerializer.Serialize(new
        {
            id = job.Id, artifact_sha256 = digest, artifact_size_bytes = size, output_path = output
        }));
        return 0;
    }

    private static async Task ReadExportChunkAsync(HttpContent content, Memory<byte> buffer, CancellationToken token)
    {
        if (content.Headers.ContentType?.MediaType != "application/octet-stream" || content.Headers.ContentLength != buffer.Length)
            throw new InvalidDataException("Unexpected export chunk headers.");
        try
        {
            await using var input = await content.ReadAsStreamAsync(token).ConfigureAwait(false);
            await input.ReadExactlyAsync(buffer, token).ConfigureAwait(false);
        }
        catch (IOException error) { throw new InvalidDataException("Incomplete export chunk.", error); }
    }
}
