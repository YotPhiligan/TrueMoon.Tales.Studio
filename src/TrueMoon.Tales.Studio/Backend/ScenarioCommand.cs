using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.Encodings.Web;

namespace TrueMoon.Tales.Studio.Backend;

[JsonPolymorphic(TypeDiscriminatorPropertyName = "operation")]
[JsonDerivedType(typeof(SaveSegment), "save_segment")]
[JsonDerivedType(typeof(SaveAction), "save_action")]
[JsonDerivedType(typeof(MoveScenarioAction), "move_scenario_action")]
[JsonDerivedType(typeof(LinkSource), "link_source")]
[JsonDerivedType(typeof(ImportVideo), "import_video")]
[JsonDerivedType(typeof(RelinkVideo), "relink_video")]
[JsonDerivedType(typeof(PlaceClip), "place_clip")]
[JsonDerivedType(typeof(AssignTake), "assign_take")]
[JsonDerivedType(typeof(SplitMontageClip), "split_clip")]
[JsonDerivedType(typeof(RemoveMontageClip), "remove_clip")]
[JsonDerivedType(typeof(UndoMontage), "undo_montage")]
[JsonDerivedType(typeof(RedoMontage), "redo_montage")]
[JsonDerivedType(typeof(SetBlackTransition), "set_black_transition")]
[JsonDerivedType(typeof(SaveMontageMusic), "save_music")]
[JsonDerivedType(typeof(SaveMontageSound), "save_sound")]
[JsonDerivedType(typeof(SaveMontageTitle), "save_title")]
[JsonDerivedType(typeof(SaveMontageMarker), "save_marker")]
[JsonDerivedType(typeof(RemoveMontageTrackItem), "remove_track_item")]
[JsonDerivedType(typeof(SetMontageClipAudio), "set_clip_audio")]
[JsonDerivedType(typeof(BindAdaptationText), "bind_adaptation_text")]
[JsonDerivedType(typeof(SaveAnimaticStill), "save_animatic_still")]
[JsonDerivedType(typeof(SaveSceneJoin), "save_scene_join")]
internal abstract record ScenarioCommand
{
    private static readonly JsonSerializerOptions Json = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower,
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
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
internal sealed record SaveAction(string SegmentId, string? ActionId, string Title, string Description, int? PlannedDuration) : ScenarioCommand
{
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? SoundNotes { get; init; }
}
internal sealed record MoveScenarioAction(string SegmentId, string ActionId, string? BeforeActionId) : ScenarioCommand;
internal sealed record LinkSource(string SegmentId, string ActionId, string SourceId, string? Purpose) : ScenarioCommand;
internal sealed record BindAdaptationText(string SegmentId, string? ActionId, string? Reference, int FirstLine, int LastLine,
    string? TextSha256) : ScenarioCommand;
internal sealed record SaveAnimaticStill(string SegmentId, string ActionId, string? Reference) : ScenarioCommand;
internal sealed record SaveSceneJoin(string LeftWindowId, string RightWindowId, string ExitState, string EntryState,
    string Bridge) : ScenarioCommand;
internal sealed record ImportVideo(string? SourceId, string Title, string TakeName, string Reference) : ScenarioCommand;
internal sealed record RelinkVideo(string SourceId, string TakeId, string Reference) : ScenarioCommand;
internal sealed record PlaceClip(string SegmentId, string ActionId, string SourceId, string TakeId,
    int Start, int Duration, int SourceIn) : ScenarioCommand;
internal sealed record AssignTake(string ClipId, string TakeId) : ScenarioCommand;
internal sealed record SplitMontageClip(string ClipId, int Frame) : ScenarioCommand;
internal sealed record RemoveMontageClip(string ClipId) : ScenarioCommand;
internal sealed record UndoMontage : ScenarioCommand;
internal sealed record RedoMontage : ScenarioCommand;
internal sealed record SetBlackTransition(string LeftId, string RightId, int FadeOut, int Pause, int FadeIn) : ScenarioCommand;

internal abstract record MontageTrackCommand : ScenarioCommand;
internal sealed record SaveMontageMusic(string Input, int StartFrame, int DurationFrames, double SourceInFrame, double Gain, bool Muted) : MontageTrackCommand;
internal sealed record SaveMontageSound(string? Id, string SegmentId, string? ClipId, string Name, string Lane, string Input,
    int Stream, int OffsetFrames, int DurationFrames, double SourceInFrame, double Gain, bool Muted) : MontageTrackCommand;
internal sealed record SaveMontageTitle(string? Id, MontageTitle Title) : MontageTrackCommand;
internal sealed record SaveMontageMarker(string? Id, string Name, int Frame) : MontageTrackCommand;
internal sealed record RemoveMontageTrackItem(string Track, string Id) : MontageTrackCommand;
internal sealed record SetMontageClipAudio([property: JsonRequired] bool Enabled) : MontageTrackCommand;

internal sealed class ScenarioEditException(string code) : IOException(code)
{
    internal string Code { get; } = code;
}
