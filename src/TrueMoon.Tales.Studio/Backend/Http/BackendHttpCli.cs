using System.Globalization;
using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using TrueMoon.Tales.Studio.Contracts.Projects;
using TrueMoon.Tales.Studio.Contracts.Serialization;
using TrueMoon.Tales.Studio.Editing;

namespace TrueMoon.Tales.Studio.Backend.Http;

internal static partial class BackendHttpCli
{
    internal const string Usage = """
        CLI существующей сессии --serve-project:
          backend-project --url <url>
          backend-move --url <url> --clip <id> --start-frame <номер> --revision <номер> --sha256 <fingerprint>
          backend-trim --url <url> --clip <id> --start-frame <номер> --source-in <кадр> --source-out <кадр> --revision <номер> --sha256 <fingerprint>
          backend-media --url <url> --asset <id> --revision <номер> --sha256 <fingerprint>
          backend-frame --url <url> --asset <id> --frame <номер> --revision <номер> --sha256 <fingerprint> --output <новый PNG>
        Токен передаётся через STUDIO_BACKEND_HTTP_TOKEN или --token.
        """;
    private const string ProjectCommand = "backend-project", MoveCommand = "backend-move", TrimCommand = "backend-trim";
    private const string MediaCommand = "backend-media", FrameCommand = "backend-frame";
    private const int MaxSnapshotBytes = 2 * 1024 * 1024;
    private const int MaxErrorBytes = 4096;
    private static readonly JsonDocumentOptions ResponseJson = new() { AllowDuplicateProperties = false };

    internal static bool Handles(string? command) => command is ProjectCommand or MoveCommand or TrimCommand or MediaCommand or FrameCommand;

