using TrueMoon.Tales.Studio.Models;

namespace TrueMoon.Tales.Studio.Editing;

internal sealed record TrimLayoutDraft(TrimDraft Clip, Clip[] Clips, ScenarioPlacement[]? ScenarioItems, bool Changed, bool Valid);

// Shared preview/command rules. Planned story durations never participate in montage projection.
internal static class TimelineTrimLayout
{
    internal static TrimLayoutDraft Project(SceneSnapshot scene, double capacity, double sourceStart, double sourceEnd, int timelineStart)
    {
        var selected = scene.Clip;
        var original = scene.SegmentClips ?? scene.Choices.Select(row => row.Clip).ToArray();
        var baseline = new TrimDraft(selected.StartFrame, selected.DurationFrames, selected.SourceInFrame, selected.SourceEnd);
        TrimLayoutDraft Refuse() => new(baseline, original, scene.ScenarioItems, false, false);
        var rate = selected.Speed ?? 1;
        if (!double.IsFinite(capacity) || capacity <= 0 || !double.IsFinite(sourceStart) || !double.IsFinite(sourceEnd) ||
            sourceStart < 0 || sourceEnd <= sourceStart || sourceEnd > capacity || sourceEnd > int.MaxValue ||
            !double.IsFinite(rate) || rate is < .25 or > 4 || timelineStart < 0 || !scene.ScenarioConsistent ||
            original.Count(row => row.Id == selected.Id) != 1 || selected.TakeId is null || !scene.Takes.Any(row => row.Id == selected.TakeId))
            return Refuse();
        var extent = Math.Max(1, Math.Ceiling((sourceEnd - sourceStart) / rate - 1e-8));
        if (!double.IsFinite(extent) || extent < 1 || extent > int.MaxValue ||
            (long)timelineStart + (long)extent > int.MaxValue || scene.Segment.DurationFrames < 1)
            return Refuse();
        var duration = checked((int)extent);
        var changed = sourceStart != selected.SourceInFrame || sourceEnd != selected.SourceEnd || timelineStart != selected.StartFrame;
        if (!changed) return new(baseline, original, scene.ScenarioItems, false, true);
        var edited = selected with { StartFrame = timelineStart, DurationFrames = duration, SourceInFrame = sourceStart, SourceOutFrame = sourceEnd };
        var requested = new TrimDraft(timelineStart, duration, sourceStart, sourceEnd);
        var candidates = original.Select(row => row.Id == selected.Id ? edited : row).ToArray();
        TrimLayoutDraft Invalid() => new(requested, candidates, scene.ScenarioItems, true, false);
        if (selected.AnchorFrame is { } anchor && timelineStart != anchor) return Invalid();
        if (selected.GapProtected && timelineStart < selected.StartFrame) return Invalid();
        if (scene.ScenarioActive)
        {
            if (scene.ScenarioItems is not { } items || !TimelineMove.Matches(scene.Segment, items, original)) return Invalid();
            var item = items.Single(row => row.Id == selected.Id);
            if (item.GapProtected && timelineStart < selected.StartFrame) return Invalid();
            var gap = (long)item.GapBeforeFrames + timelineStart - selected.StartFrame;
            if (gap < 0 || gap > int.MaxValue) return Invalid();
            var projectedItems = items.Select(row => row.Id == selected.Id ? row with
            {
                GapBeforeFrames = (int)gap, DurationFrames = duration, SourceInFrame = sourceStart, SourceOutFrame = sourceEnd
            } : row).ToArray();
            if (!TimelineMove.TryProject(scene.Segment, projectedItems, candidates, out var projected) ||
                projected.Single(row => row.Id == selected.Id).StartFrame != timelineStart)
                return Invalid();
            return new(requested, projected, projectedItems, true, true);
        }
        var windowEnd = (long)scene.Segment.StartFrame + scene.Segment.DurationFrames;
        var end = (long)timelineStart + duration;
        if (original.Where(row => row.Id != selected.Id && row.StartFrame >= (long)selected.StartFrame + selected.DurationFrames)
                .MinBy(row => row.StartFrame)?.GapProtected == true &&
            end > (long)selected.StartFrame + selected.DurationFrames) return Invalid();
        if (timelineStart < scene.Segment.StartFrame || end > windowEnd || candidates.Any(row => row.Id != selected.Id &&
            (long)row.StartFrame < end && (long)row.StartFrame + row.DurationFrames > timelineStart)) return Invalid();
        return new(requested, candidates, null, true, true);
    }
}
