using TrueMoon.Tales.Studio.Editing;
using TrueMoon.Tales.Studio.Models;
using Xunit;

namespace TrueMoon.Tales.Studio.Tests.Editing;

public sealed class TimelineTrimTests
{
    private static SceneSnapshot Scene(Clip clip, params Clip[] neighbors) => new("QA", 5, 24, new("segment", "Test", 0, 40), clip,
        "Clip", [], [], [clip, .. neighbors]);

    [Theory]
    [InlineData(2, 12, 6, 8)]
    [InlineData(-100, 8, 10, 0)]
    [InlineData(100, 17, 1, 18)]
    public void LeftTrim_MapsSpeedAndKeepsRightEdge(int delta, int start, int duration, double source)
    {
        var clip = new Clip("a", "segment", "block", "take", null, 10, 8, 4, 20, 2);
        var draft = new TimelineTrim(Scene(clip), 100).Move(TrimEdge.Start, delta);
        Assert.Equal(new TrimDraft(start, duration, source, 20), draft);
        Assert.Equal(18, draft.TimelineStart + draft.Duration);
    }

    [Fact]
    public void LeftTrim_RespectsPreviousPlaceholderAndAnchor()
    {
        var clip = new Clip("a", "segment", "block", "take", null, 10, 8, 4, 20, 2);
        var previous = new Clip("placeholder", "segment", "block", null, null, 0, 9, 0, null, null);
        Assert.Equal(9, new TimelineTrim(Scene(clip, previous), 100).Move(TrimEdge.Start, -100).TimelineStart);
        var locked = clip with { AnchorFrame = 10 };
        var trim = new TimelineTrim(Scene(locked), 100);
        Assert.False(trim.CanStart);
        Assert.Equal(new TrimDraft(10, 8, 4, 20), trim.Move(TrimEdge.Start, 2));
    }

    [Theory]
    [InlineData(-100, 1, 6)]
    [InlineData(2, 10, 24)]
    [InlineData(100, 11, 25)]
    public void RightTrim_ClampsDurationToMediaCapacity(int delta, int duration, double end)
    {
        var clip = new Clip("a", "segment", "block", "take", null, 10, 8, 4, 20, 2);
        Assert.Equal(new TrimDraft(10, duration, 4, end), new TimelineTrim(Scene(clip), 25).Move(TrimEdge.End, delta));
    }

    [Fact]
    public void RightTrim_RespectsNextClipAndPartialOriginalEnd()
    {
        var clip = new Clip("a", "segment", "block", "take", null, 10, 8, 4, 19, 2);
        var next = new Clip("next", "segment", "block", null, null, 20, 4, 0, null, null);
        var trim = new TimelineTrim(Scene(clip, next), 100);
        Assert.Equal(19, trim.Move(TrimEdge.End, 0).SourceEnd);
        Assert.Equal(new TrimDraft(10, 10, 4, 24), trim.Move(TrimEdge.End, 100));
    }
}
