using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using TrueMoon.Tales.Studio.Contracts.Projects;

namespace TrueMoon.Tales.Studio.Backend;

internal sealed record ScenarioActionWrite
{
    internal const int MaxBodyBytes = 32 * 1024;
    internal const string ReceiptsProperty = "studio_scenario_action_writes_v1";

    public required long ExpectedRevision { get; init; }
    public required string Fingerprint { get; init; }
    public required string ClientKey { get; init; }
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public ScenarioActionDraft? Action { get; init; }
    public string Actor { get; init; } = "author";

    // Unused operations stay out of the digest so existing receipts remain valid.
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public MoveScenarioAction? Move { get; init; }
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public LinkSource? Link { get; init; }
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public ScenarioSegmentDraft? Segment { get; init; }
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public ImportVideo? Video { get; init; }
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public ScenarioClipDraft? Placement { get; init; }
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public ScenarioSourcePatch? SourcePatch { get; init; }

    internal bool Valid => ExpectedRevision is >= 0 and < int.MaxValue && BackendConnection.IsHash(Fingerprint) &&
        AgentRequestDraft.ValidClientKey(ClientKey) && AgentRequestDraft.ValidActor(Actor) && Command is not null;

    internal ScenarioCommand? Command => (Action, Move, Link, Segment, Video, Placement, SourcePatch) switch
    {
        ({ Valid: true } action, null, null, null, null, null, null) => action.ToCommand(),
        (null, { } move, null, null, null, null, null) when ValidId(move.SegmentId) && ValidId(move.ActionId) &&
            BackendAgentContext.ValidSelector(move.BeforeActionId) => move,
        (null, null, { } link, null, null, null, null) when ValidId(link.SegmentId) && ValidId(link.ActionId) && ValidId(link.SourceId) &&
            link.Purpose is null or { Length: <= 160 } => link,
        (null, null, null, { Valid: true } segment, null, null, null) => segment.ToCommand(),
        (null, null, null, null, { } video, null, null) when ValidVideo(video) => video,
        (null, null, null, null, null, { } placement, null) when ValidId(placement.SegmentId) && ValidId(placement.ActionId) &&
            ValidId(placement.SourceId) && ValidId(placement.TakeId) => placement.ToCommand(),
        (null, null, null, null, null, null, { Valid: true } sourcePatch) => sourcePatch,
        _ => null
    };

    internal string? SegmentId => Action?.SegmentId ?? Move?.SegmentId ?? Link?.SegmentId ?? Segment?.SegmentId;
    internal string? ActionId => Action?.ActionId ?? Move?.ActionId ?? Link?.ActionId;

    private static bool ValidId(string? id) => id is not null && BackendAgentContext.ValidSelector(id);

    private static bool ValidVideo(ImportVideo video) => BackendAgentContext.ValidSelector(video.SourceId) &&
        video.Reference is { Length: <= 4096 } && !string.IsNullOrWhiteSpace(video.Reference) &&
        video.TakeName is { Length: <= 160 } && !string.IsNullOrWhiteSpace(video.TakeName) &&
        (video.SourceId is null ? video.Title is { Length: <= 160 } && !string.IsNullOrWhiteSpace(video.Title) :
            video.Title is null or { Length: <= 160 });

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

internal sealed record ScenarioClipDraft(string SegmentId, string ActionId, string SourceId, string TakeId,
    [property: JsonRequired] int StartFrame, [property: JsonRequired] int DurationFrames, [property: JsonRequired] int SourceInFrame)
{
    internal PlaceClip ToCommand() => new(SegmentId, ActionId, SourceId, TakeId, StartFrame, DurationFrames, SourceInFrame);
}

internal sealed record ScenarioSegmentDraft
{
    public string? SegmentId { get; init; }
    public required string Name { get; init; }
    public string Description { get; init; } = "";
    public int? StartFrame { get; init; }
    public int? DurationFrames { get; init; }

    internal bool Valid => BackendAgentContext.ValidSelector(SegmentId) && Name is { Length: <= 160 } &&
        !string.IsNullOrWhiteSpace(Name) && Description is { Length: <= 2000 } &&
        (SegmentId is null ? StartFrame.HasValue && DurationFrames.HasValue : StartFrame is null && DurationFrames is null);

