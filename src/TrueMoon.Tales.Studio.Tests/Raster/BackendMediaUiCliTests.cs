using System.Security.Cryptography;
using System.Text;
using System.Text.Json.Nodes;
using SkiaSharp;
using TrueMoon.Argentis;
using TrueMoon.Alloy.Rendering.Skia;
using TrueMoon.Dependencies;
using TrueMoon.Tales.Studio.Backend;
using TrueMoon.Tales.Studio.Backend.Http;
using TrueMoon.Tales.Studio.Persistence;
using TrueMoon.Tales.Studio.Presentation.Scene;
using TrueMoon.Tales.Studio.Tests.Backend;
using TrueMoon.Tales.Studio.Tests.Support;
using Xunit;

namespace TrueMoon.Tales.Studio.Tests.Raster;

public sealed class BackendMediaUiCliTests
{
    [BackendMediaTheory]
    [InlineData(1f)]
    [InlineData(1.5f)]
    [InlineData(2f)]
    public async Task UiAndCli_ResolveAliasesAndReadExactSourceFrames_ThroughOneBackendWithoutWriting(float scale)
    {
        await using var fixture = new BackendMediaFixture(); await fixture.StartAsync();
        // Independent full decodes precede creation of the single-threaded raster owner.
        var first = await fixture.ReferenceFrameAsync(5);
        var middle = await fixture.ReferenceFrameAsync(20);
        var last = await fixture.ReferenceFrameAsync(64);
        Assert.NotEqual(Hash(first), Hash(middle)); Assert.NotEqual(Hash(middle), Hash(last));
        var backend = fixture.Backend;
        var identity = fixture.Baseline.Identity;
        var owner = backend.App.Services.Resolve<BackendProcessFactory>()!.Owned;
        var model = backend.App.Services.Resolve<SceneViewModel>()!;
        using var host = new RasterWindow<SceneView>(backend.App.Services.Resolve<IFactory<SceneView>>()!.Create()!, "backend-media-ui-cli", 1280, 820, scale);
        host.PumpUntil(() => model.Loaded && model.Idle || model.HasError, TimeSpan.FromSeconds(20));
        Assert.True(model.Loaded, model.Status); Assert.False(model.HasError, model.Status);
        Assert.Equal(5, model.FrameIndex); Assert.Equal(first, model.Pixels);
        Assert.Equal(12, model.Scene!.Revision);
        Assert.Equal(4, model.Start); Assert.Equal(52, model.End); Assert.Equal(2d, model.Scene.Clip.Speed!.Value);
        Assert.True(model.CanBeginMove); Assert.True(model.CanBeginTrim); Assert.True(model.CanSetStart); Assert.True(model.CanSetEnd); Assert.False(model.CanSave);
        Assert.Throws<ProjectStoreBusyException>(() => ProjectWriterLease.Acquire(BackendProjectAccess.Read(backend.Files.Options)));
        AssertPreviewFrame(host, first, scale,
            host.Capture("loaded-frame5", new { sourceFrame = 5, projectRevision = 12, backendPid = identity.ProcessId, pixelSha = Hash(first) }));

        var strip = Assert.Single(RasterWindow<SceneView>.Walk(host.Window).OfType<SceneStrip>());
        var x = strip.Bounds.X + (float)(model.TimelineStart + 6 - model.Scene.Segment.StartFrame + .1) * strip.Bounds.Width / model.Scene.Segment.DurationFrames;
        var y = strip.Bounds.Y + 8;
        Assert.True(host.Input(new(InputKind.PointerDown, x, y)).PointerCaptured);
        host.Input(new(InputKind.PointerUp, x, y));
        host.PumpUntil(() => model.Idle && (model.FrameIndex == 20 || model.HasError), TimeSpan.FromSeconds(20));
        Assert.False(model.HasError, model.Status);
        Assert.Equal(20, model.FrameIndex); Assert.Equal(middle, model.Pixels);
        Assert.Equal(12, model.TimelineStart); Assert.False(model.Modified); Assert.False(model.Moving);
        AssertPreviewFrame(host, middle, scale,
            host.Capture("ruler-frame20", new { sourceFrame = 20, timelineFrame = 18, pixelSha = Hash(middle) }));

        (int Code, string Output, string Error) Cli(params string[] arguments)
        {
            using var cli = BackendCliProcess.Native(backend.Http.Address, backend.Http.Token, arguments);
            host.PumpUntil(() => cli.Completed, TimeSpan.FromSeconds(20));
            return cli.Result();
        }
        foreach (var asset in new[] { "video-a", "video-b", "video-named" })
        {
            var info = Cli("backend-media", "--asset", asset, "--revision", "12", "--sha256", fixture.Baseline.Fingerprint);
            Assert.True(info.Code == 0, info.Error);
            var metadata = Media(JsonNode.Parse(info.Output)!);
            Assert.Equal(asset, metadata["asset_id"]!.GetValue<string>());
            Assert.Equal(30, metadata["fps"]!.GetValue<double>());
            Assert.Equal(90, metadata["frame_count"]!.GetValue<int>());
            Assert.Equal(320, metadata["source_width"]!.GetValue<int>());
            Assert.Equal(240, metadata["source_height"]!.GetValue<int>());
        }
        Assert.False(File.Exists(fixture.PngPath));
        var frame = Cli("backend-frame", "--asset", "video-a", "--frame", "20", "--revision", "12", "--sha256", fixture.Baseline.Fingerprint, "--output", fixture.PngPath);
        Assert.True(frame.Code == 0, frame.Error);
        var receipt = JsonNode.Parse(frame.Output)!;
        var exported = Media(receipt);
        Assert.False(exported.AsObject().ContainsKey("pixels"));
        Assert.Equal(fixture.PngPath, receipt["output_path"]!.GetValue<string>());
        Assert.Equal(Hash(File.ReadAllBytes(fixture.PngPath)), receipt["png_sha256"]!.GetValue<string>(), ignoreCase: true);
        Assert.Equal(Hash(middle), exported["pixel_sha256"]!.GetValue<string>().ToUpperInvariant());
        Assert.Equal(20, exported["frame_index"]!.GetValue<int>());
        Assert.True(File.Exists(fixture.PngPath));
        using (var png = SKBitmap.Decode(fixture.PngPath, new SKImageInfo(480, 270, SKColorType.Bgra8888, SKAlphaType.Unpremul)))
        {
            Assert.NotNull(png); Assert.Equal(480, png.Width); Assert.Equal(270, png.Height);
            Assert.Equal(middle, png.GetPixelSpan().ToArray());
        }
        var end = Assert.Single(RasterWindow<SceneView>.Walk(host.Window.Preview).OfType<Button>(), button => button.Value == "К концу");
        host.Click(end);
        host.PumpUntil(() => model.Idle && (model.FrameIndex == 64 || model.HasError), TimeSpan.FromSeconds(20));
        Assert.False(model.HasError, model.Status);
        Assert.Equal(64, model.FrameIndex); Assert.Equal(last, model.Pixels);
        Assert.True(model.CanBeginTrim); Assert.False(model.CanSave);
        AssertPreviewFrame(host, last, scale,
            host.Capture("end-frame64", new { sourceFrame = 64, pixelSha = Hash(last) }));
        Assert.Same(owner, backend.App.Services.Resolve<BackendProcessFactory>()!.Owned);
        Assert.True(owner!.IsServing(identity.ProcessId));
        fixture.AssertUnchanged();
    }

