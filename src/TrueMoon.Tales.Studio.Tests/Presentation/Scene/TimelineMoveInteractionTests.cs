using System.Net;
using System.Text;
using TrueMoon.Alloy.Runtime;
using TrueMoon.Argentis;
using TrueMoon.Tales.Studio.Models;
using TrueMoon.Tales.Studio.Presentation.Scene;
using TrueMoon.Tales.Studio.Tests.Support;
using Xunit;

namespace TrueMoon.Tales.Studio.Tests.Presentation.Scene;

public sealed class TimelineMoveInteractionTests
{
    [Fact]
    public void Move_DraftsWithoutIO_ThenWritesOnlyPositionOnceWithOriginalRevision()
    {
        using var fixture = new InteractionFixture();
        var before = fixture.Server.Current;
        fixture.Frames.Calls.Clear();
        using var strip = new SceneStrip(fixture.Model, () => { });
        using var session = new UiSession(strip, new HeadlessSurface(), new(120, 100));
        session.Update();
        Assert.True(session.HandleInput(new(InputKind.PointerDown, 40, 40)).PointerCaptured);
        Assert.False(fixture.Model.Moving);
        session.HandleInput(new(InputKind.PointerMove, 60, 40));
        Assert.True(fixture.Model.Moving);
        Assert.True(fixture.Model.Modified);
        Assert.False(fixture.Model.CanSave);
        Assert.False(fixture.Model.CanReload);
        Assert.Equal(2, fixture.Model.TimelineStart);
        Assert.Equal(before, fixture.Server.Current);
        Assert.Empty(fixture.Server.Posts);
        Assert.Empty(fixture.Frames.Calls);
        Assert.False(session.HandleInput(new(InputKind.PointerUp, 60, 40)).PointerCaptured);
        session.HandleInput(new(InputKind.PointerUp, 90, 40));
        var request = Assert.Single(fixture.Server.Posts);
        Assert.Equal(5, request["expected_revision"]!.GetValue<int>());
        Assert.Equal("update_clip", request["action"]!.GetValue<string>());
        var payload = request["payload"]!.AsObject();
        Assert.Equal(new[] { "id", "start_frame" }, payload.Select(pair => pair.Key).Order().ToArray());
        Assert.Equal(before.Id, payload["id"]!.GetValue<string>());
        Assert.Equal(2, payload["start_frame"]!.GetValue<int>());
        fixture.Model.Poll();
        Assert.False(fixture.Model.HasError, fixture.Model.Status);
        Assert.False(fixture.Model.Modified);
        Assert.Equal(before with { StartFrame = 2 }, fixture.Server.Current);
        Assert.Equal(6, fixture.Model.Scene!.Revision);
        fixture.Model.Open(); fixture.Model.Poll();
        Assert.Equal(2, fixture.Model.Scene.Clip.StartFrame);
        Assert.Single(fixture.Server.Posts);
    }

    [Theory]
    [InlineData("escape")]
    [InlineData("focus")]
    [InlineData("capture")]
    [InlineData("resize")]
    [InlineData("hidden")]
    [InlineData("disabled")]
    [InlineData("invalid-up")]
    [InlineData("dispose")]
    public void Move_CancellationRestoresSnapshotAndReleasesCaptureWithoutWriting(string reason)
    {
        using var fixture = new InteractionFixture();
        using var strip = new SceneStrip(fixture.Model, () => { });
        using var session = new UiSession(strip, new HeadlessSurface(), new(120, 100));
        session.Update();
        session.HandleInput(new(InputKind.PointerDown, 40, 40));
        session.HandleInput(new(InputKind.PointerMove, 60, 40));
        Assert.Equal(2, fixture.Model.TimelineStart);
        switch (reason)
        {
            case "escape": session.HandleInput(new(InputKind.KeyDown, Key: UiKey.Escape)); break;
            case "focus": session.HandleInput(new(InputKind.FocusLost)); break;
            case "capture": strip.OnInputCancelled(); break;
            case "resize": strip.Arrange(new(0, 0, 200, 100)); break;
            case "hidden": strip.IsVisible = false; break;
            case "disabled": strip.IsEnabled = false; break;
            case "dispose": strip.Dispose(); break;
        }
        session.HandleInput(new(InputKind.PointerUp, reason == "invalid-up" ? float.NaN : 60, 40));
        Assert.False(strip.IsDragging);
        Assert.False(fixture.Model.Moving);
        Assert.False(fixture.Model.Modified);
        Assert.Equal(0, fixture.Model.TimelineStart);
        Assert.Equal((0d, 8d), (fixture.Model.Start, fixture.Model.End));
        Assert.Empty(fixture.Server.Posts);
        Assert.Equal(5, fixture.Server.Revision);
    }

    [Theory]
    [InlineData(40)]
    [InlineData(42)]
    [InlineData(40, true)]
    public void Move_ClickJitterOrReturnToOriginDoesNotWrite(int release, bool excursion = false)
    {
        using var fixture = new InteractionFixture();
        using var strip = new SceneStrip(fixture.Model, () => { });
        using var session = new UiSession(strip, new HeadlessSurface(), new(120, 100));
        session.Update();
        session.HandleInput(new(InputKind.PointerDown, 40, 40));
        if (excursion) session.HandleInput(new(InputKind.PointerMove, 60, 40));
        session.HandleInput(new(InputKind.PointerUp, release, 40));
        fixture.Model.Poll();
        Assert.False(fixture.Model.Modified);
        Assert.False(fixture.Model.Moving);
        Assert.Equal(0, fixture.Model.TimelineStart);
        Assert.Empty(fixture.Server.Posts);
        if (!excursion) Assert.Equal(4, fixture.Model.FrameIndex);
    }

