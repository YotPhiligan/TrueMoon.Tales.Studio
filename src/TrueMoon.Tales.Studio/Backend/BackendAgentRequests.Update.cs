using System.Globalization;
using System.Text.Json;
using System.Text.Json.Nodes;
using TrueMoon.Tales.Studio.Contracts.Projects;
using TrueMoon.Tales.Studio.Persistence;

namespace TrueMoon.Tales.Studio.Backend;

internal sealed record AgentRequestUpdateReceipt(int ApiVersion, string ProjectId, string Id, string State, long Revision,
    string? Owner, string UpdatedAt, long? CompletedProjectRevision, string? SnapshotSha256);

internal static partial class BackendAgentRequests
{
    internal static async Task<byte[]> UpdateAsync(ProjectStoreOptions paths, ProjectContract project, string id,
        AgentRequestUpdate update, CancellationToken token)
    {
        var directory = RequestsDirectory(paths);
        var path = Path.Combine(directory, id + ".json");
        BackendProjectAccess.CheckProductionWrite(paths, path);
        using var record = await ReadRecordAsync(paths, directory, project.Id, id, token).ConfigureAwait(false);
        var row = record.RootElement;
        var revision = row.GetProperty("revision").GetInt64();
        if (revision != update.ExpectedRevision)
            throw new ScenarioEditException("agent_request_revision_conflict");
        var state = String(row, "state");
        if (state is not ("pending" or "running" or "completed" or "failed" or "cancelled")) throw InvalidRecord();
        var allowed = update.Action switch
        {
            "claim" => state == "pending",
            "cancel" => state is "pending" or "running",
            _ => state == "running"
        };
        if (!allowed) throw new ScenarioEditException("agent_request_state_conflict");
        var owner = String(row, "owner");
        var actor = update.Actor.Trim();
        var completed = update.Action is "finish" or "fail";
        if (completed && owner != actor)
            throw new ScenarioEditException("agent_request_owner_conflict");

        var context = row.GetProperty("context");
        var snapshotHash = row.TryGetProperty("submission_sha256", out _) ? RequireSnapshotHash(row) : String(context, "snapshot_sha256");
        var updated = DateTimeOffset.UtcNow.ToString("O", CultureInfo.InvariantCulture);
        var fields = new JsonObject { ["state"] = update.State, ["revision"] = revision + 1, ["updated_at"] = updated };
        long? completedRevision = completed ? project.Revision : null;
        if (update.Action == "claim") fields["owner"] = owner = actor;
        else fields["result"] = update.Result;
        if (completed) fields["completed_project_revision"] = completedRevision;
        if (update.Action == "finish") AddCompletionFields(row, project, update, fields);

        var bytes = SerializeUpdate(row, fields);
        if (bytes.Length > MaxRecordBytes) throw new BackendCommandException("agent_requests_too_large");
        var receipt = new AgentRequestUpdateReceipt(1, project.Id, id, update.State, revision + 1, owner, updated,
            completedRevision, BackendConnection.IsHash(snapshotHash) ? snapshotHash : null);
        var result = JsonSerializer.SerializeToUtf8Bytes(receipt, AgentRequestDraft.Json);
        ReplaceRecord(paths, path, bytes, token);
        return result;
    }

    private static void AddCompletionFields(JsonElement row, ProjectContract project, AgentRequestUpdate update, JsonObject fields)
    {
        row.TryGetProperty("scope", out var scope);
        var intent = String(scope, "intent");
        if ((intent == "breakdown_segment") != (update.Proposal is not null)) throw InvalidUpdate();
        if (update.Proposal is { } proposal)
            fields["proposal"] = JsonSerializer.SerializeToNode(proposal.Select(item => item with { Title = item.Title.Trim() }), AgentRequestDraft.Json);

        var apply = String(row, "mode") == "apply";
        if (update.ResultTarget is not { } target)
        {
            if (apply && intent == "prepare_block") throw InvalidUpdate();
            return;
        }
        if (!apply || intent is not ("prepare_block" or "improve_block") ||
            String(scope, "segment_id") != target.SegmentId ||
            (intent == "improve_block" && String(scope, "item_id") != target.ItemId))
            throw InvalidUpdate();
        var action = project.Segments.SingleOrDefault(segment => segment.Id == target.SegmentId)?
            .Actions.SingleOrDefault(candidate => candidate.Id == target.ItemId);
        if (action is null) throw InvalidUpdate();

        var snapshot = row.GetProperty("context");
        var native = snapshot.TryGetProperty("context_version", out var version) && version.ValueKind == JsonValueKind.Number &&
            version.TryGetInt32(out var number) && number == 3;
        if (intent == "prepare_block" && AlreadyInSnapshot(snapshot, target, native)) throw InvalidUpdate();
        fields["result_target"] = new JsonObject
        {
            ["segment_id"] = target.SegmentId, ["item_id"] = target.ItemId,
            ["block_id"] = !native && action.Sources.Length == 1 ? action.Sources[0].SourceId : action.Id
        };
    }

