using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using TrueMoon.Tales.Studio.Contracts.Projects;

namespace TrueMoon.Tales.Studio.Backend;

internal sealed record ScenarioActionWrite
{
    internal const int MaxBodyBytes = 32 * 1024;
    internal const string ReceiptsProperty = "studio_scenario_action_writes_v1";

    public required long ExpectedRevision { get; init; }
    public required string Fingerprint { get; init; }
    public required string ClientKey { get; init; }
    public required ScenarioActionDraft Action { get; init; }
    public string Actor { get; init; } = "author";

    internal bool Valid => ExpectedRevision is >= 0 and < int.MaxValue && BackendConnection.IsHash(Fingerprint) &&
        AgentRequestDraft.ValidClientKey(ClientKey) && AgentRequestDraft.ValidActor(Actor) && Action is { Valid: true };

    internal string KeyHash() => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(ClientKey)));

    internal string Digest() => Convert.ToHexString(SHA256.HashData(JsonSerializer.SerializeToUtf8Bytes(
        this with { Fingerprint = Fingerprint.ToUpperInvariant(), Actor = Actor.Trim() }, AgentRequestDraft.Json)));

    internal static ScenarioActionReceipt? FindReceipt(ProjectContract project, string keyHash)
    {
        if (!project.Properties.TryGetValue(ReceiptsProperty, out var receipts)) return null;
        if (receipts.ValueKind != JsonValueKind.Object) throw new JsonException("Invalid scenario action receipts.");
        if (!receipts.TryGetProperty(keyHash, out var entry)) return null;
        var receipt = entry.Deserialize<ScenarioActionReceipt>(AgentRequestDraft.Json);
        if (receipt is not { Changed: true, ApiVersion: 1 } || receipt.ProjectId != project.Id ||
            receipt.ProjectRevision <= 0 || receipt.ProjectRevision > project.Revision || !BackendConnection.IsHash(receipt.SubmissionSha256))
            throw new JsonException("Invalid saved scenario action receipt.");
        return receipt;
    }

    internal static ProjectContract Record(ProjectContract draft, string keyHash, ScenarioActionReceipt receipt)
    {
        var properties = new Dictionary<string, JsonElement>(draft.Properties);
        var receipts = properties.TryGetValue(ReceiptsProperty, out var existing)
            ? existing.Deserialize<Dictionary<string, JsonElement>>(AgentRequestDraft.Json)!
            : new Dictionary<string, JsonElement>(StringComparer.Ordinal);
        receipts.Add(keyHash, JsonSerializer.SerializeToElement(receipt, AgentRequestDraft.Json));
        properties[ReceiptsProperty] = JsonSerializer.SerializeToElement(receipts, AgentRequestDraft.Json);
        return draft with { Properties = properties };
    }
}

internal sealed record ScenarioActionDraft
{
    public required string SegmentId { get; init; }
    public string? ActionId { get; init; }
    public required string Title { get; init; }
    public string Description { get; init; } = "";
    public int? PlannedDuration { get; init; }
    public string? SoundNotes { get; init; }

    internal bool Valid => SegmentId is not null && BackendAgentContext.ValidSelector(SegmentId) &&
        BackendAgentContext.ValidSelector(ActionId) && Title is { Length: <= 160 } && !string.IsNullOrWhiteSpace(Title) &&
        Description is { Length: <= 2000 } && PlannedDuration is null or > 0 && SoundNotes is null or { Length: <= 2000 };

    internal SaveAction ToCommand() => new(SegmentId, ActionId, Title, Description, PlannedDuration) { SoundNotes = SoundNotes };
}

internal sealed record ScenarioActionReceipt(int ApiVersion, string ProjectId, string SegmentId, string ActionId,
    long ProjectRevision, string SubmissionSha256, bool Changed)
{
    internal bool Matches(ScenarioActionWrite request) => ApiVersion == 1 && !string.IsNullOrWhiteSpace(ProjectId) &&
        SegmentId == request.Action.SegmentId && !string.IsNullOrWhiteSpace(ActionId) && BackendAgentContext.ValidSelector(ActionId) &&
        (request.Action.ActionId is { } target ? ActionId == target : Changed) &&
        ProjectRevision == request.ExpectedRevision + (Changed ? 1 : 0) && SubmissionSha256 == request.Digest();
}

/// <summary>Receipt for a scenario action write, or its stable refusal code.</summary>
public sealed class BackendScenarioActionReply
{
    /// <summary>Empty on success.</summary>
    public string ErrorCode { get; set; } = "";

    /// <summary>Bounded JSON receipt on success.</summary>
    public string ReceiptJson { get; set; } = "";
}
