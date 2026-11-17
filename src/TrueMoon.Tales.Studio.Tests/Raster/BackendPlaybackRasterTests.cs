using System.Diagnostics;
using SkiaSharp;
using TrueMoon.Argentis;
using TrueMoon.Alloy.Rendering.Skia;
using TrueMoon.Dependencies;
using TrueMoon.Tales.Studio.Backend;
using TrueMoon.Tales.Studio.Media;
using TrueMoon.Tales.Studio.Presentation.Scene;
using TrueMoon.Tales.Studio.Tests.Backend;
using TrueMoon.Tales.Studio.Tests.Support;
using Xunit;

namespace TrueMoon.Tales.Studio.Tests.Raster;

public sealed class BackendPlaybackRasterTests
{
    [BackendMediaTheory]
    [InlineData(1f)]
    [InlineData(1.5f)]
    [InlineData(2f)]
    public async Task RoutedPlayPauseSeekAndEnd_UseRealEneritVideoAndMixedPcm_WithoutWriting(float scale)
    {
        await using var fixture = new BackendPlaybackFixture();
        await fixture.StartAsync();
        var first = await fixture.ReferenceFrameAsync(5);
        var sixth = await fixture.ReferenceFrameAsync(14);
        var seek = await fixture.ReferenceFrameAsync(42);
        var last = await fixture.ReferenceFrameAsync(153);
        var audio = await fixture.ReferenceAudioAsync();
        var model = fixture.App.Services.Resolve<SceneViewModel>()!;
        using var host = Host(fixture, "backend-playback", scale);
        Ready(host, model);
        var owner = fixture.App.Services.Resolve<BackendProcessFactory>()!.Owned;
        Assert.Equal(12, model.Scene!.Revision);
        Assert.Equal(fixture.Baseline.Fingerprint, model.Scene.Fingerprint);
        Assert.Equal(first, model.Pixels); Assert.True(model.CanBeginTrim);
        var play = Play(host);
        host.Session.Focus(play);
        Assert.True(host.Input(new(InputKind.KeyDown, Key: UiKey.Space)).Handled);
        Started(host, model, fixture);
        Assert.Equal(0, Assert.Single(fixture.SoundStarts));
        Assert.Equal(first, model.Pixels);
        AssertAudio(audio, Assert.Single(fixture.PreparedSounds));
        AssertPreview(host, first, scale, "playing-first");

        fixture.Sounds[^1].PlayedSamples = 12000; // six project frames, independent of decode throughput
        host.PumpUntil(() => model.FrameIndex == 14 || model.HasError, TimeSpan.FromSeconds(15));
        Assert.False(model.HasError, model.Status);
        Assert.Equal(18, model.TimelineCursor);
        Assert.Equal(sixth, model.Pixels);
        host.Click(Play(host));
        Assert.False(model.Playing); Assert.True(fixture.Sounds[0].Disposed);
        Released(host, fixture);
        AssertPreview(host, sixth, scale, "paused-sixth");

        host.Session.Focus(Play(host));
        Assert.True(host.Input(new(InputKind.KeyDown, Key: UiKey.Space)).Handled);
        Started(host, model, fixture);
        Assert.Equal(new[] { 0, 6 }, fixture.SoundStarts);
        Assert.Equal(14, model.FrameIndex); Assert.Equal(sixth, model.Pixels);
        Seek(host, model, 24);
        Assert.False(model.Playing); Assert.True(fixture.Sounds[1].Disposed);
        host.PumpUntil(() => model.Idle && model.FrameIndex == 42 || model.HasError, TimeSpan.FromSeconds(15));
        Assert.False(model.HasError, model.Status); Assert.Equal(seek, model.Pixels);
        Released(host, fixture);
        AssertPreview(host, seek, scale, "seek-local24");

        host.Click(Play(host));
        Started(host, model, fixture);
        Assert.Equal(new[] { 0, 6, 24 }, fixture.SoundStarts);
        Assert.Equal(42, model.FrameIndex); Assert.Equal(seek, model.Pixels);
        fixture.Sounds[^1].PlayedSamples = 144000; // remaining72frames at24fps, two thousand sampleframes each
        host.PumpUntil(() => !model.Playing || model.HasError, TimeSpan.FromSeconds(15));
        Assert.False(model.HasError, model.Status);
        Assert.Equal("Конец клипа", model.MediaStatus);
        Assert.Equal(153, model.FrameIndex); Assert.Equal(last, model.Pixels);
        Assert.Equal(107, model.TimelineCursor);
        Assert.All(fixture.Sounds, sound => Assert.True(sound.Disposed));
        Assert.All(fixture.Sounds, sound => Assert.Equal(1, sound.Plays));
        AssertPreview(host, last, scale, "clip-ended");
        Released(host, fixture);
        Assert.Same(owner, fixture.App.Services.Resolve<BackendProcessFactory>()!.Owned);
        Assert.True(owner!.IsServing(fixture.Baseline.Identity.ProcessId));
        Assert.False(model.Modified); Assert.Equal(12, model.Scene.Revision);
        fixture.AssertUnchanged();
    }

