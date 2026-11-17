using System.Net;
using TrueMoon.Argentis;
using TrueMoon.Tales.Studio.Tests.Support;
using Xunit;

namespace TrueMoon.Tales.Studio.Tests.Raster;

public sealed class RasterSceneTests
{
    [Theory]
    [InlineData(1f, "escape")]
    [InlineData(1.5f, "escape")]
    [InlineData(2f, "escape")]
    [InlineData(1f, "focus")]
    [InlineData(1f, "resize")]
    public void Drag_RendersDraft_ThenCancellationRestoresPixelsAndDataWithoutWriting(float scale, string reason)
    {
        using var fixture = new RasterSceneFixture("scene-cancel-" + reason, scale);
        var before = fixture.Server.Current;
        var clip = fixture.Strip.ClipBounds;
        var baseline = fixture.Host.Capture("before", new { revision = fixture.Server.Revision });
        RasterAssert.Dimensions(baseline, (int)(1280 * scale), (int)(820 * scale));
        var grip = RasterAssert.Pixel(baseline, clip.X + 4, clip.Y + clip.Height - 8, scale);
        var (_, y, target) = fixture.DragTwoFrames();
        var draft = fixture.Host.Capture("draft", new { start = fixture.Model.TimelineStart });
        Assert.NotEqual(grip, RasterAssert.Pixel(draft, clip.X + 4, clip.Y + clip.Height - 8, scale));
        Assert.Equal(before, fixture.Server.Current);
        Assert.Empty(fixture.Server.Posts);
        Assert.Empty(fixture.Frames.Calls);
        switch (reason)
        {
            case "escape": fixture.Host.Input(new(InputKind.KeyDown, Key: UiKey.Escape)); break;
            case "focus": fixture.Host.SetActive(false); break;
            case "resize": fixture.Host.Resize(1180, 760, scale); break;
        }
        Assert.False(fixture.Host.Input(new(InputKind.PointerUp, target, y)).PointerCaptured);
        Assert.False(fixture.Strip.IsDragging);
        Assert.False(fixture.Model.Moving);
        Assert.False(fixture.Model.Modified);
        Assert.Equal(before, fixture.Server.Current);
        Assert.Equal(5, fixture.Server.Revision);
        Assert.Empty(fixture.Server.Posts);
        Assert.Empty(fixture.Frames.Calls);
        if (reason != "resize")
        {
            var cancelled = fixture.Host.Capture("cancelled", new { revision = fixture.Server.Revision });
            Assert.Equal(grip, RasterAssert.Pixel(cancelled, clip.X + 4, clip.Y + clip.Height - 8, scale));
        }
    }

    [Theory]
    [InlineData(1f, false)]
    [InlineData(1.5f, false)]
    [InlineData(2f, false)]
    [InlineData(1f, true)]
    public void Drag_ReleaseCommitsOnceOrKeepsConflictDraft_WithRealRaster(float scale, bool conflict)
    {
        using var fixture = new RasterSceneFixture(conflict ? "scene-conflict" : "scene-save", scale);
        var before = fixture.Server.Current;
        fixture.Host.Capture("before", new { revision = fixture.Server.Revision });
        var (_, y, target) = fixture.DragTwoFrames();
        fixture.Host.Capture("draft", new { start = fixture.Model.TimelineStart });
        if (conflict) fixture.Server.Revision++;
        fixture.Host.Input(new(InputKind.PointerUp, target, y));
        fixture.Host.PumpUntil(() => fixture.Model.Idle && !fixture.Model.Saving);
        var request = Assert.Single(fixture.Server.Posts);
        Assert.Equal(5, request["expected_revision"]!.GetValue<int>());
        var payload = request["payload"]!.AsObject();
        Assert.Equal(new[] { "id", "start_frame" }, payload.Select(pair => pair.Key).Order());
        Assert.Equal(before.Id, payload["id"]!.GetValue<string>());
        Assert.Equal(2, payload["start_frame"]!.GetValue<int>());
        Assert.Empty(fixture.Frames.Calls);
        if (conflict)
        {
            Assert.True(fixture.Model.Stale);
            Assert.True(fixture.Model.Modified);
            Assert.False(fixture.Model.CanSave);
            Assert.Equal(before, fixture.Server.Current);
        }
        else
        {
            Assert.False(fixture.Model.HasError, fixture.Model.Status);
            Assert.False(fixture.Model.Modified);
            Assert.Equal(before with { StartFrame = 2 }, fixture.Server.Current);
            Assert.Equal(6, fixture.Model.Scene!.Revision);
        }
        Assert.Equal(6, fixture.Server.Revision);
        var final = fixture.Host.Capture("result", new { stale = fixture.Model.Stale, revision = fixture.Server.Revision });
        var repeat = fixture.Host.Capture("repeat", new { stale = fixture.Model.Stale });
        Assert.Equal(final, repeat);
        Assert.Single(fixture.Server.Posts);
    }
}
