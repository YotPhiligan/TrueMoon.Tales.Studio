using System.Text.Json.Nodes;
using TrueMoon.Argentis;
using TrueMoon.Dependencies;
using TrueMoon.Tales.Studio.Backend;
using TrueMoon.Tales.Studio.Editing;
using TrueMoon.Tales.Studio.Persistence;
using TrueMoon.Tales.Studio.Presentation.Scene;
using TrueMoon.Tales.Studio.Tests.Backend;
using TrueMoon.Tales.Studio.Tests.Support;
using Xunit;

namespace TrueMoon.Tales.Studio.Tests.Raster;

public sealed class BackendTrimRasterTests
{
    [BackendMediaTheory]
    [InlineData(1f)]
    [InlineData(1.5f)]
    [InlineData(2f)]
    public async Task RoutedBothEdges_SaveOnceEach_ReopenAndMatchExistingCpuMp4(float scale)
    {
        await using var fixture = new BackendTrimFixture(); await fixture.StartAsync();
        var reference = await fixture.Media.ReferenceFrameAsync(17);
        var model = fixture.Media.App.Services.Resolve<SceneViewModel>()!;
        using var host = Host(fixture, "backend-trim-edges", scale); Ready(host, model);
        var strip = Strip(host);
        var end = Drag(host, model, TrimEdge.End, -24);
        Assert.True(model.Trimming); Assert.Equal(72, model.TimelineDuration); Assert.Equal(94, model.End);
        fixture.AssertBytes(fixture.Initial, 12, 1);
        host.Capture("end-draft", new { model.Start, model.End, model.TimelineStart });
        host.Input(new(InputKind.PointerUp, end.X, end.Y));
        host.Input(new(InputKind.PointerUp, end.X, end.Y));
        Ready(host, model);
        Assert.Equal(13, model.Scene!.Revision); Assert.Equal(72, model.Scene.Clip.DurationFrames);
        var first = File.ReadAllBytes(fixture.ProjectPath); Assert.Equal(2, fixture.Media.Files.Files.HistoryFiles.Length);
        Assert.Contains(fixture.Media.Files.Files.HistoryFiles, path => File.ReadAllBytes(path).SequenceEqual(fixture.Initial));
        var start = Drag(host, model, TrimEdge.Start, 8);
        Assert.Equal(20, model.TimelineStart); Assert.Equal(14, model.Start); Assert.Equal(64, model.TimelineDuration);
        fixture.AssertBytes(first, 13, 2);
        host.Input(new(InputKind.PointerUp, start.X, start.Y)); Ready(host, model);
        Assert.False(model.Stale); Assert.False(model.Modified);
        Assert.Equal(14, model.Scene!.Revision); Assert.Equal(20, model.Scene.Clip.StartFrame);
        Assert.Equal(14, model.Scene.Clip.SourceInFrame); Assert.Equal(94, model.Scene.Clip.SourceEnd);
        Assert.Equal(64, model.Scene.Clip.DurationFrames); Assert.Equal(1.25, model.Scene.Clip.Speed);
        Assert.Equal("take-a", model.Scene.Clip.TakeId); Assert.Equal("source-a", model.Scene.Clip.BlockId);
        var saved = File.ReadAllBytes(fixture.ProjectPath); fixture.AssertBytes(saved, 14, 3);
        Assert.Contains(fixture.Media.Files.Files.HistoryFiles, path => File.ReadAllBytes(path).SequenceEqual(first));
        var old = JsonNode.Parse(fixture.Initial)!; var current = JsonNode.Parse(saved)!;
        Assert.True(JsonNode.DeepEquals(old["project"]!["sources"], current["project"]!["sources"]));
        Assert.True(JsonNode.DeepEquals(old["project"]!["properties"], current["project"]!["properties"]));
        model.Open(); Ready(host, model); Assert.Equal(14, model.Scene!.Revision);
        model.ReturnToClip(); host.PumpUntil(() => model.Idle || model.HasError); Assert.False(model.HasError, model.Status);
        Assert.Equal(17, model.FrameIndex); Assert.Equal(reference, model.Pixels);
        host.Click(Play(host));
        host.PumpUntil(() => fixture.Media.Sounds.LastOrDefault()?.Plays == 1 || model.HasError, TimeSpan.FromSeconds(15));
        Assert.True(model.Playing, model.Status); Assert.Equal(17, model.FrameIndex); Assert.Equal(reference, model.Pixels);
        host.Click(Play(host));
        host.PumpUntil(() => fixture.Media.Playback.DrainAsync().IsCompleted, TimeSpan.FromSeconds(10));
        Assert.False(model.Playing); Assert.True(fixture.Media.Sounds[^1].Disposed);
        model.SeekTimeline(40); host.PumpUntil(() => model.Idle || model.HasError);
        Assert.False(model.HasError, model.Status); Assert.Equal(48, model.FrameIndex); Assert.Equal(40, model.TimelineCursor);
        host.Capture("trimmed-seek", new { revision = 14, source_frame = 48 });
        if (scale == 1)
        {
            var audio = fixture.Media.PreparedSounds[^1];
            Assert.Equal(128000, audio.SampleFrames);
            foreach (var local in new[] { 0, 20, 63 })
            {
                var proof = fixture.CpuAsync(Path.Combine(host.EvidenceDirectory, "cpu-export"), local).GetAwaiter().GetResult();
                var native = (int)Math.Floor((14 + local * 1.25) * 30 / 24);
                var frame = fixture.Media.ReferenceFrameAsync(native).GetAwaiter().GetResult();
                var previous = fixture.Media.ReferenceFrameAsync(native - 1).GetAwaiter().GetResult();
                var next = fixture.Media.ReferenceFrameAsync(native + 1).GetAwaiter().GetResult();
                var observed = proof.Proof["source_samples"]![local]!["actual_seconds"]!.GetValue<double>();
                Assert.InRange(Math.Abs(observed - native / 30d), 0, 1e-8);
                Assert.Equal("completed", proof.Proof["result"]!["state"]!.GetValue<string>());
                Assert.Equal(96, proof.Proof["result"]!["video_info"]!["frames"]!.GetValue<int>());
                Assert.Equal(24, proof.Proof["result"]!["video_info"]!["fps"]!.GetValue<int>());
                Assert.Equal(64, proof.Proof["source_samples"]!.AsArray().Count);
                var streams = proof.Proof["probe"]!["streams"]!.AsArray(); Assert.Equal(2, streams.Count);
                Assert.Equal("h264", streams[0]!["codec_name"]!.GetValue<string>());
                Assert.Equal(480, streams[0]!["width"]!.GetValue<int>()); Assert.Equal(270, streams[0]!["height"]!.GetValue<int>());
                Assert.Equal("24/1", streams[0]!["avg_frame_rate"]!.GetValue<string>());
                Assert.Equal("aac", streams[1]!["codec_name"]!.GetValue<string>());
                Assert.Equal("48000", streams[1]!["sample_rate"]!.GetValue<string>()); Assert.Equal(2, streams[1]!["channels"]!.GetValue<int>());
                var distance = FrameDistance(frame, proof.Frame); Assert.InRange(distance, 0, 8);
                Assert.True(distance + 5 < FrameDistance(previous, proof.Frame), "Encoded frame must distinguish the preceding source frame marker.");
                Assert.True(distance + 5 < FrameDistance(next, proof.Frame), "Encoded frame must distinguish the following source frame marker.");
                Assert.Equal(audio.Samples.Length, proof.Audio.Length);
                var rms = Math.Sqrt(audio.Samples.Zip(proof.Audio, (a, b) => (a - b) * (a - b)).Average());
                Assert.InRange(rms, 0, .004);
            }
            foreach (var outside in new[] { -1, 64 })
            {
                var black = fixture.CpuAsync(Path.Combine(host.EvidenceDirectory, "cpu-export"), outside).GetAwaiter().GetResult().Frame;
                Assert.All(Enumerable.Range(0, black.Length).Where(i => i % 4 != 3), i => Assert.InRange(black[i], 0, 3));
            }
        }
        fixture.AssertBytes(saved, 14, 3);
        Assert.Throws<ProjectStoreBusyException>(() => ProjectWriterLease.Acquire(BackendProjectAccess.Read(fixture.Media.Files.Options)));
    }

