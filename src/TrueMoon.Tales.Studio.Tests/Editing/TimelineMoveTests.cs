using TrueMoon.Tales.Studio.Editing;
using TrueMoon.Tales.Studio.Models;
using Xunit;

namespace TrueMoon.Tales.Studio.Tests.Editing;

public sealed class TimelineMoveTests
{
    [Theory]
    [InlineData(int.MinValue, 100)]
    [InlineData(-11, 100)]
    [InlineData(-10, 100)]
    [InlineData(0, 110)]
    [InlineData(3, 113)]
    [InlineData(22, 132)]
    [InlineData(23, 132)]
    [InlineData(int.MaxValue, 132)]
    public void PlainMove_ClampsAbsoluteStartInsideWindow_WithoutChangingMaterial(int delta, int expected)
    {
        var clip = Video("a", 110, 8) with { SourceInFrame = 2.5, SourceOutFrame = 17.5, Speed = 2, MuteNativeAudio = true };
        var scene = Plain(new("segment", "Test", 100, 40), clip, [clip]);
        var move = new TimelineMove(scene);

        var draft = move.Move(delta);

        Assert.True(move.CanMove);
        Assert.True(draft.Valid);
        Assert.Equal(expected, draft.RequestedStart);
        Assert.Equal(expected, draft.TimelineStart);
        Assert.Equal(expected != 110, draft.Changed);
        Assert.Equal(clip with { StartFrame = expected }, Assert.Single(draft.Clips));
        Assert.Equal(110, scene.Clip.StartFrame);
    }

    [Theory]
    [InlineData(-4, 106, true)]
    [InlineData(-5, 105, false)]
    [InlineData(14, 124, true)]
    [InlineData(15, 125, false)]
    public void PlainMove_AllowsTouchingNeighbors_ButRejectsOneFrameOverlap(int delta, int expected, bool valid)
    {
        var clip = Video("a", 110, 8);
        var previous = Video("previous", 100, 6) with { TakeId = null };
        var next = Video("next", 132, 4);
        var scene = Plain(new("segment", "Test", 100, 40), clip, [previous, clip, next]);

        var draft = new TimelineMove(scene).Move(delta);

        Assert.Equal(valid, draft.Valid);
        Assert.True(draft.Changed);
        Assert.Equal(expected, draft.TimelineStart);
        Assert.Equal(clip with { StartFrame = expected }, draft.Clips.Single(row => row.Id == "a"));
        Assert.Equal(previous, draft.Clips.Single(row => row.Id == "previous"));
        Assert.Equal(next, draft.Clips.Single(row => row.Id == "next"));
    }

    [Fact]
    public void PlainMove_UsesPointerDownBaseline_AndReturnToOriginIsNoOp()
    {
        var clip = Video("a", 110, 8);
        var move = new TimelineMove(Plain(new("segment", "Test", 100, 40), clip, [clip]));

        Assert.Equal(107, move.Move(-3).TimelineStart);
        Assert.Equal(114, move.Move(4).TimelineStart);
        var restored = move.Move(0);

        Assert.True(restored.Valid);
        Assert.False(restored.Changed);
        Assert.Equal(110, restored.RequestedStart);
        Assert.Equal(clip, Assert.Single(restored.Clips));
    }

    [Fact]
    public void PlainMove_AnchoredClipCannotMove()
    {
        var clip = Video("a", 110, 8) with { AnchorFrame = 110 };
        var move = new TimelineMove(Plain(new("segment", "Test", 100, 40), clip, [clip]));

        Assert.False(move.CanMove);
        var draft = move.Move(8);

        Assert.False(draft.Valid);
        Assert.False(draft.Changed);
        Assert.Equal(110, draft.TimelineStart);
        Assert.Equal(clip, Assert.Single(draft.Clips));
    }

    [Fact]
    public void PlainMove_ClipLongerThanWindowCannotProduceValidPlacement()
    {
        var clip = Video("a", 100, 41);
        var move = new TimelineMove(Plain(new("segment", "Test", 100, 40), clip, [clip]));

        var draft = move.Move(int.MaxValue);

        Assert.False(draft.Valid);
        Assert.False(draft.Changed);
        Assert.Equal(100, draft.TimelineStart);
        Assert.Equal(clip, Assert.Single(draft.Clips));
    }

