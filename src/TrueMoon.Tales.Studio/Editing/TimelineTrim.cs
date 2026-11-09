using TrueMoon.Tales.Studio.Models;

namespace TrueMoon.Tales.Studio.Editing;

internal enum TrimEdge { Start, End }

internal readonly record struct TrimDraft(int TimelineStart, int Duration, double SourceStart, double SourceEnd);

// A gesture uses the immutable scene at pointer-down, never the preceding move.
internal sealed class TimelineTrim(SceneSnapshot scene, double capacity)
{
    public bool CanStart => scene.Clip.AnchorFrame is null;

    public TrimDraft Move(TrimEdge edge, int delta)
    {
        var clip = scene.Clip;
        var rate = clip.Speed ?? 1;
        var end = clip.StartFrame + clip.DurationFrames;
        var neighbors = scene.SegmentClips ?? scene.Choices.Select(row => row.Clip).ToArray();
        if (edge == TrimEdge.Start)
        {
            if (!CanStart) return new(clip.StartFrame, clip.DurationFrames, clip.SourceInFrame, clip.SourceEnd);
            var previousEnd = neighbors.Where(row => row.Id != clip.Id && row.StartFrame < clip.StartFrame)
                .Select(row => row.StartFrame + row.DurationFrames).DefaultIfEmpty(scene.Segment.StartFrame).Max();
            var minimum = Math.Max(previousEnd - clip.StartFrame, (int)Math.Ceiling(-clip.SourceInFrame / rate));
            delta = Math.Clamp(delta, minimum, clip.DurationFrames - 1);
            var start = Math.Max(0, clip.SourceInFrame + delta * rate);
            var duration = Duration(start, clip.SourceEnd, rate);
            return new(end - duration, duration, start, clip.SourceEnd);
        }
        var nextStart = neighbors.Where(row => row.Id != clip.Id && row.StartFrame >= end)
            .Select(row => row.StartFrame).DefaultIfEmpty(scene.Segment.StartFrame + scene.Segment.DurationFrames).Min();
        var maximum = Math.Max(1, Math.Min(nextStart, scene.Segment.StartFrame + scene.Segment.DurationFrames) - clip.StartFrame);
        var count = Math.Clamp((long)clip.DurationFrames + delta, 1, maximum);
        // Preserve a partial final source frame when returning to the original edge.
        var sourceEnd = count == clip.DurationFrames ? clip.SourceEnd : Math.Min(capacity, clip.SourceInFrame + count * rate);
        return new(clip.StartFrame, Duration(clip.SourceInFrame, sourceEnd, rate), clip.SourceInFrame, sourceEnd);
    }

    internal static int Duration(double start, double end, double rate) => Math.Max(1, (int)Math.Ceiling((end - start) / rate - 1e-8));
}