    [BackendMediaTheory]
    [InlineData(true, false, false)]
    [InlineData(false, true, false)]
    [InlineData(false, false, true)]
    [InlineData(true, true, true)]
    public async Task AudioPreparation_UsesSavedMuteFlagsAndAbsoluteMusicAndClipEventPlacement(bool muteNative, bool muteMusic, bool muteEvent)
    {
        await using var fixture = new BackendPlaybackFixture(muteNative, muteMusic, muteEvent);
        await fixture.StartAsync();
        var expected = await fixture.ReferenceAudioAsync(!muteNative, !muteMusic, !muteEvent);
        var scene = BackendSceneMapping.Map(fixture.Baseline.Project, false, "clip-main") with { Fingerprint = fixture.Baseline.Fingerprint };
        var audio = await fixture.App.Services.Resolve<IPreviewMedia>()!.PrepareAsync(scene, CancellationToken.None);
        AssertAudio(expected, audio);
        if (muteNative && muteMusic && muteEvent)
        {
            Assert.All(audio.Samples, sample => Assert.Equal(0, sample));
            Assert.All(audio.Peaks, peak => Assert.Equal(0, peak));
        }
        else Assert.Contains(audio.Peaks, peak => peak > .01f);
        fixture.AssertVideoReleased();
        fixture.AssertUnchanged();
    }

    [BackendMediaTheory]
    [InlineData("focus")]
    [InlineData("reload")]
    [InlineData("select")]
    [InlineData("close")]
    public async Task Lifecycle_StopsOwnedPlaybackAndReleasesVideo_WithoutWriting(string operation)
    {
        await using var fixture = new BackendPlaybackFixture();
        await fixture.StartAsync();
        var model = fixture.App.Services.Resolve<SceneViewModel>()!;
        using var host = Host(fixture, "backend-playback-lifecycle");
        Ready(host, model);
        host.Click(Play(host)); Started(host, model, fixture);
        var sound = fixture.Sounds[^1];
        switch (operation)
        {
            case "focus": host.SetActive(false); break;
            case "reload":
                // Reload is a titlebar command; the raster tree contains the client surface only.
                model.Open(); host.Update();
                break;
            case "select":
                var select = Assert.Single(RasterWindow<SceneView>.Walk(host.Window.Navigation).OfType<Button>(),
                    button => button.Value?.EndsWith("Реакция", StringComparison.Ordinal) == true);
                host.Click(select);
                break;
            case "close":
                var close = host.RequestCloseAsync();
                Assert.True(close.IsCompletedSuccessfully);
                Assert.True(close.GetAwaiter().GetResult());
                Assert.True(host.Closed); Assert.True(host.Session.IsDisposed);
                break;
        }
        Assert.False(model.Playing); Assert.True(sound.Disposed);
        if (operation is "reload" or "select")
        {
            Ready(host, model);
            Assert.Equal(operation == "select" ? "clip-extra" : "clip-main", model.Scene!.Clip.Id);
            host.Capture("after-" + operation, new { revision = model.Scene.Revision, clip = model.Scene.Clip.Id });
        }
        if (operation == "close") WaitReleased(fixture);
        else Released(host, fixture);
        Assert.False(model.Modified);
        fixture.AssertUnchanged();
    }

