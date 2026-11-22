using System.Text.Json.Serialization;

namespace TrueMoon.Tales.Studio.Backend;

internal sealed record AgentRequestUpdate
{
    internal const int MaxBodyBytes = 1024 * 1024;
    internal const int MaxIpcBytes = BackendAgentRequests.MaxResponseBytes;
    internal const int MaxResultChars = 100_000;

    public required long ExpectedRevision { get; init; }
    public required string Action { get; init; }
    public required string Actor { get; init; }
    public string Result { get; init; } = "";
    public AgentRequestResultTarget? ResultTarget { get; init; }
    public AgentRequestProposalItem[]? Proposal { get; init; }

    internal bool Valid => ExpectedRevision is >= 1 and < long.MaxValue && AgentRequestDraft.ValidActor(Actor) &&
        Result is { Length: <= MaxResultChars } &&
        (ResultTarget is null || ResultTarget.Valid) &&
        (Proposal is null || Proposal is { Length: >= 1 and <= 24 } && Proposal.All(item => item is { Valid: true })) &&
        (Action switch
        {
            "claim" => Result.Length == 0 && ResultTarget is null && Proposal is null,
            "cancel" => ResultTarget is null && Proposal is null,
            "fail" => !string.IsNullOrWhiteSpace(Result) && ResultTarget is null && Proposal is null,
            "finish" => !string.IsNullOrWhiteSpace(Result),
            _ => false
        });

    internal string State => Action switch
    {
        "claim" => "running",
        "finish" => "completed",
        "fail" => "failed",
        "cancel" => "cancelled",
        _ => throw new InvalidOperationException("Unknown agent request action.")
    };
}

internal sealed record AgentRequestResultTarget
{
    public required string SegmentId { get; init; }
    public required string ItemId { get; init; }

    internal bool Valid => SegmentId is not null && ItemId is not null &&
        BackendAgentContext.ValidSelector(SegmentId) && BackendAgentContext.ValidSelector(ItemId);
}

internal sealed record AgentRequestProposalItem
{
    public required string Title { get; init; }
    public required string Description { get; init; }
    public required int DurationFrames { get; init; }
    public int GapBeforeFrames { get; init; }
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? SoundNotes { get; init; }
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? ContinuityNotes { get; init; }

    internal bool Valid => Title is { Length: <= 160 } && !string.IsNullOrWhiteSpace(Title) &&
        Description is { Length: <= AgentRequestDraft.MaxText } && !string.IsNullOrWhiteSpace(Description) &&
        DurationFrames > 0 && GapBeforeFrames >= 0 &&
        SoundNotes is null or { Length: <= AgentRequestDraft.MaxText } &&
        ContinuityNotes is null or { Length: <= AgentRequestDraft.MaxText };
}