    [Fact]
    public void ScenarioMove_ReordersAndReflowsStoredGaps_PreservingAllSourceFields()
    {
        var scene = Sequential();
        var original = scene.SegmentClips!.ToDictionary(clip => clip.Id);
        var move = new TimelineMove(scene);

        var draft = move.Move(24);

        Assert.True(move.CanMove);
        Assert.True(draft.Valid);
        Assert.True(draft.Changed);
        Assert.Equal(24, draft.RequestedStart);
        Assert.Equal(16, draft.TimelineStart);
        Assert.Equal(["b", "c", "a"], draft.Clips.Select(clip => clip.Id).ToArray());
        Assert.Equal([4, 12, 16], draft.Clips.Select(clip => clip.StartFrame).ToArray());
        Assert.Equal(original["b"] with { StartFrame = 4 }, draft.Clips[0]);
        Assert.Equal(original["c"] with { StartFrame = 12 }, draft.Clips[1]);
        Assert.Equal(original["a"] with { StartFrame = 16 }, draft.Clips[2]);
        Assert.Equal([0, 12, 20], scene.SegmentClips!.Select(clip => clip.StartFrame).ToArray());
    }

    [Fact]
    public void ScenarioMove_ToBeginningRetainsMovedItemsPrecedingGap()
    {
        var original = Sequential();
        var scene = original with { Clip = original.SegmentClips!.Single(clip => clip.Id == "b") };

        var draft = new TimelineMove(scene).Move(-12);

        Assert.True(draft.Valid);
        Assert.True(draft.Changed);
        Assert.Equal(0, draft.RequestedStart);
        Assert.Equal(4, draft.TimelineStart);
        Assert.Equal(["b", "a", "c"], draft.Clips.Select(clip => clip.Id).ToArray());
        Assert.Equal([4, 10, 20], draft.Clips.Select(clip => clip.StartFrame).ToArray());
    }

    [Fact]
    public void ScenarioMove_SameInsertionSlotAndReturnToOriginDoNotChangeOrder()
    {
        var scene = Sequential();
        var move = new TimelineMove(scene);

        var sameSlot = move.Move(1);
        Assert.True(sameSlot.Valid);
        Assert.False(sameSlot.Changed);
        Assert.Equal(1, sameSlot.RequestedStart);
        Assert.Equal(0, sameSlot.TimelineStart);
        Assert.Equal(scene.SegmentClips, sameSlot.Clips);

        Assert.True(move.Move(24).Changed);
        var restored = move.Move(0);
        Assert.True(restored.Valid);
        Assert.False(restored.Changed);
        Assert.Equal(0, restored.RequestedStart);
        Assert.Equal(scene.SegmentClips, restored.Clips);
    }

    [Fact]
    public void ScenarioMove_ZeroDeltaPreservesTouchingBoundaryOrder()
    {
        var first = Video("a", 0, 4);
        var second = Video("b", 4, 4);
        var scene = Story(new("segment", "Test", 0, 8), second, [first, second], [Placement(first, 0), Placement(second, 0)]);

        var draft = new TimelineMove(scene).Move(0);

        Assert.True(draft.Valid);
        Assert.False(draft.Changed);
        Assert.Equal(4, draft.TimelineStart);
        Assert.Equal([first, second], draft.Clips);
    }

    [Fact]
    public void ScenarioMove_LongFirstClipCanMovePastShortLastClip_InFullWindow()
    {
        var first = Video("a", 0, 8);
        var last = Video("b", 8, 4);
        var scene = Story(new("segment", "Test", 0, 12), first, [first, last],
            [Placement(first, 0), Placement(last, 0)]);

        var draft = new TimelineMove(scene).Move(9);

        Assert.True(draft.Valid);
        Assert.True(draft.Changed);
        Assert.Equal(9, draft.RequestedStart);
        Assert.Equal(4, draft.TimelineStart);
        Assert.Equal([last with { StartFrame = 0 }, first with { StartFrame = 4 }], draft.Clips);
    }