    [BackendMediaFact]
    public async Task DistinctAssetIds_SelectTheirOwnFile_AndCliPngMatchesIndependentSecondAssetDecode()
    {
        await using var fixture = new BackendMediaFixture(); await fixture.StartAsync(distinctSecond: true);
        var expectedA = await fixture.ReferenceFrameAsync(20);
        var expectedB = await fixture.ReferenceFrameAsync(fixture.SecondVideoPath, 20);
        Assert.NotEqual(Hash(expectedA), Hash(expectedB));
        var baseline = fixture.Baseline;
        var connection = fixture.Backend.Connection;
        var first = await connection.FrameAsync("video-a", 20, baseline.Project.Revision, baseline.Fingerprint);
        var second = await connection.FrameAsync("video-b", 20, baseline.Project.Revision, baseline.Fingerprint);
        Assert.Equal(expectedA, first.Pixels); Assert.Equal(expectedB, second.Pixels);
        Assert.Equal("video-a", first.AssetId); Assert.Equal("video-b", second.AssetId);
        Assert.NotEqual(first.PixelSha256, second.PixelSha256);
        Assert.Equal(Hash(expectedB), second.PixelSha256);
        using var cli = BackendCliProcess.Native(fixture.Backend.Http.Address, fixture.Backend.Http.Token,
            "backend-frame", "--asset", "video-b", "--frame", "20", "--revision", "12", "--sha256", baseline.Fingerprint, "--output", fixture.PngPath);
        var result = await cli.WaitAsync();
        Assert.True(result.Code == 0, result.Error);
        var metadata = Media(JsonNode.Parse(result.Output)!);
        Assert.Equal("video-b", metadata["asset_id"]!.GetValue<string>());
        Assert.Equal(Hash(expectedB), metadata["pixel_sha256"]!.GetValue<string>().ToUpperInvariant());
        SkiaNativeLibrary.Initialize();
        using var encoded = new MemoryStream(File.ReadAllBytes(fixture.PngPath), writable: false);
        using var png = SKBitmap.Decode(encoded, new SKImageInfo(480, 270, SKColorType.Bgra8888, SKAlphaType.Unpremul));
        Assert.NotNull(png); Assert.Equal(expectedB, png.GetPixelSpan().ToArray());
        fixture.AssertUnchanged();
    }

