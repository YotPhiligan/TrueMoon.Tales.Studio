using System.Net;
using TrueMoon.Alloy.Runtime;
using TrueMoon.Argentis;
using TrueMoon.Tales.Studio.Editing;
using TrueMoon.Tales.Studio.Models;
using TrueMoon.Tales.Studio.Presentation.Scene;
using TrueMoon.Tales.Studio.Tests.Support;
using Xunit;

namespace TrueMoon.Tales.Studio.Tests.Presentation.Scene;

public sealed class TimelineInteractionTests
{
    [Fact]
    public void Trim_ReleaseWritesOnce_WithRevision_AndReopensSavedBounds()
    {
        using var fixture = new InteractionFixture();
        var strip = new SceneStrip(fixture.Model, () => { });
        using var session = new UiSession(strip, new HeadlessSurface(), new(120, 100));
        session.Update();
        var down = session.HandleInput(new(InputKind.PointerDown, 3, 40));
        Assert.True(down.PointerCaptured);
        session.HandleInput(new(InputKind.PointerMove, 23, 40));
        Assert.Equal(2, fixture.Model.Start);
        Assert.Equal(2, fixture.Model.TimelineStart);
        Assert.Equal(6, fixture.Model.TimelineDuration);
        Assert.Empty(fixture.Server.Posts);
        var up = session.HandleInput(new(InputKind.PointerUp, 23, 40));
        Assert.False(up.PointerCaptured);
        session.HandleInput(new(InputKind.PointerUp, 23, 40));
        var request = Assert.Single(fixture.Server.Posts);
        Assert.Equal("update_clip", request["action"]!.GetValue<string>());
        Assert.Equal(5, request["expected_revision"]!.GetValue<int>());
        Assert.Equal(2, request["payload"]!["start_frame"]!.GetValue<int>());
        fixture.Model.Poll();
        Assert.Equal(6, fixture.Model.Scene!.Revision);
        Assert.False(fixture.Model.Modified);
        fixture.Model.Open(); fixture.Model.Poll();
        Assert.Equal((2, 6, 2d, 8d), (fixture.Model.Scene.Clip.StartFrame, fixture.Model.Scene.Clip.DurationFrames, fixture.Model.Start, fixture.Model.End));
    }

    [Theory]
    [InlineData("escape")]
    [InlineData("focus-lost")]
    [InlineData("capture-lost")]
    public void Trim_CancelRestoresBounds_WithoutWriting(string reason)
    {
        using var fixture = new InteractionFixture();
        var strip = new SceneStrip(fixture.Model, () => { });
        using var session = new UiSession(strip, new HeadlessSurface(), new(120, 100));
        session.Update();
        session.HandleInput(new(InputKind.PointerDown, 77, 40));
        session.HandleInput(new(InputKind.PointerMove, 57, 40));
        Assert.Equal(6, fixture.Model.End);
        if (reason == "escape") session.HandleInput(new(InputKind.KeyDown, Key: UiKey.Escape));
        else if (reason == "focus-lost") session.HandleInput(new(InputKind.FocusLost));
        else strip.OnInputCancelled();
        Assert.False(strip.IsDragging);
        Assert.False(fixture.Model.Trimming);
        Assert.False(fixture.Model.Modified);
        Assert.Equal((0d, 8d, 0), (fixture.Model.Start, fixture.Model.End, fixture.Model.TimelineStart));
        session.HandleInput(new(InputKind.PointerUp, 57, 40));
        Assert.Empty(fixture.Server.Posts);
    }

    [Fact]
    public void Trim_OutsideReleaseClampsToOneFrame_AndWritesOnce()
    {
        using var fixture = new InteractionFixture();
        var strip = new SceneStrip(fixture.Model, () => { });
        using var session = new UiSession(strip, new HeadlessSurface(), new(120, 100));
        session.Update();
        session.HandleInput(new(InputKind.PointerDown, 77, 40));
        var up = session.HandleInput(new(InputKind.PointerUp, -200, -200));
        Assert.True(up.Handled); Assert.False(up.PointerCaptured);
        Assert.Single(fixture.Server.Posts);
        fixture.Model.Poll();
        Assert.Equal(1, fixture.Model.Scene!.Clip.DurationFrames);
        Assert.Equal(1, fixture.Model.End);
    }