    [Fact]
    public void ScenarioMove_IncludesPlaceholderMissingFromVideoChoices()
    {
        var scene = Sequential();
        var clips = scene.SegmentClips!.Select(clip => clip.Id == "b"
            ? clip with { TakeId = null, SourceInFrame = 0, SourceOutFrame = null, Speed = null } : clip).ToArray();
        scene = Story(scene.Segment, clips[0], clips,
            [Placement(clips[0], 0), Placement(clips[1], 4), Placement(clips[2], 2)]) with
        {
            Choices = [new(clips[0], scene.Segment, "a", []), new(clips[2], scene.Segment, "c", [])]
        };

        var draft = new TimelineMove(scene).Move(24);

        Assert.True(draft.Valid);
        Assert.Equal(["b", "c", "a"], draft.Clips.Select(clip => clip.Id).ToArray());
        Assert.Equal([4, 12, 16], draft.Clips.Select(clip => clip.StartFrame).ToArray());
        Assert.Equal(clips[1] with { StartFrame = 4 }, draft.Clips[0]);
        Assert.Null(draft.Clips[0].TakeId);
    }

    [Fact]
    public void ScenarioMove_ReflowPreservesNeighborAnchor()
    {
        var first = Video("a", 0, 8);
        var anchored = Video("b", 12, 4) with { AnchorFrame = 12 };
        var last = Video("c", 20, 4);
        var scene = Story(new("segment", "Test", 0, 40), first, [first, anchored, last],
            [Placement(first, 0), Placement(anchored, 4), Placement(last, 4)]);

        var draft = new TimelineMove(scene).Move(30);

        Assert.True(draft.Valid);
        Assert.True(draft.Changed);
        Assert.Equal(30, draft.RequestedStart);
        Assert.Equal(24, draft.TimelineStart);
        Assert.Equal(["b", "c", "a"], draft.Clips.Select(clip => clip.Id).ToArray());
        Assert.Equal([12, 20, 24], draft.Clips.Select(clip => clip.StartFrame).ToArray());
        Assert.Equal(anchored, draft.Clips[0]);
    }

    [Fact]
    public void ScenarioMove_AnchorOverlapProducesInvalidGhostWithoutMutatingNeighbors()
    {
        var first = Video("a", 0, 8);
        var anchored = Video("b", 12, 4) with { AnchorFrame = 12 };
        var last = Video("c", 20, 8);
        var scene = Story(new("segment", "Test", 0, 40), last, [first, anchored, last],
            [Placement(first, 0), Placement(anchored, 4), Placement(last, 4)]);
        var move = new TimelineMove(scene);

        var draft = move.Move(-20);

        Assert.True(move.CanMove);
        Assert.False(draft.Valid);
        Assert.True(draft.Changed);
        Assert.Equal(0, draft.RequestedStart);
        Assert.Equal(0, draft.TimelineStart);
        Assert.Equal(first, draft.Clips.Single(clip => clip.Id == "a"));
        Assert.Equal(anchored, draft.Clips.Single(clip => clip.Id == "b"));
        Assert.Equal(last with { StartFrame = 0 }, draft.Clips.Single(clip => clip.Id == "c"));
        Assert.Equal([0, 12, 20], scene.SegmentClips!.Select(clip => clip.StartFrame).ToArray());
    }

    [Fact]
    public void ScenarioMove_AnchorWaitingCanOverflowFixedWindowAfterReorder()
    {
        var first = Video("a", 0, 4);
        var anchored = Video("b", 12, 4) with { AnchorFrame = 12 };
        var last = Video("c", 16, 8);
        var scene = Story(new("segment", "Test", 0, 24), first, [first, anchored, last],
            [Placement(first, 0), Placement(anchored, 0), Placement(last, 0)]);
        var move = new TimelineMove(scene);

        var draft = move.Move(20);

        Assert.True(move.CanMove);
        Assert.False(draft.Valid);
        Assert.True(draft.Changed);
        Assert.Equal(20, draft.RequestedStart);
        Assert.Equal(anchored, draft.Clips.Single(clip => clip.Id == "b"));
        Assert.Equal(last, draft.Clips.Single(clip => clip.Id == "c"));
        Assert.Equal(first with { StartFrame = 20 }, draft.Clips.Single(clip => clip.Id == "a"));
    }