    [BackendMediaFact]
    public async Task MissingMediaAfterRoutedClipSelection_ClearsPreviousImageAndRenderedFrame_WithoutWriting()
    {
        await using var fixture = new BackendMediaFixture(); await fixture.StartAsync(distinctSecond: true);
        var first = await fixture.ReferenceFrameAsync(5);
        var model = fixture.Backend.App.Services.Resolve<SceneViewModel>()!;
        using var host = new RasterWindow<SceneView>(fixture.Backend.App.Services.Resolve<IFactory<SceneView>>()!.Create()!,
            "backend-media-missing-transition", 1280, 820);
        host.PumpUntil(() => model.Loaded && model.Idle || model.HasError, TimeSpan.FromSeconds(20));
        Assert.True(model.Loaded, model.Status); Assert.False(model.HasError, model.Status);
        var before = host.Capture("loaded-before-missing", new { clip = "clip-main", sourceFrame = 5 });
        AssertPreviewFrame(host, first, 1, before);
        var image = Assert.Single(RasterWindow<SceneView>.Walk(host.Window.Preview).OfType<Image>());
        var bounds = image.Bounds.Deflate(image.Padding);
        var previousCrop = PreviewCropHash(before, bounds, 1);
        File.Delete(fixture.SecondVideoPath);
        var select = Assert.Single(RasterWindow<SceneView>.Walk(host.Window.Navigation).OfType<Button>(),
            button => button.Value?.EndsWith("Реакция", StringComparison.Ordinal) == true);
        host.Click(select);
        host.PumpUntil(() => model.Loaded && model.Idle && model.Scene?.Clip.Id == "clip-extra" || model.HasError, TimeSpan.FromSeconds(20));
        Assert.False(model.HasError, model.Status); Assert.Equal("clip-extra", model.Scene!.Clip.Id);
        Assert.Null(model.Pixels); Assert.Contains("не найден", model.MediaStatus);
        var after = host.Capture("missing-after-selection", new { clip = "clip-extra", pixels = false });
        Assert.Null(image.Source);
        Assert.Equal(bounds, image.Bounds.Deflate(image.Padding));
        Assert.NotEqual(previousCrop, PreviewCropHash(after, bounds, 1));
        Assert.True(model.CanBeginMove); Assert.False(model.CanBeginTrim); Assert.False(model.Modified);
        fixture.AssertUnchanged();
    }

