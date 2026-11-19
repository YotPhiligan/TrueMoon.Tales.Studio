namespace TrueMoon.Tales.Studio.Presentation.Scene;

internal sealed record StoryboardPlanJoin(StoryboardShot Exit, StoryboardShot Entry)
{
    internal int ExitFrame => checked((int)(End(Exit.Position!) - 1));
    internal int EntryFrame => Entry.Position!.Frame;

    internal static IEnumerable<StoryboardPlanJoin> Read(IEnumerable<StoryboardShot> shots)
    {
        var windows = shots.Where(shot => shot.SegmentId is not null && shot.Position is { } position && InWindow(position))
            .GroupBy(shot => shot.Position!.Window.Id, StringComparer.Ordinal);
        foreach (var window in windows)
        {
            var ordered = window.OrderBy(shot => shot.Position!.Frame).ToArray();
            long earlierEnd = -1;
            for (var index = 0; index + 1 < ordered.Length; index++)
            {
                var exit = ordered[index];
                var entry = ordered[index + 1];
                var left = exit.Position!;
                var right = entry.Position!;
                var exitEnd = End(left);
                var uniqueEntry = index + 2 >= ordered.Length || ordered[index + 2].Position!.Frame > right.Frame;
                if (exit.SegmentId == entry.SegmentId && earlierEnd <= left.Frame && exitEnd <= right.Frame && uniqueEntry)
                    yield return new(exit, entry);
                earlierEnd = Math.Max(earlierEnd, exitEnd);
            }
        }
    }

    private static bool InWindow(StoryboardPosition position)
    {
        var end = End(position);
        return position.Duration > 0 && position.Frame >= position.Window.StartFrame &&
            end <= (long)position.Window.StartFrame + position.Window.DurationFrames && end - 1 <= int.MaxValue;
    }

    private static long End(StoryboardPosition position) => (long)position.Frame + position.Duration;
}