    [Theory]
    [InlineData("missing-items")]
    [InlineData("inconsistent-project")]
    [InlineData("gap-mismatch")]
    [InlineData("unknown-duration")]
    [InlineData("negative-gap")]
    [InlineData("duplicate-id")]
    [InlineData("take-mismatch")]
    [InlineData("source-mismatch")]
    [InlineData("mute-mismatch")]
    [InlineData("missing-placement")]
    [InlineData("saved-position-mismatch")]
    public void ScenarioMove_RejectsStaleOrUnrepresentableScenario(string defect)
    {
        var scene = Sequential();
        var items = scene.ScenarioItems!;
        scene = defect switch
        {
            "missing-items" => scene with { ScenarioItems = null },
            "inconsistent-project" => scene with { ScenarioConsistent = false },
            "gap-mismatch" => scene with { ScenarioItems = [items[0] with { GapBeforeFrames = 1 }, items[1], items[2]] },
            "unknown-duration" => scene with { ScenarioItems = [items[0] with { DurationFrames = null }, items[1], items[2]] },
            "negative-gap" => scene with { ScenarioItems = [items[0] with { GapBeforeFrames = -1 }, items[1], items[2]] },
            "duplicate-id" => scene with { ScenarioItems = [items[0], items[1] with { Id = items[0].Id }, items[2]] },
            "take-mismatch" => scene with { ScenarioItems = [items[0] with { TakeId = "other-take" }, items[1], items[2]] },
            "source-mismatch" => scene with { ScenarioItems = [items[0] with { SourceOutFrame = 18 }, items[1], items[2]] },
            "mute-mismatch" => scene with { ScenarioItems = [items[0] with { MuteNativeAudio = false }, items[1], items[2]] },
            "missing-placement" => scene with { ScenarioItems = [items[0], items[2]] },
            "saved-position-mismatch" => scene with { SegmentClips = [scene.SegmentClips![0], scene.SegmentClips[1] with { StartFrame = 13 }, scene.SegmentClips[2]] },
            _ => throw new ArgumentOutOfRangeException(nameof(defect))
        };
        var move = new TimelineMove(scene);

        Assert.False(move.CanMove);
        var draft = move.Move(24);

        Assert.False(draft.Valid);
        Assert.False(draft.Changed);
        Assert.Equal(0, draft.TimelineStart);
        Assert.Equal(scene.SegmentClips, draft.Clips);
    }

    private static Clip Video(string id, int start, int duration) =>
        new(id, "segment", "source-" + id, "take-" + id, "Title " + id, start, duration, 0, null, 1);

    private static ScenarioPlacement Placement(Clip clip, int gap) => new(clip.Id, clip.BlockId, clip.TakeId,
        clip.DurationFrames, gap, clip.SourceInFrame, clip.SourceOutFrame, clip.Speed, clip.AnchorFrame, clip.MuteNativeAudio);

    private static SceneSnapshot Plain(Segment window, Clip selected, Clip[] clips) =>
        new("QA", 5, 24, window, selected, selected.StoryTitle!, [], [], clips);

    private static SceneSnapshot Story(Segment window, Clip selected, Clip[] clips, ScenarioPlacement[] items) =>
        Plain(window, selected, clips) with { ScenarioItems = items, ScenarioActive = true };

    private static SceneSnapshot Sequential()
    {
        var first = Video("a", 0, 8) with { SourceInFrame = 2.5, SourceOutFrame = 17.5, Speed = 2, MuteNativeAudio = true };
        var second = Video("b", 12, 6);
        var third = Video("c", 20, 4);
        return Story(new("segment", "Test", 0, 48), first, [first, second, third],
            [Placement(first, 0), Placement(second, 4), Placement(third, 2)]);
    }
}