    [BackendMediaTheory]
    [InlineData("escape")]
    [InlineData("focus")]
    [InlineData("resize")]
    [InlineData("noop")]
    public async Task RoutedTrim_CancelAndNoopNeverWrite(string cause)
    {
        await using var fixture = new BackendTrimFixture(); await fixture.StartAsync();
        var model = fixture.Media.App.Services.Resolve<SceneViewModel>()!;
        using var host = Host(fixture, "backend-trim-cancel"); Ready(host, model);
        var target = Drag(host, model, TrimEdge.End, cause == "noop" ? 0 : -12);
        if (cause == "escape") host.Input(new(InputKind.KeyDown, Key: UiKey.Escape));
        else if (cause == "focus") host.SetActive(false);
        else if (cause == "resize") host.Resize(1420, 900, 1.5f);
        host.Input(new(InputKind.PointerUp, target.X, target.Y));
        host.PumpUntil(() => model.Idle || model.HasError);
        Assert.False(model.Trimming); Assert.False(model.Modified); Assert.False(model.Stale);
        Assert.Equal(96, model.TimelineDuration); Assert.Equal(4, model.Start); Assert.Equal(124, model.End);
        fixture.AssertBytes(fixture.Initial, 12, 1); host.Capture("after-" + cause, new { revision = model.Scene!.Revision, model.Start, model.End, model.TimelineStart });
    }