    [BackendMediaFact]
    public async Task RepeatedFocusChanges_StopAndDrainEveryOwnedDecoder_WithoutWrites()
    {
        await using var fixture = new BackendPlaybackFixture();
        await fixture.StartAsync();
        var model = fixture.App.Services.Resolve<SceneViewModel>()!;
        using var host = Host(fixture, "backend-playback-repeated-focus");
        Ready(host, model);
        for (var cycle = 0; cycle < 10; cycle++)
        {
            host.Session.Focus(Play(host));
            Assert.True(host.Input(new(InputKind.KeyDown, Key: UiKey.Space)).Handled);
            Started(host, model, fixture);
            Assert.Equal(cycle, fixture.SoundStarts[^1]);
            fixture.Sounds[^1].PlayedSamples = 2000; // advance exactly one montage frame before each focus loss
            var native = (int)Math.Floor((4 + (cycle + 1) * 1.25) * 30 / 24);
            host.PumpUntil(() => model.FrameIndex == native || model.HasError, TimeSpan.FromSeconds(8));
            Assert.False(model.HasError, model.Status);
            Assert.Equal(13 + cycle, model.TimelineCursor);
            host.SetActive(false);
            Assert.False(model.Playing); Assert.True(fixture.Sounds[^1].Disposed);
            Released(host, fixture);
            Assert.Equal(cycle + 1, fixture.Videos.Count);
            foreach (var video in fixture.Videos)
            {
                Assert.True(video.Completion.IsCompletedSuccessfully);
                Assert.Null(video.Error);
            }
            fixture.AssertUnchanged();
            host.SetActive(true);
        }
        Assert.Equal(Enumerable.Range(0, 10), fixture.SoundStarts);
        Assert.All(fixture.Sounds, sound => { Assert.True(sound.Disposed); Assert.Equal(1, sound.Plays); });
        host.Capture("ten-focus-cycles", new { revision = 12, decoderSessions = fixture.Videos.Count, cursor = model.TimelineCursor });
    }

    [BackendMediaFact]
    public async Task ChangedExactProjectBytes_DuringPlaybackStopWithConflict_AndRequireExplicitReload()
    {
        await using var fixture = new BackendPlaybackFixture();
        await fixture.StartAsync();
        var model = fixture.App.Services.Resolve<SceneViewModel>()!;
        using var host = Host(fixture, "backend-playback-conflict");
        Ready(host, model);
        host.Click(Play(host)); Started(host, model, fixture);
        File.AppendAllText(fixture.Files.Files.ProjectPath, " "); // same revision, changed exact bytes
        fixture.Sounds[^1].PlayedSamples = 96000;
        host.PumpUntil(() => !model.Playing || model.HasError, TimeSpan.FromSeconds(15));
        Assert.True(model.HasError, model.Status); Assert.False(model.Playing);
        Assert.True(model.Stale);
        Assert.Contains("Конфликт ревизии", model.Status);
        Assert.True(fixture.Sounds[0].Disposed); Assert.False(model.CanPlay);
        Assert.Equal(fixture.Baseline.Fingerprint, model.Scene!.Fingerprint);
        Released(host, fixture);
        var before = fixture.Sounds.Count;
        Assert.False(Play(host).IsEnabled);
        host.Click(Play(host));
        Assert.False(model.Playing); Assert.Equal(before, fixture.Sounds.Count);
        host.Capture("conflict-stopped", new { revision = 12, baseline = model.Scene.Fingerprint });
        model.Open(); host.Update();
        Ready(host, model);
        Assert.NotEqual(fixture.Baseline.Fingerprint, model.Scene!.Fingerprint);
        Assert.Equal(12, model.Scene.Revision); Assert.False(model.HasError);
        host.Click(Play(host)); Started(host, model, fixture);
        host.Click(Play(host)); Released(host, fixture);
        fixture.AssertUnchanged(externallyAppended: true);
    }

