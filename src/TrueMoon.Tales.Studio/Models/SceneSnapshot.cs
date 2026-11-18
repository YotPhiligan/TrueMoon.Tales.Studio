namespace TrueMoon.Tales.Studio.Models;

internal sealed record EditorOpening(SceneSnapshot? Scene, EmptyProject? Empty = null);
internal sealed record EmptyProject(string Name, long Revision, double Fps, string? Dimensions,
    bool CanUndoMontage = false, bool CanRedoMontage = false);

internal sealed record SceneSnapshot(string ProjectName, int Revision, double Fps, Segment Segment, Clip Clip,
    string ClipTitle, Take[] Takes, ClipChoice[] Choices, Clip[]? SegmentClips = null, AudioSlice[]? Audio = null,
    ScenarioPlacement[]? ScenarioItems = null, bool ScenarioActive = false, bool ScenarioConsistent = true, string? Fingerprint = null,
    bool CanUndoMontage = false, bool CanRedoMontage = false, int[]? MarkerFrames = null, MontageJoin? NextJoin = null);
internal sealed record MontageJoin(string RightId, int Gap, int FadeOut, int Pause, int FadeIn);
internal sealed record ClipChoice(Clip Clip, Segment Segment, string Title, Take[] Takes);
internal sealed record Segment(string Id, string Name, int StartFrame, int DurationFrames);
internal sealed record Clip(string Id, string SegmentId, string BlockId, string? TakeId, string? StoryTitle,
    int StartFrame, int DurationFrames, double SourceInFrame, double? SourceOutFrame, double? Speed, int? AnchorFrame = null, bool MuteNativeAudio = false, bool GapProtected = false)
{
    public double SourceEnd => SourceOutFrame ?? SourceInFrame + DurationFrames * (Speed ?? 1);
}
internal sealed record Take(string Id, string VideoAssetId, MediaInfo? VideoInfo, string? Name = null);
internal sealed record MediaInfo(double Fps, double Seconds, int? Frames)
{
    public int FrameCount => Frames ?? (int)Math.Ceiling(Seconds * Fps);
}

// Only the placement inputs used by the existing schema-1 scenario projection.
internal sealed record ScenarioPlacement(string Id, string BlockId, string? TakeId, int? DurationFrames,
    int GapBeforeFrames, double SourceInFrame, double? SourceOutFrame = null, double? Speed = null, int? AnchorFrame = null, bool MuteNativeAudio = false, bool GapProtected = false);