    internal SaveSegment ToCommand() => new(SegmentId, Name, Description, StartFrame.GetValueOrDefault(), DurationFrames.GetValueOrDefault());
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

internal sealed record ScenarioActionReceipt(int ApiVersion, string ProjectId,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] string? SegmentId,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] string? ActionId,
    long ProjectRevision, string SubmissionSha256, bool Changed)
{
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? SourceId { get; init; }
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? TakeId { get; init; }
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? AssetId { get; init; }
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? ClipId { get; init; }

    internal static ScenarioActionReceipt Create(ScenarioActionWrite write, ProjectContract draft, bool changed)
    {
        var revision = write.ExpectedRevision + (changed ? 1 : 0);
        var digest = write.Digest();
        if (write.SourcePatch is { } sourcePatch)
            return new(1, draft.Id, null, null, revision, digest, changed) { SourceId = sourcePatch.SourceId };
        if (write.Placement is { } placement)
        {
            var clip = draft.Montage.Clips[^1];
            return new(1, draft.Id, placement.SegmentId, clip.ActionId, revision, digest, changed)
            {
                ClipId = clip.Id, SourceId = clip.SourceId, TakeId = clip.TakeId
            };
        }
        if (write.Video is { } video)
        {
            var source = video.SourceId is { } sourceId ? draft.Sources.Single(row => row.Id == sourceId) : draft.Sources[^1];
            var take = source.Takes[^1];
            return new(1, draft.Id, null, null, revision, digest, changed)
            {
                SourceId = source.Id, TakeId = take.Id, AssetId = take.VideoAssetId
            };
        }
        var segmentId = write.SegmentId ?? draft.Segments[^1].Id;
        var actionId = write.Segment is not null ? null :
            write.ActionId ?? draft.Segments.Single(segment => segment.Id == segmentId).Actions[^1].Id;
        return new(1, draft.Id, segmentId, actionId, revision, digest, changed);
    }

    internal bool Matches(ScenarioActionWrite request) => ApiVersion == 1 && !string.IsNullOrWhiteSpace(ProjectId) &&
        ProjectRevision == request.ExpectedRevision + (Changed ? 1 : 0) && SubmissionSha256 == request.Digest() &&
        (request.SourcePatch is { } sourcePatch ? MatchesSourcePatch(sourcePatch) :
            request.Placement is { } placement ? MatchesPlacement(placement) :
            request.Video is { } video ? MatchesVideo(video) : MatchesScenario(request));

    private bool MatchesSourcePatch(ScenarioSourcePatch sourcePatch) => SourceId == sourcePatch.SourceId &&
        SegmentId is null && ActionId is null && ClipId is null && TakeId is null && AssetId is null;

    private bool MatchesScenario(ScenarioActionWrite request) => ClipId is null && SourceId is null && TakeId is null && AssetId is null &&
        !string.IsNullOrWhiteSpace(SegmentId) && BackendAgentContext.ValidSelector(SegmentId) &&
        (request.SegmentId is { } segment ? SegmentId == segment : Changed) && MatchesAction(request);

    private bool MatchesVideo(ImportVideo video) => Changed && ClipId is null && SegmentId is null && ActionId is null &&
        SourceId is not null && TakeId is not null && AssetId is not null &&
        BackendAgentContext.ValidSelector(SourceId) && BackendAgentContext.ValidSelector(TakeId) && BackendAgentContext.ValidSelector(AssetId) &&
        (video.SourceId is null || SourceId == video.SourceId);

    private bool MatchesPlacement(ScenarioClipDraft placement) => Changed && AssetId is null && ClipId is not null &&
        BackendAgentContext.ValidSelector(ClipId) && SegmentId == placement.SegmentId && ActionId == placement.ActionId &&
        SourceId == placement.SourceId && TakeId == placement.TakeId;

    private bool MatchesAction(ScenarioActionWrite request) => request.Segment is not null ? ActionId is null :
        !string.IsNullOrWhiteSpace(ActionId) && BackendAgentContext.ValidSelector(ActionId) &&
        (request.ActionId is { } action ? ActionId == action : Changed);
}

/// <summary>Receipt for a scenario write, or its stable refusal code.</summary>
public sealed class BackendScenarioActionReply
{
    /// <summary>Empty on success.</summary>
    public string ErrorCode { get; set; } = "";

    /// <summary>Bounded JSON receipt on success.</summary>
    public string ReceiptJson { get; set; } = "";
}