    [BackendMediaFact]
    public async Task OwnedBackendDisconnect_DuringPlaybackStopsSoundAndReleasesDecoder_WithoutReplayOrWrites()
    {
        await using var fixture = new BackendPlaybackFixture();
        await fixture.StartAsync();
        var model = fixture.App.Services.Resolve<SceneViewModel>()!;
        using var host = Host(fixture, "backend-playback-disconnect");
        Ready(host, model);
        host.Click(Play(host)); Started(host, model, fixture);
        var owner = fixture.App.Services.Resolve<BackendProcessFactory>()!.Owned;
        Assert.True(owner!.IsServing(fixture.Baseline.Identity.ProcessId));
        using var child = Process.GetProcessById(fixture.Baseline.Identity.ProcessId);
        Assert.NotEqual(Environment.ProcessId, child.Id);
        // The live process object and Mithril identity establish ownership before killing this test child.
        Assert.Equal(fixture.BackendStartedAt, child.StartTime);
        child.Kill(entireProcessTree: true);
        child.WaitForExit(8000);
        Assert.True(child.HasExited);
        fixture.Sounds[^1].PlayedSamples = 192000;
        host.PumpUntil(() => !model.Playing || model.HasError, TimeSpan.FromSeconds(15));
        Assert.False(model.Playing); Assert.True(model.HasError, model.Status);
        Assert.True(fixture.Sounds[0].Disposed); Assert.Equal(1, fixture.Sounds[0].Plays);
        Assert.Single(fixture.Sounds);
        Assert.Same(owner, fixture.App.Services.Resolve<BackendProcessFactory>()!.Owned);
        Released(host, fixture);
        host.Capture("disconnected", new { backend = child.Id, revision = 12, soundDisposed = true });
        fixture.AssertUnchanged();
    }

    private static RasterWindow<SceneView> Host(BackendPlaybackFixture fixture, string scenario, float scale = 1) =>
        new(fixture.App.Services.Resolve<IFactory<SceneView>>()!.Create()!, scenario, 1280, 820, scale);

    private static void Ready(RasterWindow<SceneView> host, SceneViewModel model)
    {
        host.PumpUntil(() => model.Loaded && model.Idle && model.PlayLabel != "Звук…" || model.HasError, TimeSpan.FromSeconds(25));
        Assert.True(model.Loaded, model.Status); Assert.False(model.HasError, model.Status); Assert.True(model.CanPlay, model.MediaStatus);
    }

    private static Button Play(RasterWindow<SceneView> host) =>
        Assert.Single(RasterWindow<SceneView>.Walk(host.Window.Preview).OfType<Button>(),
            button => button.AccessibleName?.Contains("Пробел", StringComparison.Ordinal) == true);

    private static void Started(RasterWindow<SceneView> host, SceneViewModel model, BackendPlaybackFixture fixture)
    {
        host.PumpUntil(() => fixture.Sounds.Count > 0 && fixture.Sounds[^1].Plays == 1 || model.HasError, TimeSpan.FromSeconds(15));
        Assert.False(model.HasError, model.Status); Assert.True(model.Playing); Assert.False(fixture.Sounds[^1].Disposed);
    }

    private static void Seek(RasterWindow<SceneView> host, SceneViewModel model, int localFrame)
    {
        var strip = Assert.Single(RasterWindow<SceneView>.Walk(host.Window).OfType<SceneStrip>());
        var x = strip.Bounds.X + (float)(model.TimelineStart + localFrame - model.Scene!.Segment.StartFrame + .1) * strip.Bounds.Width / model.Scene.Segment.DurationFrames;
        var y = strip.Bounds.Y + 8;
        Assert.True(host.Input(new(InputKind.PointerDown, x, y)).PointerCaptured);
        host.Input(new(InputKind.PointerUp, x, y));
    }

    private static void AssertAudio(float[] expected, PreviewAudio actual)
    {
        Assert.Equal(192000, actual.SampleFrames); Assert.Equal(12, actual.TimelineStart); Assert.Equal(96, actual.Frames);
        Assert.Equal(24, actual.Fps); Assert.Equal(expected.Length, actual.Samples.Length);
        var maximum = 0f;
        for (var i = 0; i < expected.Length; i++)
        {
            Assert.True(float.IsFinite(actual.Samples[i]), $"PCM sample{i} must be finite.");
            maximum = Math.Max(maximum, Math.Abs(actual.Samples[i] - expected[i]));
        }
        Assert.True(maximum <= .00002f, $"Real backend PCM differs from independently composed native/music/event oracle: max error={maximum}.");
        Assert.NotEmpty(actual.Peaks);
        Assert.All(actual.Peaks, peak => Assert.True(float.IsFinite(peak) && peak is >= 0 and <= 1));
    }