    [BackendMediaFact]
    public async Task ScenarioTrim_ShowsFollowingReflowBeforeCommit_AndPreservesPlannedDuration()
    {
        await using var fixture = new BackendTrimFixture(scenario: true); await fixture.StartAsync();
        var model = fixture.Media.App.Services.Resolve<SceneViewModel>()!;
        using var host = Host(fixture, "backend-trim-scenario"); Ready(host, model);
        Assert.True(model.Scene!.ScenarioActive); Assert.True(model.Scene.ScenarioConsistent);
        var target = Drag(host, model, TrimEdge.End, -24);
        Assert.Equal(88, model.PreviewClips.Single(row => row.Id == "clip-follow").StartFrame);
        Assert.Equal(112, model.Scene.SegmentClips!.Single(row => row.Id == "clip-follow").StartFrame);
        fixture.AssertBytes(fixture.Initial, 12, 1); host.Capture("reflow-draft", new { revision = model.Scene.Revision, positions = model.PreviewClips.Select(row => new { row.Id, row.StartFrame, row.DurationFrames }) });
        host.Input(new(InputKind.PointerUp, target.X, target.Y)); Ready(host, model);
        Assert.Equal(13, model.Scene!.Revision); Assert.Equal(72, model.Scene.Clip.DurationFrames);
        Assert.Equal(88, model.Scene.SegmentClips!.Single(row => row.Id == "clip-follow").StartFrame);
        Assert.True(model.Scene.ScenarioConsistent); Assert.True(model.CanBeginMove);
        var current = fixture.Media.Connection.OpenAsync().GetAwaiter().GetResult();
        Assert.Equal(96, current.Project.Segments.Single(row => row.Id == "segment-main").Actions.Single(row => row.Id == "clip-main").PlannedDurationFrames);
        model.Open(); Ready(host, model); Assert.Equal(72, model.Scene!.Clip.DurationFrames);
        Assert.Equal(88, model.Scene.SegmentClips!.Single(row => row.Id == "clip-follow").StartFrame);
    }

