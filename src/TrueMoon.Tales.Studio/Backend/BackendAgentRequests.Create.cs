using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using TrueMoon.Tales.Studio.Contracts.Projects;
using TrueMoon.Tales.Studio.Persistence;

namespace TrueMoon.Tales.Studio.Backend;

internal sealed record AgentRequestReceipt(int ApiVersion, string ProjectId, string Id, string State, long Revision,
    long BaseProjectRevision, string Fingerprint, string ClientKey, string SubmissionSha256, string SnapshotSha256);

internal static partial class BackendAgentRequests
{
    internal static string RequestId(string clientKey) =>
        "req-" + Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(clientKey)).AsSpan(0, 8));

    internal static async Task<byte[]> CreateAsync(ProjectStoreOptions paths, ProjectFileDocument baseline, BackendOptions options,
        AgentRequestDraft draft, Func<CancellationToken, Task> verifyBaseline, CancellationToken token)
    {
        var project = baseline.Project;
        var directory = RequestsDirectory(paths);
        var id = RequestId(draft.ClientKey);
        var path = Path.Combine(directory, id + ".json");
        BackendProjectAccess.CheckProductionWrite(paths, path);
        var digest = draft.Digest();
        if (File.Exists(path))
        {
            using var record = await ReadRecordAsync(paths, directory, project.Id, id, token).ConfigureAwait(false);
            return Receipt(record.RootElement, draft, digest);
        }
        // Legacy Web stored the same key with a random ID and no exact project SHA.
        foreach (var previousId in RequestIds(paths, directory))
        {
            token.ThrowIfCancellationRequested();
            using var record = await ReadRecordAsync(paths, directory, project.Id, previousId, token).ConfigureAwait(false);
            if (String(record.RootElement, "client_key") == draft.ClientKey)
                throw new ScenarioEditException("agent_request_key_conflict");
        }

        if (project.Revision != draft.ExpectedRevision || !string.Equals(baseline.Fingerprint, draft.Fingerprint, StringComparison.OrdinalIgnoreCase))
            throw new ProjectRevisionConflictException(draft.ExpectedRevision, project.Revision);
        var (selector, scope) = ResolveScope(project, draft);
        var snapshot = await BackendAgentContext.ReadAsync(baseline, options, selector, token).ConfigureAwait(false);
        if (snapshot.ErrorCode.Length != 0) throw new ScenarioEditException(snapshot.ErrorCode);
        var frozen = JsonNode.Parse(snapshot.ContextJson)!.AsObject();
        if (scope is not null) frozen["request_scope"] = scope.DeepClone();
        var snapshotSha = SnapshotHash(frozen);
        frozen["snapshot_sha256"] = snapshotSha;

        var selection = frozen["block"];
        var blockId = selection?["id"]?.GetValue<string>();
        var kind = selection?["kind"]?.GetValue<string>();
        var label = kind switch
        {
            "action" => project.Segments.SelectMany(segment => segment.Actions).Single(action => action.Id == blockId).Title,
            "source" => project.Sources.Single(source => source.Id == blockId).Label,
            _ => null
        };
        var now = DateTimeOffset.UtcNow.ToString("O", System.Globalization.CultureInfo.InvariantCulture);
        var row = new JsonObject
        {
            ["id"] = id, ["project_id"] = project.Id, ["block_id"] = blockId, ["block_label"] = label, ["block_kind"] = kind,
            ["goal"] = draft.Goal, ["mode"] = draft.Mode, ["state"] = "pending", ["revision"] = 1,
            ["base_project_revision"] = project.Revision, ["created_at"] = now, ["updated_at"] = now, ["actor"] = draft.Actor,
            ["owner"] = null, ["result"] = "", ["completed_project_revision"] = null, ["context"] = frozen,
            ["client_key"] = draft.ClientKey, ["submission_sha256"] = digest
        };
        if (scope is not null) row["scope"] = scope;
        var bytes = JsonSerializer.SerializeToUtf8Bytes(row, AgentRequestDraft.Json);
        // Leave room for the GET response envelope before accepting the request.
        var envelope = JsonSerializer.SerializeToUtf8Bytes(new { api_version = 1, project_id = project.Id }, AgentRequestDraft.Json);
        CheckSize(bytes.Length + envelope.Length + ",\"request\":".Length);
        var receipt = new AgentRequestReceipt(1, project.Id, id, "pending", 1, project.Revision,
            baseline.Fingerprint, draft.ClientKey, digest, snapshotSha);
        var result = JsonSerializer.SerializeToUtf8Bytes(receipt, BackendAdaptation.Json);

        token.ThrowIfCancellationRequested();
        Directory.CreateDirectory(directory);
        var pending = path + "." + Guid.NewGuid().ToString("N") + ".pending";
        BackendProjectAccess.CheckProductionWrite(paths, pending);
        var ownsPending = false;
        try
        {
            using (var stream = new FileStream(pending, FileMode.CreateNew, FileAccess.Write, FileShare.None))
            {
                ownsPending = true;
                stream.Write(bytes);
                stream.Flush(flushToDisk: true);
            }
            await verifyBaseline(token).ConfigureAwait(false);
            token.ThrowIfCancellationRequested();
            BackendProjectAccess.CheckProductionWrite(paths, path);
            File.Move(pending, path);
            ownsPending = false;
        }
        finally { if (ownsPending) File.Delete(pending); }
        return result;
    }

    private static (string? Selector, JsonObject? Scope) ResolveScope(ProjectContract project, AgentRequestDraft draft)
    {
        if (draft.Scope is not { } scope) return (draft.Block, null);
        var segment = project.Segments.SingleOrDefault(row => row.Id == scope.SegmentId)
            ?? throw new ScenarioEditException("agent_request_scope_missing");
        var action = scope.ItemId is null ? null : segment.Actions.SingleOrDefault(row => row.Id == scope.ItemId)
            ?? throw new ScenarioEditException("agent_request_scope_missing");
        if (scope.Draft?.AfterItemId is { } after && !segment.Actions.Any(row => row.Id == after))
            throw new ScenarioEditException("agent_request_insertion_conflict");
        if (draft.Block is { } block)
        {
            var selection = BackendAgentContext.Select(project, block);
            if (selection.Kind == "action" ? selection.Id != action!.Id : !action!.Sources.Any(row => row.SourceId == selection.Id))
                throw new ScenarioEditException("invalid_agent_request_scope");
        }
        var result = JsonSerializer.SerializeToNode(scope, AgentRequestDraft.Json)!.AsObject();
        result["segment_name"] = segment.Name;
        if (action is not null) result["block_id"] = action.Id;
        return (action?.Id, result);
    }

    private static byte[] Receipt(JsonElement row, AgentRequestDraft draft, string digest)
    {
        if (String(row, "client_key") != draft.ClientKey || String(row, "submission_sha256") != digest)
            throw new ScenarioEditException("agent_request_key_conflict");
        var context = row.GetProperty("context");
        var fingerprint = String(context, "fingerprint");
        var state = String(row, "state");
        if (state is null || !string.Equals(fingerprint, draft.Fingerprint, StringComparison.OrdinalIgnoreCase) ||
            !row.TryGetProperty("base_project_revision", out var revision) || revision.ValueKind != JsonValueKind.Number ||
            !revision.TryGetInt64(out var number) || number != draft.ExpectedRevision)
            throw InvalidRecord();
        var receipt = new AgentRequestReceipt(1, String(row, "project_id")!, String(row, "id")!, state,
            row.GetProperty("revision").GetInt64(), number, fingerprint!, draft.ClientKey, digest, RequireSnapshotHash(row));
        return JsonSerializer.SerializeToUtf8Bytes(receipt, BackendAdaptation.Json);
    }

    private static string SnapshotHash(JsonObject context) =>
        Convert.ToHexString(SHA256.HashData(JsonSerializer.SerializeToUtf8Bytes(context)));

    private static string RequireSnapshotHash(JsonElement row)
    {
        var frozen = JsonNode.Parse(row.GetProperty("context").GetRawText())!.AsObject();
        var expected = frozen["snapshot_sha256"] is JsonValue value && value.TryGetValue<string>(out var sha) ? sha : null;
        frozen.Remove("snapshot_sha256");
        if (!BackendConnection.IsHash(expected) || !string.Equals(expected, SnapshotHash(frozen), StringComparison.OrdinalIgnoreCase))
            throw InvalidRecord();
        return expected!;
    }

    internal static void ValidateReceipt(JsonElement response, AgentRequestDraft draft)
    {
        if (response.Deserialize<AgentRequestReceipt>(BackendAdaptation.Json) is not { ApiVersion: 1, Revision: >= 1 } receipt ||
            string.IsNullOrWhiteSpace(receipt.ProjectId) || receipt.State is not ("pending" or "running" or "completed" or "failed" or "cancelled") ||
            receipt.Id != RequestId(draft.ClientKey) || receipt.ClientKey != draft.ClientKey ||
            receipt.BaseProjectRevision != draft.ExpectedRevision ||
            !string.Equals(receipt.Fingerprint, draft.Fingerprint, StringComparison.OrdinalIgnoreCase) ||
            receipt.SubmissionSha256 != draft.Digest() || !BackendConnection.IsHash(receipt.SnapshotSha256))
            throw new JsonException("Invalid agent request creation receipt.");
    }
}