    private static bool VideoReleased(BackendPlaybackFixture fixture)
    {
        try { fixture.AssertVideoReleased(); return true; }
        catch (IOException) { return false; }
    }
    private static void Released(RasterWindow<SceneView> host, BackendPlaybackFixture fixture)
    {
        var drain = fixture.Playback.DrainAsync();
        try { host.PumpUntil(() => drain.IsCompleted && VideoReleased(fixture), TimeSpan.FromSeconds(8)); }
        catch (TimeoutException error)
        {
            var video = string.Join("; ", fixture.Videos.Select(value => $"completion={value.Completion.Status}, error={value.Error}"));
            throw new TimeoutException($"Playback close did not release the video; client drain status={drain.Status}; videos: {video}.", error);
        }
        Assert.True(drain.IsCompletedSuccessfully, $"Playback close failed: {drain.Exception}");
    }
    private static void WaitReleased(BackendPlaybackFixture fixture)
    {
        var watch = Stopwatch.StartNew();
        while (!VideoReleased(fixture))
        {
            Assert.True(watch.Elapsed < TimeSpan.FromSeconds(8), "Closed window must release its owned video decoder.");
            Thread.Yield();
        }
    }

    private static void AssertPreview(RasterWindow<SceneView> host, byte[] pixels, float scale, string name)
    {
        var image = Assert.Single(RasterWindow<SceneView>.Walk(host.Window.Preview).OfType<Image>());
        Assert.IsType<SkiaImageSource>(image.Source);
        var png = host.Capture(name, new { scale, pixel_sha256 = BackendPlaybackFixture.Hash(pixels) });
        using var decoded = SKBitmap.Decode(png);
        Assert.NotNull(decoded);
        using var actual = decoded.Copy(SKColorType.Bgra8888);
        Assert.NotNull(actual);
        // Match the renderer's RGBA surface and the source's explicit sRGB encoding.
        using var expected = new SKBitmap(new SKImageInfo(actual.Width, actual.Height, SKColorType.Rgba8888, SKAlphaType.Premul));
        using var colorSpace = SKColorSpace.CreateSrgb();
        using var reference = SKImage.FromPixelCopy(new SKImageInfo(480, 270, SKColorType.Bgra8888, SKAlphaType.Premul, colorSpace), pixels, 480 * 4);
        var bounds = image.Bounds.Deflate(image.Padding);
        using (var canvas = new SKCanvas(expected))
        {
            canvas.Clear(SKColors.Transparent); canvas.Scale(scale);
            canvas.DrawImage(reference, new SKRect(bounds.X, bounds.Y, bounds.X + bounds.Width, bounds.Y + bounds.Height), new SKSamplingOptions(SKFilterMode.Linear));
        }
        using var expectedBgra = expected.Copy(SKColorType.Bgra8888);
        Assert.NotNull(expectedBgra);
        var left = (int)MathF.Ceiling(bounds.X * scale) + 2;
        var right = (int)MathF.Floor((bounds.X + bounds.Width) * scale) - 2;
        var top = (int)MathF.Ceiling(bounds.Y * scale) + 2;
        var bottom = (int)MathF.Floor((bounds.Y + bounds.Height) * scale) - 2;
        Assert.True(right - left > 8 && bottom - top > 8);
        for (var y = top; y < bottom; y++)
            Assert.True(actual.GetPixelSpan().Slice(y * actual.RowBytes + left * 4, (right - left) * 4)
                .SequenceEqual(expectedBgra.GetPixelSpan().Slice(y * expectedBgra.RowBytes + left * 4, (right - left) * 4)),
                $"Rendered playback preview differs from independent decode at row{y}, scale{scale}.");
    }
}
