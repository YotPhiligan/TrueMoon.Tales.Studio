using System.Text.Encodings.Web;
using System.Text.Json;
using TrueMoon.Tales.Studio.Persistence;

namespace TrueMoon.Tales.Studio.Backend;

internal static partial class BackendAgentRequests
{
    internal const int MaxResponseBytes = 768 * 1024;
    private const int MaxRecordBytes = 4 * 1024 * 1024;
    private const int MaxRows = 200;
    private static readonly JsonDocumentOptions RecordJson = new() { AllowDuplicateProperties = false };
    private static readonly JsonWriterOptions WireJson = new() { Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping };
    private static readonly string[] SummaryFields =
    [
        "id", "project_id", "block_id", "block_label", "goal", "mode", "state", "revision", "base_project_revision",
        "created_at", "updated_at", "actor", "owner", "result", "completed_project_revision"
    ];
    private static readonly Comparer<(string UpdatedAt, string Id)> Order = Comparer<(string UpdatedAt, string Id)>.Create((left, right) =>
    {
        var time = StringComparer.Ordinal.Compare(left.UpdatedAt, right.UpdatedAt);
        return time != 0 ? time : StringComparer.Ordinal.Compare(left.Id, right.Id);
    });

    internal static bool ValidId(string? id) => id is { Length: 20 } && id.StartsWith("req-", StringComparison.Ordinal) &&
        id.AsSpan(4).IndexOfAnyExcept("0123456789abcdef") < 0;

    internal static async Task<byte[]> ReadAsync(ProjectStoreOptions paths, string projectId, string? id, CancellationToken token)
    {
        var directory = RequestsDirectory(paths);
        using var output = new MemoryStream();
        using var writer = new Utf8JsonWriter(output, WireJson);
        writer.WriteStartObject();
        writer.WriteNumber("api_version", 1);
        writer.WriteString("project_id", projectId);
        if (id is null)
        {
            var summaries = await ReadSummariesAsync(paths, directory, projectId, token).ConfigureAwait(false);
            writer.WriteStartArray("requests");
            foreach (var summary in summaries)
            {
                CheckSize(writer.BytesCommitted + writer.BytesPending + summary.Length + 2);
                writer.WriteRawValue(summary, skipInputValidation: true);
            }
            writer.WriteEndArray();
        }
        else
        {
            using var record = await ReadRecordAsync(paths, directory, projectId, id, token).ConfigureAwait(false);
            if (record.RootElement.TryGetProperty("submission_sha256", out _))
                _ = RequireSnapshotHash(record.RootElement);
            writer.WritePropertyName("request");
            record.RootElement.WriteTo(writer);
        }
        writer.WriteEndObject();
        writer.Flush();
        CheckSize(output.Length);
        return output.ToArray();
    }

    private static string RequestsDirectory(ProjectStoreOptions paths) => Path.Combine(paths.ProjectDirectory, "Production", "AgentRequests");

    private static IEnumerable<string> RequestIds(ProjectStoreOptions paths, string directory)
    {
        try { BackendProjectAccess.CheckProductionRead(paths, directory); }
        catch (Exception error) when (error is FileNotFoundException or DirectoryNotFoundException) { yield break; }
        if ((File.GetAttributes(directory) & FileAttributes.Directory) == 0) throw InvalidRecord();
        foreach (var path in Directory.EnumerateFiles(directory, "req-*.json"))
        {
            var id = Path.GetFileNameWithoutExtension(path);
            if (ValidId(id)) yield return id;
        }
    }

    private static async Task<byte[][]> ReadSummariesAsync(ProjectStoreOptions paths, string directory, string projectId, CancellationToken token)
    {
        var newest = new SortedDictionary<(string UpdatedAt, string Id), byte[]?>(Order);
        var size = 0;
        foreach (var id in RequestIds(paths, directory))
        {
            token.ThrowIfCancellationRequested();
            using var record = await ReadRecordAsync(paths, directory, projectId, id, token).ConfigureAwait(false);
            var row = record.RootElement;
            var key = (String(row, "updated_at")!, id);
            if (newest.Count == MaxRows && Order.Compare(key, newest.First().Key) <= 0) continue;
            var summary = Summarize(row);
            newest.Add(key, summary);
            size += summary.Length;
            if (newest.Count > MaxRows)
            {
                var oldest = newest.First();
                size -= oldest.Value?.Length ?? 0;
                newest.Remove(oldest.Key);
            }
            // Keep ordering keys for oversized lists. Only newer rows can displace an omitted row.
            while (size > MaxResponseBytes)
            {
                var oldest = newest.First(entry => entry.Value is not null);
                size -= oldest.Value!.Length;
                newest[oldest.Key] = null;
            }
        }
        if (newest.Values.Any(row => row is null)) throw new BackendCommandException("agent_requests_too_large");
        return newest.Values.Reverse().Select(row => row!).ToArray();
    }