    [Fact]
    public void Trim_UnchangedGestureDoesNotWrite()
    {
        using var fixture = new InteractionFixture();
        var strip = new SceneStrip(fixture.Model, () => { });
        using var session = new UiSession(strip, new HeadlessSurface(), new(120, 100));
        session.Update();
        session.HandleInput(new(InputKind.PointerDown, 77, 40));
        session.HandleInput(new(InputKind.PointerMove, 75, 40));
        session.HandleInput(new(InputKind.PointerUp, 77, 40));
        Assert.Empty(fixture.Server.Posts);
        Assert.False(fixture.Model.Modified);
        Assert.False(strip.IsDragging);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void Trim_InterruptedGeometryOrInvalidReleaseCancelsWithoutWriting(bool resized)
    {
        using var fixture = new InteractionFixture();
        var strip = new SceneStrip(fixture.Model, () => { });
        using var session = new UiSession(strip, new HeadlessSurface(), new(120, 100));
        session.Update();
        session.HandleInput(new(InputKind.PointerDown, 77, 40));
        session.HandleInput(new(InputKind.PointerMove, 57, 40));
        Assert.True(fixture.Model.Modified);
        if (resized) strip.Arrange(new(0, 0, 180, 100));
        session.HandleInput(new(InputKind.PointerUp, resized ? 57 : float.NaN, 40));
        Assert.Empty(fixture.Server.Posts);
        Assert.False(fixture.Model.Modified);
        Assert.False(strip.IsDragging);
        Assert.Equal(8, fixture.Model.End);
    }

    [Fact]
    public void Trim_PreservesAnExistingManualDraft()
    {
        using var fixture = new InteractionFixture();
        fixture.Model.Step(1); fixture.Model.Poll();
        fixture.Model.SetBoundary(true);
        Assert.Equal(1, fixture.Model.Start);
        Assert.False(fixture.Model.BeginTrim(TrimEdge.End));
        Assert.Equal(1, fixture.Model.Start);
        Assert.True(fixture.Model.Modified);
        Assert.Empty(fixture.Server.Posts);
    }

    [Theory]
    [InlineData(HttpStatusCode.Conflict)]
    [InlineData(HttpStatusCode.ServiceUnavailable)]
    public void Trim_WriteFailureKeepsDraft_AndDoesNotRetry(HttpStatusCode failure)
    {
        using var fixture = new InteractionFixture();
        fixture.Server.RejectWrite = failure;
        Assert.True(fixture.Model.BeginTrim(TrimEdge.End));
        fixture.Model.MoveTrim(-2);
        fixture.Model.CompleteTrim();
        fixture.Model.Poll();
        Assert.True(fixture.Model.HasError);
        Assert.True(fixture.Model.Stale);
        Assert.True(fixture.Model.Modified);
        Assert.Equal(6, fixture.Model.End);
        Assert.Equal(8, fixture.Model.Scene!.Clip.SourceEnd);
        fixture.Model.Save(); fixture.Model.CompleteTrim();
        Assert.Single(fixture.Server.Posts);
        Assert.False(fixture.Model.CanBeginTrim);
    }

    [Fact]
    public void Scrub_CoalescesMoves_AndPresentsFinalFrameWithoutWriting()
    {
        using var fixture = new InteractionFixture();
        fixture.Frames.Calls.Clear(); fixture.Frames.Block = true;
        var strip = new SceneStrip(fixture.Model, () => { });
        using var session = new UiSession(strip, new HeadlessSurface(), new(120, 100));
        session.Update();
        Assert.True(session.HandleInput(new(InputKind.PointerDown, 40, 12)).PointerCaptured);
        session.HandleInput(new(InputKind.PointerMove, 60, 12));
        session.HandleInput(new(InputKind.PointerMove, 20, 12));
        Assert.Single(fixture.Frames.Calls);
        Assert.Equal(2, fixture.Model.TimelineCursor);
        fixture.Frames.Calls[0].Completion.SetResult(FakeFrames.Pixels(4));
        fixture.Model.Poll();
        Assert.Equal(0, fixture.Model.FrameIndex); // Obsolete frame 4 must not flash.
        Assert.Equal(2, fixture.Frames.Calls.Count);
        Assert.Equal(2, fixture.Frames.Calls[1].Frame);
        fixture.Frames.Calls[1].Completion.SetResult(FakeFrames.Pixels(2));
        fixture.Model.Poll();
        Assert.Equal(2, fixture.Model.FrameIndex);
        Assert.Equal(2, fixture.Model.Pixels![0]);
        Assert.False(session.HandleInput(new(InputKind.PointerUp, 20, 12)).PointerCaptured);
        Assert.Empty(fixture.Server.Posts);
    }
}