    internal static async Task<int> RunAsync(string[] args)
    {
        if (args is [_, "--help"]) { Console.WriteLine(Usage); return 0; }
        if (!TryRequest(args, out var request, out var output)) return Fail("invalid_arguments", Usage, exitCode: 2);

        using (request)
        using (var client = new HttpClient(new SocketsHttpHandler { UseProxy = false, AllowAutoRedirect = false }))
        using (var interrupt = new CancellationTokenSource())
        using (var deadline = CancellationTokenSource.CreateLinkedTokenSource(interrupt.Token))
        {
            deadline.CancelAfter(TimeSpan.FromSeconds(15));
            var token = deadline.Token;
            var payload = (request.Content as JsonContent)?.Value;
            var write = payload is BackendHttpMove or BackendHttpTrim;
            void Cancel(object? _, ConsoleCancelEventArgs signal) { signal.Cancel = true; interrupt.Cancel(); }
            Console.CancelKeyPress += Cancel;
            try
            {
                using var response = await client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, token).ConfigureAwait(false);
                if (response.StatusCode != HttpStatusCode.OK)
                    return await HttpFailureAsync(response, write, token).ConfigureAwait(false);
                using var json = await ReadJsonAsync(response.Content, MaxSnapshotBytes, token).ConfigureAwait(false);
                var snapshot = json.RootElement;
                if (payload is BackendHttpMediaQuery media)
                    return await WriteMediaResultAsync(snapshot, media, output, token).ConfigureAwait(false);
                var project = ReadProject(snapshot, out var fingerprint);
                if (write) ValidateWrite((JsonContent)request.Content!, project, fingerprint);
                Console.WriteLine(snapshot.GetRawText());
                return 0;
            }
            catch (OperationCanceledException) when (interrupt.IsCancellationRequested)
            { return Fail("cancelled", "Операция отменена.", write, exitCode: 130); }
            catch (OperationCanceledException)
            { return Fail("deadline_exceeded", "Истекло время ожидания ответа backend.", write); }
            catch (Exception error) when (error is HttpRequestException or IOException)
            { return Fail("backend_unavailable", "Ответ backend не получен полностью.", write); }
            catch (Exception error) when (error is JsonException or InvalidDataException or NotSupportedException or InvalidOperationException)
            { return Fail("invalid_response", "Получен недостоверный ответ backend.", write); }
            finally { Console.CancelKeyPress -= Cancel; }
        }
    }

    private static bool TryRequest(string[] args, out HttpRequestMessage request, out string? output)
    {
        request = null!;
        output = null;
        var command = args[0];
        var options = new Dictionary<string, string>(StringComparer.Ordinal);
        if (args.Length % 2 != 1) return false;
        for (var i = 1; i < args.Length; i += 2)
        {
            var key = args[i] == "--start" && command == TrimCommand ? "--start-frame" : args[i];
            if (!options.TryAdd(key, args[i + 1])) return false;
        }

        if (!options.Remove("--url", out var url) || !TryAddress(url, out var address)) return false;
        options.Remove("--token", out var supplied);
        var secret = supplied ?? Environment.GetEnvironmentVariable("STUDIO_BACKEND_HTTP_TOKEN");
        if (!BackendConnection.IsHash(secret)) return false;

        var route = BackendHttpServer.ProjectRoute;
        HttpContent? content = null;
        if (command != ProjectCommand)
        {
            if (!options.Remove("--revision", out var number) || !long.TryParse(number, NumberStyles.None, CultureInfo.InvariantCulture, out var revision) ||
                !options.Remove("--sha256", out var fingerprint) || !BackendConnection.IsHash(fingerprint))
                return false;
            if (command is MediaCommand or FrameCommand)
            {
                if (!TryMediaRequest(command, options, revision, fingerprint, out route, out content, out output)) return false;
            }
            else if (!TryEditRequest(command, options, revision, fingerprint, out route, out content)) return false;
        }
        else if (options.Count != 0) return false;

        request = new HttpRequestMessage(content is null ? HttpMethod.Get : HttpMethod.Post, new Uri(address, route)) { Content = content };
        request.Headers.ConnectionClose = true;
        request.Headers.Add("X-Studio-Token", secret);
        return true;
    }

    private static bool TryEditRequest(string command, Dictionary<string, string> options, long revision, string fingerprint,
        out string route, out HttpContent? content)
    {
        route = "";
        content = null;
        if (!options.Remove("--clip", out var clip) || !ValidId(clip) ||
            !options.Remove("--start-frame", out var number) || !int.TryParse(number, NumberStyles.None, CultureInfo.InvariantCulture, out var start))
            return false;
        if (command == MoveCommand && options.Count == 0)
        {
            route = BackendHttpServer.MoveRoute;
            content = JsonContent.Create(new BackendHttpMove
            { ClipId = clip, StartFrame = start, ExpectedRevision = revision, Fingerprint = fingerprint }, options: BackendHttpServer.Json);
            return true;
        }
        if (command != TrimCommand || !options.Remove("--source-in", out var sourceIn) || !options.Remove("--source-out", out var sourceOut) || options.Count != 0 ||
            !double.TryParse(sourceIn, NumberStyles.Float, CultureInfo.InvariantCulture, out var first) ||
            !double.TryParse(sourceOut, NumberStyles.Float, CultureInfo.InvariantCulture, out var end))
            return false;
        var trim = new BackendHttpTrim { ClipId = clip, StartFrame = start, ExpectedRevision = revision, Fingerprint = fingerprint,
            SourceInFrame = first, SourceOutFrame = end };
        if (!trim.Valid) return false;
        route = BackendHttpServer.TrimRoute;
        content = JsonContent.Create(trim, options: BackendHttpServer.Json);
        return true;
    }

    private static bool ValidId(string value) => !string.IsNullOrWhiteSpace(value) && value.Length <= 160 && !value.Any(char.IsControl);

    private static bool TryAddress(string text, out Uri address)
    {
        address = null!;
        if (!Uri.TryCreate(text, UriKind.Absolute, out var decoded) || decoded.Port is < 1 or > 65535) return false;
        var expected = FormattableString.Invariant($"http://127.0.0.1:{decoded.Port}");
        var origin = text.EndsWith('/') ? text[..^1] : text;
        if (origin != expected && (decoded.Port != 80 || origin != "http://127.0.0.1")) return false;
        address = decoded;
        return true;
    }

    private static async Task<JsonDocument> ReadJsonAsync(HttpContent content, int limit, CancellationToken token)
    {
        if (content.Headers.ContentType?.MediaType != "application/json" || content.Headers.ContentLength > limit)
            throw new InvalidDataException("Unexpected backend content.");
        await using var input = await content.ReadAsStreamAsync(token).ConfigureAwait(false);
        using var buffer = new MemoryStream();
        var chunk = new byte[8192];
        int count;
        while ((count = await input.ReadAsync(chunk.AsMemory(0, Math.Min(chunk.Length, limit + 1 - (int)buffer.Length)), token)
            .ConfigureAwait(false)) != 0)
        {
            if (buffer.Length + count > limit) throw new InvalidDataException("Backend response exceeded its limit.");
            buffer.Write(chunk, 0, count);
        }
        return JsonDocument.Parse(buffer.GetBuffer().AsMemory(0, (int)buffer.Length), ResponseJson);
    }

    private static ProjectContract ReadProject(JsonElement snapshot, out string fingerprint)
    {
        if (!ValidEnvelope(snapshot) ||
            !snapshot.TryGetProperty("fingerprint", out var sha) || sha.ValueKind != JsonValueKind.String || !BackendConnection.IsHash(sha.GetString()) ||
            !snapshot.TryGetProperty("scenario_active", out var active) || active.ValueKind is not (JsonValueKind.True or JsonValueKind.False) ||
            !snapshot.TryGetProperty("project", out var project) || project.ValueKind != JsonValueKind.Object ||
            !project.TryGetProperty("revision", out var revision) || revision.ValueKind != JsonValueKind.Number || !revision.TryGetInt64(out var number) || number < 0)
            throw new JsonException("Invalid backend snapshot.");
        fingerprint = sha.GetString()!;
        return ProjectContractJson.Deserialize(project.GetRawText());
    }

    private static bool ValidEnvelope(JsonElement snapshot) => snapshot.ValueKind == JsonValueKind.Object &&
        snapshot.TryGetProperty("api_version", out var api) && api.ValueKind == JsonValueKind.Number && api.TryGetInt32(out var apiVersion) && apiVersion == 1 &&
        snapshot.TryGetProperty("schema_version", out var schema) && schema.ValueKind == JsonValueKind.Number && schema.TryGetInt32(out var schemaVersion) && schemaVersion == 2;

    private static void ValidateWrite(JsonContent content, ProjectContract project, string fingerprint)
    {
        var (clipId, start, revision, baseline) = content.Value switch
        {
            BackendHttpMove move => (move.ClipId, move.StartFrame, move.ExpectedRevision, move.Fingerprint),
            BackendHttpTrim trim => (trim.ClipId, trim.StartFrame, trim.ExpectedRevision, trim.Fingerprint),
            _ => throw new InvalidOperationException("Unexpected CLI request.")
        };
        var unchanged = project.Revision == revision;
        if (!unchanged && (project.Revision <= revision || project.Revision - revision != 1) ||
            unchanged != string.Equals(fingerprint, baseline, StringComparison.OrdinalIgnoreCase))
            throw new JsonException("Unexpected backend revision.");
        var clip = project.Montage.Clips.SingleOrDefault(row => row.Id == clipId);
        if (clip is null || clip.StartFrame != start) throw new JsonException("Unexpected clip placement.");
        if (content.Value is BackendHttpTrim extent)
        {
            var end = clip.SourceOutFrame ?? clip.SourceInFrame + clip.DurationFrames * clip.Speed;
            if (clip.SourceInFrame != extent.SourceInFrame || end != extent.SourceOutFrame ||
                (end - clip.SourceInFrame) / clip.Speed > int.MaxValue ||
                clip.DurationFrames != TimelineTrim.Duration(clip.SourceInFrame, end, clip.Speed))
                throw new JsonException("Unexpected clip extent.");
        }
    }

    private static async Task<int> HttpFailureAsync(HttpResponseMessage response, bool write, CancellationToken token)
    {
        var status = (int)response.StatusCode;
        var code = "backend_http_error";
        var reconcile = write && (status is 409 or >= 500 || status is >= 300 and < 400);
        try
        {
            using var json = await ReadJsonAsync(response.Content, MaxErrorBytes, token).ConfigureAwait(false);
            var failure = json.RootElement.Deserialize<BackendHttpFailure>(BackendHttpServer.Json);
            if (failure?.Error is { Length: > 0 and <= 80 } error &&
                error.All(ch => ch is >= 'a' and <= 'z' or >= '0' and <= '9' or '_'))
            {
                code = error;
                reconcile |= failure.ReconciliationRequired;
            }
        }
        catch (Exception error) when (error is JsonException or InvalidDataException or IOException) { }
        return Fail(code, $"Backend вернул HTTP {status}.", reconcile, status, status == 409 ? 3 : 1);
    }

    private static int Fail(string error, string message, bool reconcile = false, int? status = null, int exitCode = 1)
    {
        if (reconcile) message += " Перечитайте проект через backend-project и сверьте ревизию и SHA.";
        Console.Error.WriteLine(JsonSerializer.Serialize(new
        {
            error, message, status, reconciliation_required = reconcile, automatic_retry = false
        }));
        return exitCode;
    }
}
