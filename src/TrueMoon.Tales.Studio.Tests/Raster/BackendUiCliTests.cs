using System.Text.Json.Nodes;
using TrueMoon.Argentis;
using TrueMoon.Dependencies;
using TrueMoon.Tales.Studio.Backend;
using TrueMoon.Tales.Studio.Persistence;
using TrueMoon.Tales.Studio.Presentation.Scene;
using TrueMoon.Tales.Studio.Tests.Backend;
using TrueMoon.Tales.Studio.Tests.Support;
using Xunit;

namespace TrueMoon.Tales.Studio.Tests.Raster;

public sealed class BackendUiCliTests
{
    [PythonCliTheory]
    [InlineData(1f)]
    [InlineData(1.5f)]
    [InlineData(2f)]
    public async Task UiAndCli_ShareWriter_ConflictDuringDrag_ReloadAndObserveEachOthersCommits(float scale)
    {
        // This test requires the same explicitly selected stdlib-only Python as the writer interop test.
        Assert.False(string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable("STUDIO_TEST_PYTHON")), "Set STUDIO_TEST_PYTHON for UI/CLI end-to-end checks.");
        await using var fixture = new BackendHttpFixture(); await fixture.StartAsync();
        var identity = (await fixture.Connection.OpenAsync()).Identity;
        var owner = fixture.App.Services.Resolve<BackendProcessFactory>()!.Owned;
        var model = fixture.App.Services.Resolve<SceneViewModel>()!;
        using var host = new RasterWindow<SceneView>(fixture.App.Services.Resolve<IFactory<SceneView>>()!.Create()!, "backend-ui-cli", 1280, 820, scale);
        host.PumpUntil(() => model.Loaded || model.HasError); Assert.True(model.Loaded, model.Status);
        var strip = Assert.Single(RasterWindow<SceneView>.Walk(host.Window).OfType<SceneStrip>());
        Assert.Equal(12, model.Scene!.Revision);
        Assert.Throws<ProjectStoreBusyException>(() => ProjectWriterLease.Acquire(BackendProjectAccess.Read(fixture.Files.Options)));
        host.Capture("loaded", new { revision = 12, backendPid = identity.ProcessId });
        (int Code, string Output, string Error) Cli(params string[] args)
        {
            using var cli = new BackendCliProcess(fixture.Http.Address, fixture.Http.Token, args);
            host.PumpUntil(() => cli.Completed, TimeSpan.FromSeconds(20));
            return cli.Result();
        }
        (float Target, float Y) Drag()
        {
            var clip = strip.ClipBounds;
            var x = clip.X + clip.Width / 2; var y = clip.Y + clip.Height / 2;
            var target = x + strip.Bounds.Width * 2 / model.Scene!.Segment.DurationFrames;
            Assert.True(host.Input(new(InputKind.PointerDown, x, y)).PointerCaptured);
            host.Input(new(InputKind.PointerMove, target, y)); Assert.True(model.Moving);
            return (target, y);
        }
        var first = Cli("backend-project"); Assert.True(first.Code == 0, first.Error);
        var opened = JsonNode.Parse(first.Output)!;
        Assert.Equal(12, opened["project"]!["revision"]!.GetValue<int>());
        Assert.Equal(model.Scene.Fingerprint, opened["fingerprint"]!.GetValue<string>());
        Assert.Single(fixture.Files.Files.HistoryFiles);
        var drag = Drag(); Assert.Equal(14, model.TimelineStart);
        host.Capture("ui-draft", new { start = 14, revision = 12 });
        var external = Cli("backend-move", "--clip", "clip-main", "--start-frame", "16", "--revision", "12", "--sha256", model.Scene.Fingerprint!);
        Assert.True(external.Code == 0, external.Error);
        Assert.Equal(13, JsonNode.Parse(external.Output)!["project"]!["revision"]!.GetValue<int>());
        Assert.True(model.Moving); Assert.Equal(12, model.Scene.Revision); Assert.Equal(14, model.TimelineStart);
        host.Input(new(InputKind.PointerUp, drag.Target, drag.Y)); host.PumpUntil(() => model.Idle && !model.Saving);
        Assert.True(model.Stale); Assert.True(model.Modified); Assert.False(model.CanSave);
        Assert.Contains("Конфликт", model.Status);
        Assert.Equal(2, fixture.Files.Files.HistoryFiles.Length);
        var bytes = File.ReadAllBytes(fixture.Files.Files.ProjectPath);
        Assert.Equal(16, ProjectFileCodec.Read(bytes).Project.Montage.Clips[0].StartFrame);
        var refused = Cli("backend-move", "--clip", "clip-main", "--start-frame", "18", "--revision", "12", "--sha256", opened["fingerprint"]!.GetValue<string>());
        Assert.Equal(3, refused.Code); Assert.Contains("revision_conflict", refused.Error);
        Assert.Equal(bytes, File.ReadAllBytes(fixture.Files.Files.ProjectPath)); Assert.Equal(2, fixture.Files.Files.HistoryFiles.Length);
        host.Capture("conflict", new { uiRevision = 12, diskRevision = 13, draft = 14 });
        model.Cancel(); model.Open(); host.PumpUntil(() => model.Idle);
        Assert.False(model.HasError, model.Status); Assert.False(model.Stale); Assert.Equal(13, model.Scene.Revision); Assert.Equal(16, model.TimelineStart);
        var secondDrag = Drag(); Assert.Equal(18, model.TimelineStart);
        host.Input(new(InputKind.PointerUp, secondDrag.Target, secondDrag.Y)); host.PumpUntil(() => model.Idle && !model.Saving);
        Assert.False(model.HasError, model.Status); Assert.False(model.Modified); Assert.Equal(14, model.Scene.Revision);
        var readAfterUi = Cli("backend-project"); Assert.True(readAfterUi.Code == 0, readAfterUi.Error);
        var current = JsonNode.Parse(readAfterUi.Output)!;
        Assert.Equal(14, current["project"]!["revision"]!.GetValue<int>());
        Assert.Equal(18, current["project"]!["montage"]!["clips"]![0]!["start_frame"]!.GetValue<int>());
        var noop = Cli("backend-move", "--clip", "clip-main", "--start-frame", "18", "--revision", "14", "--sha256", current["fingerprint"]!.GetValue<string>());
        Assert.True(noop.Code == 0, noop.Error); Assert.Equal(14, JsonNode.Parse(noop.Output)!["project"]!["revision"]!.GetValue<int>());
        Assert.Equal(3, fixture.Files.Files.HistoryFiles.Length);
        Assert.Same(owner, fixture.App.Services.Resolve<BackendProcessFactory>()!.Owned);
        Assert.True(owner!.IsServing(identity.ProcessId));
        var png = host.Capture("after-ui-cli", new { revision = 14, start = 18, history = 3 });
        RasterAssert.Dimensions(png, (int)(1280 * scale), (int)(820 * scale));
    }
}

public sealed class PythonCliTheoryAttribute : TheoryAttribute
{
    public PythonCliTheoryAttribute()
    {
        if (Environment.GetEnvironmentVariable("STUDIO_TEST_PYTHON") is null)
            Skip = "Requires STUDIO_TEST_PYTHON for the real stdlib-only CLI process.";
    }
}