    [BackendMediaFact]
    public async Task CliCommitDuringUiTrim_RetainsConflictingDraftAndReloadsExplicitly()
    {
        await using var fixture = new BackendTrimFixture(); await fixture.StartAsync();
        var model = fixture.Media.App.Services.Resolve<SceneViewModel>()!;
        using var host = Host(fixture, "backend-trim-cli-conflict"); Ready(host, model);
        var target = Drag(host, model, TrimEdge.End, -24);
        host.PumpUntil(() => model.Idle || model.HasError); Assert.True(model.Trimming);
        using (var cli = new BackendCliProcess(fixture.Http.Address, fixture.Http.Token, "backend-trim", "--clip", "clip-main",
            "--source-in", "4", "--source-out", "104", "--start", "12", "--revision", "12", "--sha256", model.Scene!.Fingerprint!))
        {
            host.PumpUntil(() => cli.Completed, TimeSpan.FromSeconds(20)); var result = cli.Result();
            Assert.True(result.Code == 0, result.Output + result.Error);
            Assert.Equal(13, JsonNode.Parse(result.Output)!["project"]!["revision"]!.GetValue<int>());
        }
        var external = File.ReadAllBytes(fixture.ProjectPath);
        host.Input(new(InputKind.PointerUp, target.X, target.Y));
        host.PumpUntil(() => model.Idle && !model.Saving);
        Assert.True(model.HasError); Assert.True(model.Stale); Assert.True(model.Modified);
        Assert.Equal(72, model.TimelineDuration); Assert.Equal(12, model.Scene!.Revision);
        model.Save(); fixture.AssertBytes(external, 13, 2);
        host.Capture("conflict-draft", new { revision = model.Scene.Revision, model.Stale, model.Start, model.End }); model.Cancel(); model.Open(); Ready(host, model);
        Assert.False(model.Stale); Assert.False(model.Modified); Assert.Equal(80, model.TimelineDuration);
        fixture.AssertBytes(external, 13, 2);
    }

    [BackendMediaFact]
    public async Task MissingVideo_DisablesTrimAndBoundaryCommands_WhileMetadataMoveRemainsAvailable()
    {
        await using var fixture = new BackendTrimFixture(missing: true); await fixture.StartAsync();
        var model = fixture.Media.App.Services.Resolve<SceneViewModel>()!;
        using var host = Host(fixture, "backend-trim-missing");
        host.PumpUntil(() => model.Loaded || model.HasError, TimeSpan.FromSeconds(15));
        Assert.True(model.Loaded, model.Status); Assert.Null(model.Pixels); Assert.True(model.CanBeginMove);
        Assert.False(model.CanBeginTrim); Assert.False(model.CanSetStart); Assert.False(model.CanSetEnd);
        Assert.False(model.BeginTrim(TrimEdge.End)); fixture.AssertBytes(fixture.Initial, 12, 1);
    }

    private static RasterWindow<SceneView> Host(BackendTrimFixture fixture, string name, float scale = 1) =>
        new(fixture.Media.App.Services.Resolve<IFactory<SceneView>>()!.Create()!, name, 1280, 820, scale);
    private static SceneStrip Strip(RasterWindow<SceneView> host) => Assert.Single(RasterWindow<SceneView>.Walk(host.Window).OfType<SceneStrip>());
    private static Button Play(RasterWindow<SceneView> host) => Assert.Single(RasterWindow<SceneView>.Walk(host.Window.Preview).OfType<Button>(), row =>
        row.AccessibleName?.Contains("Воспроизвести монтажный клип") == true || row.AccessibleName?.Contains("Приостановить просмотр") == true);
    private static void Ready(RasterWindow<SceneView> host, SceneViewModel model)
    {
        host.PumpUntil(() => model.Loaded && model.Idle && model.PlayLabel != "Звук…" || model.HasError, TimeSpan.FromSeconds(20));
        Assert.True(model.Loaded, model.Status); Assert.False(model.HasError, model.Status);
        Assert.True(model.CanBeginTrim, model.MediaStatus);
    }
    private static (float X, float Y) Drag(RasterWindow<SceneView> host, SceneViewModel model, TrimEdge edge, int delta)
    {
        var strip = Strip(host); var bar = strip.ClipBounds;
        var x = edge == TrimEdge.Start ? bar.X + 2 : bar.X + bar.Width - 2; var y = bar.Y + bar.Height / 2;
        var target = x + delta * strip.Bounds.Width / model.Scene!.Segment.DurationFrames;
        Assert.True(host.Input(new(InputKind.PointerDown, x, y)).PointerCaptured);
        host.Input(new(InputKind.PointerMove, target, y)); Assert.True(model.Trimming);
        return (target, y);
    }
    private static double FrameDistance(byte[] expected, byte[] actual)
    {
        Assert.Equal(expected.Length, actual.Length);
        double sum = 0; var count = 0;
        for (var i = 0; i < actual.Length; i++) if (i % 4 != 3) { sum += Math.Abs(actual[i] - expected[i]); count++; }
        return sum / count; // Encoded H264/Pillow resizing differs from CPU preview; PTS and adjacent frame IDs are asserted separately.
    }
}
