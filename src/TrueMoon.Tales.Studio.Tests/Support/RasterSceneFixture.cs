using SkiaSharp;
using TrueMoon;
using TrueMoon.Cobalt;
using TrueMoon.Dependencies;
using TrueMoon.Tales.Studio.Configuration;
using TrueMoon.Tales.Studio.Infrastructure;
using TrueMoon.Tales.Studio.Media;
using TrueMoon.Tales.Studio.Presentation.Scene;
using Xunit;

namespace TrueMoon.Tales.Studio.Tests.Support;

internal sealed class RasterSceneFixture : IDisposable
{
    private readonly IApp _app;
    internal FakeStudio Server { get; } = new();
    internal FakeFrames Frames { get; } = new();
    internal SceneViewModel Model { get; }
    internal RasterWindow<SceneView> Host { get; }
    internal SceneStrip Strip { get; }

    internal RasterSceneFixture(string scenario, float scale)
    {
        Server.Window = Server.Window with { Name = "Тестовая сцена · Ёж перед рассветом" };
        _app = App.Builder(_ => { }).Setup(context => context.UseStudioServices().Services(r => r
            .Instance(new StudioOptions(new("http://127.0.0.1:12345/"), "qa", "unused-ffmpeg"))
            .Singleton<StudioHttpClient>(resolver => new(resolver.Resolve<StudioOptions>()!, Server))
            .Instance<IFrameDecoder>(Frames)
            .Singleton<SceneViewModel>(resolver => new(resolver.Resolve<StudioHttpClient>()!, Frames)))).Build();
        try
        {
            Assert.IsType<CobaltServiceResolver>(_app.Services);
            Model = _app.Services.Resolve<SceneViewModel>()!;
            var view = _app.Services.Resolve<IFactory<SceneView>>()!.Create()!;
            Host = new(view, scenario, 1280, 820, scale);
            Host.PumpUntil(() => Model.Loaded && Model.Idle);
            Strip = Assert.Single(RasterWindow<SceneView>.Walk(view).OfType<SceneStrip>());
            Assert.True(Strip.Bounds.Width > 100);
            Frames.Calls.Clear();
        }
        catch { try { Host?.Dispose(); } finally { _app.Dispose(); } throw; }
    }

    internal (float X, float Y, float Target) DragTwoFrames()
    {
        var clip = Strip.ClipBounds;
        var x = clip.X + clip.Width / 2; var y = clip.Y + clip.Height / 2;
        var target = x + Strip.Bounds.Width * 2 / Model.Scene!.Segment.DurationFrames;
        Assert.True(Host.Input(new(TrueMoon.Argentis.InputKind.PointerDown, x, y)).PointerCaptured);
        Host.Input(new(TrueMoon.Argentis.InputKind.PointerMove, target, y));
        Assert.True(Model.Moving);
        Assert.Equal(2, Model.TimelineStart);
        return (x, y, target);
    }

    public void Dispose() { try { Host.Dispose(); } finally { _app.Dispose(); } }
}

internal static class RasterAssert
{
    internal static void TextInk(byte[] png, TrueMoon.Argentis.Rect bounds, float scale)
    {
        using var bitmap = SKBitmap.Decode(png);
        // This element has no fill/border: contrast inside its geometry must come from glyphs.
        var background = bitmap.GetPixel(bitmap.Width - 4, bitmap.Height - 4);
        var ink = 0;
        for (var y = (int)Math.Ceiling(bounds.Y * scale); y < Math.Floor((bounds.Y + bounds.Height) * scale); y++)
            for (var x = (int)Math.Ceiling(bounds.X * scale); x < Math.Floor((bounds.X + bounds.Width) * scale); x++)
            {
                var pixel = bitmap.GetPixel(x, y);
                if (pixel.Alpha > 128 && Math.Abs(pixel.Red - background.Red) > 40) ink++;
            }
        Assert.True(ink > 50 * scale * scale, "The title region must contain rasterized text.");
    }

    internal static void Dimensions(byte[] png, int width, int height)
    {
        using var decoded = SKBitmap.Decode(png);
        Assert.NotNull(decoded);
        Assert.Equal(width, decoded.Width); Assert.Equal(height, decoded.Height);
        Assert.Contains(decoded.Pixels, pixel => pixel.Alpha != 0);
    }

    internal static SKColor Pixel(byte[] png, float x, float y, float scale)
    {
        using var bitmap = SKBitmap.Decode(png);
        return bitmap.GetPixel((int)Math.Floor(x * scale), (int)Math.Floor(y * scale));
    }
}
