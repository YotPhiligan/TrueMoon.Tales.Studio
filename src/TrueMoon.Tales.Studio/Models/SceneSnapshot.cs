namespace TrueMoon.Tales.Studio.Models;

internal sealed record SceneSnapshot(string ProjectName, int Revision, double Fps, Segment Segment, Clip Clip,
    string ClipTitle, Take[] Takes, ClipChoice[] Choices, Clip[]? SegmentClips = null, AudioSlice[]? Audio = null);
internal sealed record ClipChoice(Clip Clip, Segment Segment, string Title, Take[] Takes);
internal sealed record Segment(string Id, string Name, int StartFrame, int DurationFrames);
internal sealed record Clip(string Id, string SegmentId, string BlockId, string? TakeId, string? StoryTitle,
    int StartFrame, int DurationFrames, double SourceInFrame, double? SourceOutFrame, double? Speed, int? AnchorFrame = null, bool MuteNativeAudio = false)
{
    public double SourceEnd => SourceOutFrame ?? SourceInFrame + DurationFrames * (Speed ?? 1);
}
internal sealed record Take(string Id, string VideoAssetId, MediaInfo? VideoInfo, string? Name = null);
internal sealed record MediaInfo(double Fps, double Seconds, int? Frames)
{
    public int FrameCount => Frames ?? (int)Math.Ceiling(Seconds * Fps);
}