    private static async Task<JsonDocument> ReadRecordAsync(ProjectStoreOptions paths, string directory, string projectId, string id,
        CancellationToken token)
    {
        var path = Path.Combine(directory, id + ".json");
        try
        {
            BackendProjectAccess.CheckProductionRead(paths, path);
            await using var file = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read | FileShare.Delete, 4096, FileOptions.Asynchronous);
            if (file.Length > MaxRecordBytes) throw new BackendCommandException("agent_requests_too_large");
            if (file.Length == 0) throw InvalidRecord();
            var bytes = new byte[(int)file.Length];
            await file.ReadExactlyAsync(bytes, token).ConfigureAwait(false);
            var start = bytes.AsSpan().StartsWith("\uFEFF"u8) ? 3 : 0;
            var document = JsonDocument.Parse(bytes.AsMemory(start), RecordJson);
            try
            {
                ValidateRecord(document.RootElement, projectId, id);
                RequireSavedContext(document.RootElement);
                return document;
            }
            catch { document.Dispose(); throw; }
        }
        catch (Exception error) when (error is FileNotFoundException or DirectoryNotFoundException)
        { throw new BackendCommandException("agent_request_not_found"); }
    }

    private static byte[] Summarize(JsonElement row)
    {
        using var bytes = new MemoryStream();
        using var writer = new Utf8JsonWriter(bytes, WireJson);
        writer.WriteStartObject();
        foreach (var field in SummaryFields)
        {
            if (!row.TryGetProperty(field, out var value))
                throw InvalidRecord();
            writer.WritePropertyName(field);
            value.WriteTo(writer);
        }
        foreach (var property in row.EnumerateObject())
            if (property.Name is "scope" or "result_target")
                property.WriteTo(writer);
        if (row.TryGetProperty("proposal", out var proposal) && proposal.ValueKind == JsonValueKind.Array && proposal.GetArrayLength() > 0)
            writer.WriteBoolean("has_proposal", true);
        writer.WriteEndObject();
        writer.Flush();
        return bytes.ToArray();
    }

    internal static void ValidateResponse(JsonElement response, string? requestedId)
    {
        var projectId = String(response, "project_id");
        if (string.IsNullOrWhiteSpace(projectId) || !response.TryGetProperty("api_version", out var version) ||
            version.ValueKind != JsonValueKind.Number || !version.TryGetInt32(out var api) || api != 1)
            throw new JsonException("Invalid agent requests envelope.");
        if (requestedId is not null)
        {
            if (!response.TryGetProperty("request", out var request))
                throw new JsonException("Missing agent request.");
            ValidateRecord(request, projectId, requestedId);
            RequireSavedContext(request);
            return;
        }
        if (!response.TryGetProperty("requests", out var rows) || rows.ValueKind != JsonValueKind.Array || rows.GetArrayLength() > MaxRows)
            throw new JsonException("Invalid agent request list.");
        foreach (var row in rows.EnumerateArray()) ValidateRecord(row, projectId, String(row, "id"));
    }

    private static void ValidateRecord(JsonElement row, string projectId, string? id)
    {
        if (!ValidId(id) || String(row, "id") != id || String(row, "project_id") != projectId ||
            !row.TryGetProperty("revision", out var revision) || revision.ValueKind != JsonValueKind.Number ||
            !revision.TryGetInt64(out var number) || number < 1 ||
            string.IsNullOrWhiteSpace(String(row, "updated_at")))
            throw InvalidRecord();
    }

    private static string? String(JsonElement element, string property) =>
        element.ValueKind == JsonValueKind.Object && element.TryGetProperty(property, out var value) &&
        value.ValueKind == JsonValueKind.String ? value.GetString() : null;

    private static void RequireSavedContext(JsonElement request)
    {
        if (!request.TryGetProperty("context", out var context) || context.ValueKind != JsonValueKind.Object)
            throw InvalidRecord();
    }

    private static JsonException InvalidRecord() => new("Invalid agent request record.");

    private static void CheckSize(long size)
    {
        if (size > MaxResponseBytes) throw new BackendCommandException("agent_requests_too_large");
    }
}
