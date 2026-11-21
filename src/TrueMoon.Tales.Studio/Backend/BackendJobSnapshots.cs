using System.Text;
using System.Text.Json;

namespace TrueMoon.Tales.Studio.Backend;

internal sealed record SavedJobMotion(string Kind, string TakeId, string? AssetId, string? Sha256);
internal sealed record SavedJobSnapshot(string Id, long Revision, string Prompt, string PromptSha256,
    SavedJobMotion? Motion, string? TransferRequestSha256, FullTransferForm? TransferForm);
internal sealed record JobSnapshotComparison(SavedJobSnapshot Before, SavedJobSnapshot After);

internal static partial class BackendFullTransfer
{
    private static readonly UTF8Encoding SnapshotEncoding = new(false, true);

    internal static bool ValidSnapshotId(string? id) => ValidJobId(id) ||
        id is { Length: 20 } && id.StartsWith("job-", StringComparison.Ordinal) &&
        id.AsSpan(4).IndexOfAnyExcept("0123456789abcdef") < 0;

    internal static SavedJobSnapshot ReadSnapshot(string directory, string id, string projectId, long revision)
    {
        if (!ValidSnapshotId(id)) throw InvalidSnapshot();
        var snapshot = ValidJobId(id)
            ? ReadTransferSnapshot(directory, id, projectId)
            : ReadLegacySnapshot(directory, id, projectId);
        if (snapshot.Revision < 0 || snapshot.Revision > revision || snapshot.Prompt.Length is < 1 or > 12_000)
            throw new ScenarioEditException("job_snapshot_invalid");
        return snapshot;
    }

    private static SavedJobSnapshot ReadTransferSnapshot(string directory, string id, string projectId)
    {
        var job = JobDirectory(directory, id);
        var (request, receipt, _) = ReadJob(job, id, projectId, null);
        var promptSha = Hash(SnapshotEncoding.GetBytes(request.Form.Prompt));
        return new(id, request.Revision, request.Form.Prompt, promptSha,
            new("video_1", request.Draft.TakeId, request.Draft.AssetId, request.Draft.Sha256),
            receipt.RequestSha256, request.Form);
    }

    private static SavedJobSnapshot ReadLegacySnapshot(string directory, string id, string projectId)
    {
        var job = Path.Combine(directory, "Production", "Jobs", id);
        using var receipt = JsonDocument.Parse(ReadFile(job, "Studio.job.json"), LegacyJson);
        var record = receipt.RootElement;
        var revision = record.GetProperty("source_revision").GetInt64();
        if (record.GetProperty("id").GetString() != id || record.GetProperty("project_id").GetString() != projectId)
            throw InvalidSnapshot();
        var files = record.GetProperty("input_files");
        var input = Path.Combine(job, "Input");
        var prompt = ReadInput("Prompt.txt");
        using var config = JsonDocument.Parse(ReadInput("Config.json"), LegacyJson);
        using var blockSnapshot = JsonDocument.Parse(ReadInput("Block.snapshot.json"), LegacyJson);
        var block = blockSnapshot.RootElement.GetProperty("block");
        if (revision != blockSnapshot.RootElement.GetProperty("project_revision").GetInt64() ||
            block.GetProperty("id").GetString() != record.GetProperty("block_id").GetString())
            throw InvalidSnapshot();
        SavedJobMotion? motion = null;
        switch (config.RootElement.GetProperty("profile").GetString())
        {
            case "omni-context":
                var takeId = block.GetProperty("context_take_id").GetString();
                var latent = config.RootElement.GetProperty("previous_latent").GetString();
                if (takeId is not { Length: > 0 and <= 160 } || string.IsNullOrWhiteSpace(latent))
                    throw InvalidSnapshot();
                // The recorded path is a metadata key only. Legacy media roots can be offline.
                var hashes = record.GetProperty("source_files").EnumerateArray()
                    .Where(row => SameLegacyPath(row.GetProperty("path").GetString(), latent))
                    .Select(row => row.GetProperty("sha256").GetString()).Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
                if (hashes.Length > 1 || hashes.Length == 1 && !BackendConnection.IsHash(hashes[0]))
                    throw InvalidSnapshot();
                motion = new("motion_context", takeId, null, hashes.SingleOrDefault()?.ToUpperInvariant());
                break;
            case "omni-guided":
                break;
            default:
                throw InvalidSnapshot();
        }
        return new(id, revision, SnapshotEncoding.GetString(prompt), Hash(prompt), motion, null, null);

        byte[] ReadInput(string name)
        {
            var bytes = ReadFile(input, name);
            var suffix = $"/Jobs/{id}/Input/{name}";
            var hashes = files.EnumerateObject()
                .Where(row => row.Name.Replace('\\', '/').EndsWith(suffix, StringComparison.OrdinalIgnoreCase))
                .Select(row => row.Value.GetString()).ToArray();
            if (hashes.Length != 1 || !string.Equals(hashes[0], Hash(bytes), StringComparison.OrdinalIgnoreCase))
                throw InvalidSnapshot();
            return bytes;
        }
    }

    private static readonly JsonDocumentOptions LegacyJson = new() { AllowDuplicateProperties = false };

    private static bool SameLegacyPath(string? left, string right) =>
        string.Equals(left?.Replace('\\', '/'), right.Replace('\\', '/'), StringComparison.OrdinalIgnoreCase);

    private static ScenarioEditException InvalidSnapshot() => new("job_snapshot_invalid");
}
