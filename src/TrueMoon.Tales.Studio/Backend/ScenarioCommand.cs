using System.Text.Json;
using System.Text.Json.Serialization;

namespace TrueMoon.Tales.Studio.Backend;

[JsonPolymorphic(TypeDiscriminatorPropertyName = "operation")]
[JsonDerivedType(typeof(SaveSegment), "save_segment")]
[JsonDerivedType(typeof(SaveAction), "save_action")]
[JsonDerivedType(typeof(LinkSource), "link_source")]
[JsonDerivedType(typeof(ImportVideo), "import_video")]
[JsonDerivedType(typeof(RelinkVideo), "relink_video")]
[JsonDerivedType(typeof(PlaceClip), "place_clip")]
[JsonDerivedType(typeof(AssignTake), "assign_take")]
internal abstract record ScenarioCommand
{
    private static readonly JsonSerializerOptions Json = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower,
        UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow,
        AllowDuplicateProperties = false,
        MaxDepth = 8
    };

    internal static string Encode(ScenarioCommand command) => JsonSerializer.Serialize(command, Json);
    internal static ScenarioCommand Decode(string json) =>
        json is { Length: > 0 and <= 16_000 } && JsonSerializer.Deserialize<ScenarioCommand>(json, Json) is { } command
            ? command : throw new ScenarioEditException("invalid_scenario_edit");
}

internal sealed record SaveSegment(string? SegmentId, string Name, string Description, int Start, int Duration) : ScenarioCommand;
internal sealed record SaveAction(string SegmentId, string? ActionId, string Title, string Description, int? PlannedDuration) : ScenarioCommand;
internal sealed record LinkSource(string SegmentId, string ActionId, string SourceId, string? Purpose) : ScenarioCommand;
internal sealed record ImportVideo(string? SourceId, string Title, string TakeName, string Reference) : ScenarioCommand;
internal sealed record RelinkVideo(string SourceId, string TakeId, string Reference) : ScenarioCommand;
internal sealed record PlaceClip(string SegmentId, string ActionId, string SourceId, string TakeId,
    int Start, int Duration, int SourceIn) : ScenarioCommand;
internal sealed record AssignTake(string ClipId, string TakeId) : ScenarioCommand;

internal sealed class ScenarioEditException(string code) : IOException(code)
{
    internal string Code { get; } = code;
}