    [Fact]
    public void Move_OutsideReleaseClampsPositionButPreservesTheSourceAndDuration()
    {
        using var fixture = new InteractionFixture();
        var before = fixture.Server.Current;
        using var strip = new SceneStrip(fixture.Model, () => { });
        using var session = new UiSession(strip, new HeadlessSurface(), new(120, 100));
        session.Update();
        session.HandleInput(new(InputKind.PointerDown, 40, 40));
        Assert.False(session.HandleInput(new(InputKind.PointerUp, 2000, -200)).PointerCaptured);
        fixture.Model.Poll();
        Assert.Equal(before with { StartFrame = 4 }, fixture.Server.Current);
        Assert.Single(fixture.Server.Posts);
    }

    [Theory]
    [InlineData("anchor")]
    [InlineData("draft")]
    [InlineData("alternative")]
    [InlineData("inconsistent-story")]
    public void Move_RefusesUneditableStateAndPreservesIt(string reason)
    {
        using var fixture = new InteractionFixture(configure: server =>
        {
            if (reason == "anchor") server.Current = server.Current with { AnchorFrame = 0 };
            if (reason == "inconsistent-story") server.ScenarioItems = [new("missing", "block", null, 8, 0, 0)];
        });
        if (reason == "draft") { fixture.Model.Step(1); fixture.Model.Poll(); fixture.Model.SetBoundary(true); }
        if (reason == "alternative") { fixture.Model.SelectTake(1); fixture.Model.Poll(); }
        var before = (fixture.Model.Start, fixture.Model.End, fixture.Model.TimelineStart, fixture.Model.TakeIndex, fixture.Model.Modified);
        Assert.False(fixture.Model.BeginMove());
        Assert.Equal(before, (fixture.Model.Start, fixture.Model.End, fixture.Model.TimelineStart, fixture.Model.TakeIndex, fixture.Model.Modified));
        Assert.Empty(fixture.Server.Posts);
    }

    [Theory]
    [InlineData(HttpStatusCode.Conflict)]
    [InlineData(HttpStatusCode.ServiceUnavailable)]
    [InlineData(HttpStatusCode.UnprocessableEntity)]
    public void Move_FailedWriteKeepsDraftAndBlocksRetryUntilDiscardAndReload(HttpStatusCode failure)
    {
        using var fixture = new InteractionFixture();
        fixture.Server.RejectWrite = failure;
        Assert.True(fixture.Model.BeginMove());
        fixture.Model.MoveClip(2);
        fixture.Model.CompleteMove(); fixture.Model.Poll();
        Assert.True(fixture.Model.HasError);
        Assert.True(fixture.Model.Stale);
        Assert.True(fixture.Model.Modified);
        Assert.Equal(2, fixture.Model.TimelineStart);
        Assert.Equal(0, fixture.Model.Scene!.Clip.StartFrame);
        fixture.Model.Save(); fixture.Model.CompleteMove();
        Assert.False(fixture.Model.BeginMove());
        fixture.Model.Open();
        Assert.Single(fixture.Server.Posts);
        fixture.Model.Cancel();
        Assert.False(fixture.Model.Modified);
        Assert.True(fixture.Model.Stale);
        fixture.Server.RejectWrite = null;
        fixture.Server.Revision = 9;
        fixture.Model.Open(); fixture.Model.Poll();
        Assert.False(fixture.Model.Stale);
        Assert.Equal(9, fixture.Model.Scene.Revision);
        Assert.True(fixture.Model.CanBeginMove);
    }

    [Fact]
    public void Move_PendingAndMalformedResponseNeverCauseASecondWrite()
    {
        using var fixture = new InteractionFixture();
        fixture.Server.PendingWrite = new(TaskCreationOptions.RunContinuationsAsynchronously);
        Assert.True(fixture.Model.BeginMove()); fixture.Model.MoveClip(2); fixture.Model.CompleteMove();
        Assert.True(fixture.Model.Saving);
        fixture.Model.Save(); fixture.Model.Cancel(); fixture.Model.Open();
        Assert.Single(fixture.Server.Posts);
        fixture.Server.PendingWrite.SetResult(new(HttpStatusCode.OK) { Content = new StringContent("not json", Encoding.UTF8, "application/json") });
        Assert.True(SpinWait.SpinUntil(() => { fixture.Model.Poll(); return !fixture.Model.Saving; }, TimeSpan.FromSeconds(3)));
        Assert.True(fixture.Model.Stale);
        Assert.True(fixture.Model.Modified);
        Assert.True(fixture.Model.HasError);
        Assert.Equal(2, fixture.Model.TimelineStart);
        fixture.Model.Save();
        Assert.Single(fixture.Server.Posts);
    }

    [Fact]
    public void Move_OverlapIsVisibleButReleaseRestoresWithoutWriting()
    {
        using var fixture = new InteractionFixture(configure: server =>
            server.OtherClips = [new("placeholder", "segment", "block", null, "Без видео", 8, 2, 0, null, null)]);
        Assert.True(fixture.Model.BeginMove()); fixture.Model.MoveClip(2);
        Assert.True(fixture.Model.HasError);
        Assert.Equal(2, fixture.Model.TimelineStart);
        Assert.Equal(8, fixture.Model.PreviewClips.Single(clip => clip.Id == "placeholder").StartFrame);
        fixture.Model.CompleteMove();
        Assert.False(fixture.Model.Modified);
        Assert.Equal(0, fixture.Model.TimelineStart);
        Assert.Empty(fixture.Server.Posts);
    }
}
