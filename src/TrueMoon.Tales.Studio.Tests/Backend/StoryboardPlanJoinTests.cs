using TrueMoon.Tales.Studio.Models;
using TrueMoon.Tales.Studio.Presentation.Scene;
using Xunit;

namespace TrueMoon.Tales.Studio.Tests.Backend;

public sealed class StoryboardPlanJoinTests
{
    [Fact]
    public void Read_UsesTimelineOrderAndKeepsDistinctCutsOfOneAction()
    {
        var window = new Segment("rest", "Привал", 672, 288);
        var first = Shot(window, 696, 108, "first", "spirits");
        var second = Shot(window, 804, 84, "second", "spirits");
        var still = Shot(window, 888, 68, null, "watch");
        var detached = still with { Position = null };
        var elsewhere = Shot(new("end", "Привал", 960, 192), 960, 48, null, "end");

        var pairs = StoryboardPlanJoin.Read([elsewhere, still, second, detached, first]).ToArray();

        Assert.Collection(pairs,
            pair => Check(pair, first, second, 803, 804),
            pair => Check(pair, second, still, 887, 888));
    }

    [Fact]
    public void Read_SkipsAmbiguousOverlapsAndRetainsAnUncoveredGap()
    {
        var window = new Segment("rest", "Привал", 672, 288);
        var first = Shot(window, 672, 24, "first", "approach");
        var overlap = Shot(window, 700, 80, "overlap", "camp");
        var sameStart = Shot(window, 700, 24, "same-start", "camp");
        var underOverlap = Shot(window, 736, 12, "under", "camp");
        var clear = Shot(window, 780, 24, "clear", "look");
        var still = Shot(window, 808, 48, null, "watch");
        var outside = Shot(window, 950, 20, null, "outside");
        var detached = Shot(window, 860, 20, "detached", "detached") with { SegmentId = null };

        var pair = Assert.Single(StoryboardPlanJoin.Read([outside, overlap, still, clear, underOverlap, first, detached, sameStart]));

        Check(pair, clear, still, 803, 808);
    }

    private static void Check(StoryboardPlanJoin pair, StoryboardShot exit, StoryboardShot entry, int exitFrame, int entryFrame)
    {
        Assert.Same(exit, pair.Exit);
        Assert.Same(entry, pair.Entry);
        Assert.Equal((exitFrame, entryFrame), (pair.ExitFrame, pair.EntryFrame));
    }

    private static StoryboardShot Shot(Segment window, int start, int length, string? clip, string action) =>
        new(window.Name, "scene", action, action, "", new(window, start, length, action, clip), length, null, 0, false, null);
}