    private static bool AlreadyInSnapshot(JsonElement snapshot, AgentRequestResultTarget target, bool native)
    {
        if (native && snapshot.TryGetProperty("project", out var project) && project.ValueKind == JsonValueKind.Object &&
            project.TryGetProperty("segments", out var segments) && segments.ValueKind == JsonValueKind.Array)
        {
            foreach (var segment in segments.EnumerateArray())
                if (String(segment, "id") == target.SegmentId && segment.TryGetProperty("actions", out var actions) &&
                    actions.ValueKind == JsonValueKind.Array)
                    return actions.EnumerateArray().Any(action => String(action, "id") == target.ItemId);
        }
        if (!native && snapshot.TryGetProperty("scenario_segment", out var legacy) && String(legacy, "id") == target.SegmentId &&
            legacy.TryGetProperty("items", out var items) && items.ValueKind == JsonValueKind.Array)
            return items.EnumerateArray().Any(item => String(item, "id") == target.ItemId);
        throw InvalidRecord();
    }

    private static byte[] SerializeUpdate(JsonElement row, JsonObject fields)
    {
        using var stream = new MemoryStream();
        using var writer = new Utf8JsonWriter(stream, WireJson);
        writer.WriteStartObject();
        foreach (var property in row.EnumerateObject())
        {
            if (fields.ContainsKey(property.Name)) continue;
            writer.WritePropertyName(property.Name);
            // Preserve the saved context and unknown historical fields verbatim.
            writer.WriteRawValue(property.Value.GetRawText(), skipInputValidation: true);
        }
        foreach (var (name, value) in fields)
        {
            writer.WritePropertyName(name);
            if (value is null) writer.WriteNullValue();
            else value.WriteTo(writer);
        }
        writer.WriteEndObject();
        writer.Flush();
        return stream.ToArray();
    }

    private static void ReplaceRecord(ProjectStoreOptions paths, string path, byte[] bytes, CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        var staged = path + "." + Guid.NewGuid().ToString("N") + ".pending";
        BackendProjectAccess.CheckProductionWrite(paths, staged);
        var ownsStage = false;
        try
        {
            using (var stream = new FileStream(staged, FileMode.CreateNew, FileAccess.Write, FileShare.None))
            {
                ownsStage = true;
                stream.Write(bytes);
                stream.Flush(flushToDisk: true);
            }
            token.ThrowIfCancellationRequested();
            BackendProjectAccess.CheckProductionWrite(paths, path);
            new AtomicFileReplacement().Replace(staged, path);
            ownsStage = false;
        }
        finally { if (ownsStage) File.Delete(staged); }
    }

    private static ScenarioEditException InvalidUpdate() => new("invalid_agent_request_update");

    internal static void ValidateUpdateReceipt(JsonElement response, string id, AgentRequestUpdate update)
    {
        if (response.Deserialize<AgentRequestUpdateReceipt>(AgentRequestDraft.Json) is not { ApiVersion: 1 } receipt ||
            string.IsNullOrWhiteSpace(receipt.ProjectId) || receipt.Id != id || receipt.State != update.State ||
            receipt.Revision != update.ExpectedRevision + 1 ||
            (update.Action != "cancel" && receipt.Owner != update.Actor.Trim()) ||
            ((update.Action is "finish" or "fail") ? receipt.CompletedProjectRevision is null or < 0 : receipt.CompletedProjectRevision is not null) ||
            string.IsNullOrWhiteSpace(receipt.UpdatedAt) ||
            (receipt.SnapshotSha256 is not null && !BackendConnection.IsHash(receipt.SnapshotSha256)))
            throw new JsonException("Invalid agent request update receipt.");
    }
}