    [BackendMediaFact]
    public async Task MissingMedia_KeepsSceneLoadedAndRoutedMoveCancellationAvailable_WithoutWriting()
    {
        await using var fixture = new BackendMediaFixture(); await fixture.StartAsync(missing: true);
        var model = fixture.Backend.App.Services.Resolve<SceneViewModel>()!;
        using var host = new RasterWindow<SceneView>(fixture.Backend.App.Services.Resolve<IFactory<SceneView>>()!.Create()!, "backend-media-missing", 1280, 820);
        host.PumpUntil(() => model.Loaded && model.Idle || model.HasError, TimeSpan.FromSeconds(20));
        Assert.True(model.Loaded, model.Status); Assert.False(model.HasError, model.Status);
        Assert.Null(model.Pixels); Assert.Contains("не найден", model.MediaStatus);
        Assert.True(model.CanBeginMove); Assert.False(model.CanBeginTrim); Assert.False(model.CanSave);
        var strip = Assert.Single(RasterWindow<SceneView>.Walk(host.Window).OfType<SceneStrip>());
        var bounds = strip.ClipBounds;
        var x = bounds.X + bounds.Width / 2; var y = bounds.Y + bounds.Height / 2;
        var target = x + strip.Bounds.Width * 2 / model.Scene!.Segment.DurationFrames;
        Assert.True(host.Input(new(InputKind.PointerDown, x, y)).PointerCaptured);
        host.Input(new(InputKind.PointerMove, target, y));
        Assert.True(model.Moving); Assert.True(model.Modified);
        host.Input(new(InputKind.KeyDown, Key: UiKey.Escape));
        host.Input(new(InputKind.PointerUp, target, y));
        host.PumpUntil(() => model.Idle);
        Assert.False(model.Moving); Assert.False(model.Modified); Assert.Equal(12, model.TimelineStart);
        Assert.True(model.CanBeginMove);
        host.Capture("missing-after-escape", new { revision = 12, pixels = false });
        fixture.AssertUnchanged();
    }

    [BackendMediaTheory]
    [InlineData("path", 400, "invalid_request")]
    [InlineData("asset", 404, "media_not_found")]
    [InlineData("revision", 409, "revision_conflict")]
    [InlineData("sha", 409, "revision_conflict")]
    [InlineData("past-end", 422, "invalid_frame")]
    [InlineData("negative", 422, "invalid_frame")]
    [InlineData("metadata-extra-frame", 400, "invalid_request")]
    [InlineData("auth", 403, "forbidden")]
    [InlineData("duplicate", 400, "invalid_request")]
    [InlineData("method", 405, "method_not_allowed")]
    public async Task HttpMedia_RefusesPathsAndInvalidBaselineRequests_WithoutChangingProject(string mode, int status, string code)
    {
        await using var fixture = new BackendMediaFixture(); await fixture.StartAsync();
        var owner = fixture.Backend.App.Services.Resolve<BackendProcessFactory>()!.Owned;
        var payload = new JsonObject { ["asset_id"] = "video-a", ["frame"] = 0, ["expected_revision"] = 12,
            ["fingerprint"] = fixture.Baseline.Fingerprint };
        var route = BackendHttpServer.FrameRoute;
        switch (mode)
        {
            case "path": payload["path"] = fixture.VideoPath; break;
            case "asset": payload["asset_id"] = "not-in-project"; break;
            case "revision": payload["expected_revision"] = 13; break;
            case "sha": payload["fingerprint"] = (char.ToUpperInvariant(fixture.Baseline.Fingerprint[0]) == 'A' ? "B" : "A") + fixture.Baseline.Fingerprint[1..]; break;
            case "past-end": payload["frame"] = 90; break;
            case "negative": payload["frame"] = -1; break;
            case "metadata-extra-frame": route = BackendHttpServer.MediaRoute; break;
        }
        var json = payload.ToJsonString();
        if (mode == "duplicate") json = json.Replace("\"asset_id\":\"video-a\"", "\"asset_id\":\"video-a\",\"asset_id\":\"video-b\"", StringComparison.Ordinal);
        using var client = fixture.Backend.Client(authenticated: mode != "auth");
        using var request = new HttpRequestMessage(mode == "method" ? HttpMethod.Get : HttpMethod.Post, route)
            { Content = new StringContent(json, Encoding.UTF8, "application/json") };
        using var response = await client.SendAsync(request);
        Assert.Equal(status, (int)response.StatusCode);
        var failure = JsonNode.Parse(await response.Content.ReadAsStringAsync())!;
        Assert.Equal(code, failure["error"]!.GetValue<string>());
        Assert.Same(owner, fixture.Backend.App.Services.Resolve<BackendProcessFactory>()!.Owned);
        Assert.True(owner!.IsServing(fixture.Baseline.Identity.ProcessId));
        fixture.AssertUnchanged();
    }

    private static void AssertPreviewFrame(RasterWindow<SceneView> host, byte[] reference, float scale, byte[] png)
    {
        var widget = Assert.Single(RasterWindow<SceneView>.Walk(host.Window.Preview).OfType<Image>());
        var source = Assert.IsType<SkiaImageSource>(widget.Source);
        Assert.Equal((480f, 270f), (source.Size.Width, source.Size.Height));
        Assert.Equal(480 * 270 * 4, reference.Length);
        using var decoded = SKBitmap.Decode(png);
        Assert.NotNull(decoded);
        using var actual = decoded.Copy(SKColorType.Bgra8888);
        Assert.NotNull(actual);
        using var expected = new SKBitmap(new SKImageInfo(actual.Width, actual.Height, SKColorType.Rgba8888, SKAlphaType.Premul));
        using var colorSpace = SKColorSpace.CreateSrgb();
        using var referenceImage = SKImage.FromPixelCopy(new SKImageInfo(480, 270, SKColorType.Bgra8888, SKAlphaType.Premul, colorSpace), reference, 480 * 4);
        Assert.NotNull(referenceImage);
        var bounds = widget.Bounds.Deflate(widget.Padding);
        using (var canvas = new SKCanvas(expected))
        {
            canvas.Clear(SKColors.Transparent);
            canvas.Scale(scale);
            canvas.DrawImage(referenceImage, new SKRect(bounds.X, bounds.Y, bounds.X + bounds.Width, bounds.Y + bounds.Height),
                new SKSamplingOptions(SKFilterMode.Linear));
        }
        using var expectedBgra = expected.Copy(SKColorType.Bgra8888);
        Assert.NotNull(expectedBgra);
        var crop = PreviewPixels(bounds, scale, actual.Width, actual.Height);
        var actualPixels = actual.GetPixelSpan();
        var expectedPixels = expectedBgra.GetPixelSpan();
        for (var y = crop.Top; y < crop.Bottom; y++)
        {
            var actualRow = actualPixels.Slice(y * actual.RowBytes + crop.Left * 4, crop.Width * 4);
            var expectedRow = expectedPixels.Slice(y * expectedBgra.RowBytes + crop.Left * 4, crop.Width * 4);
            Assert.True(actualRow.SequenceEqual(expectedRow), $"Rendered preview differs from independently decoded frame at row {y}, scale {scale}.");
        }
    }

    private static string PreviewCropHash(byte[] png, Rect bounds, float scale)
    {
        using var decoded = SKBitmap.Decode(png);
        Assert.NotNull(decoded);
        using var bitmap = decoded.Copy(SKColorType.Bgra8888);
        Assert.NotNull(bitmap);
        var crop = PreviewPixels(bounds, scale, bitmap.Width, bitmap.Height);
        var pixels = bitmap.GetPixelSpan();
        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        for (var y = crop.Top; y < crop.Bottom; y++) hash.AppendData(pixels.Slice(y * bitmap.RowBytes + crop.Left * 4, crop.Width * 4));
        return Convert.ToHexString(hash.GetHashAndReset());
    }

    private static SKRectI PreviewPixels(Rect bounds, float scale, int width, int height)
    {
        // Exclude fractional boundary pixels and all surrounding text/panel chrome.
        var crop = new SKRectI(Math.Max(0, (int)MathF.Ceiling(bounds.X * scale) + 2),
            Math.Max(0, (int)MathF.Ceiling(bounds.Y * scale) + 2),
            Math.Min(width, (int)MathF.Floor((bounds.X + bounds.Width) * scale) - 2),
            Math.Min(height, (int)MathF.Floor((bounds.Y + bounds.Height) * scale) - 2));
        Assert.True(crop.Width > 8 && crop.Height > 8, "The visible preview interior must have a nontrivial pixel extent.");
        return crop;
    }

    private static string Hash(byte[] pixels) => Convert.ToHexString(SHA256.HashData(pixels));
    private static JsonNode Media(JsonNode envelope) => envelope["media"] ?? envelope;
}
