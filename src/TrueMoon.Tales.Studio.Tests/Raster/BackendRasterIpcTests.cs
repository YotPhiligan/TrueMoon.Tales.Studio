using System.Diagnostics;
using TrueMoon;
using TrueMoon.Argentis;
using TrueMoon.Dependencies;
using TrueMoon.Tales.Studio.Backend;
using TrueMoon.Tales.Studio.Media;
using TrueMoon.Tales.Studio.Persistence;
using TrueMoon.Tales.Studio.Presentation.Scene;
using TrueMoon.Tales.Studio.Tests.Backend;
using TrueMoon.Tales.Studio.Tests.Support;
using Xunit;

namespace TrueMoon.Tales.Studio.Tests.Raster;

public sealed class BackendRasterIpcTests
{
    [Theory]
    [InlineData(1f, false)]
    [InlineData(1.5f, false)]
    [InlineData(2f, false)]
    [InlineData(1f, true)]
    public async Task Drag_RealMithrilEneritStore_CancelsWithoutWriting_ThenCommitsOnceOrRetainsConflict(float scale, bool conflict)
    {
        using var fixture = new BackendWriteFixture();
        // Explicit fixture migration is separate from opening the editor.
        var store = fixture.Files.Store();
        var schema1 = await store.OpenAsync();
        await store.MigrateAsync(schema1, 11, "test-fixture");
        var initial = File.ReadAllBytes(fixture.Files.ProjectPath);
        var frames = new FakeFrames();
        await using var app = App.Builder(_ => { }).Setup(context => context.UseProjectBackend(fixture.Options, false)
            .UseBackendScene().Services(r => r.Instance<IFrameDecoder>(frames))).Build();
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        Process? child = null;
        RasterWindow<SceneView>? host = null;
        try
        {
            await app.StartAsync(deadline.Token);
            var connection = app.Services.Resolve<BackendConnection>()!;
            var identity = (await connection.OpenAsync(deadline.Token)).Identity;
            Assert.NotEqual(Environment.ProcessId, identity.ProcessId);
            Assert.Equal(Environment.ProcessId, identity.ParentProcessId);
            child = Process.GetProcessById(identity.ProcessId);
            var model = app.Services.Resolve<SceneViewModel>()!;
            var view = app.Services.Resolve<IFactory<SceneView>>()!.Create()!;
            host = new(view, conflict ? "backend-conflict" : "backend-save", 1280, 820, scale);
            host.PumpUntil(() => model.Loaded || model.HasError);
            Assert.True(model.Loaded, model.Status);
            Assert.Equal(12, model.Scene!.Revision);
            Assert.NotNull(model.Scene.Fingerprint);
            Assert.False(model.CanBeginTrim); Assert.True(model.CanSeek); Assert.False(model.CanPlay);
            Assert.Contains("воспроизведение и обрезка пока недоступны", model.MediaStatus);
            frames.Calls.Clear();
            var strip = Assert.Single(RasterWindow<SceneView>.Walk(view).OfType<SceneStrip>());
            Assert.Throws<ProjectStoreBusyException>(() => ProjectWriterLease.Acquire(BackendProjectAccess.Read(fixture.Options)));
            var baseline = host.Capture("loaded", new { revision = 12 });
            RasterAssert.Dimensions(baseline, (int)(1280 * scale), (int)(820 * scale));
            (float Target, float Y) Drag()
            {
                var clip = strip.ClipBounds;
                var x = clip.X + clip.Width / 2; var y = clip.Y + clip.Height / 2;
                var target = x + strip.Bounds.Width * 2 / model.Scene!.Segment.DurationFrames;
                Assert.True(host.Input(new(InputKind.PointerDown, x, y)).PointerCaptured);
                host.Input(new(InputKind.PointerMove, target, y));
                Assert.True(model.Moving); Assert.Equal(14, model.TimelineStart);
                return (target, y);
            }
            var cancel = Drag(); host.Capture("cancel-draft", new { start = 14 });
            Assert.Equal(initial, File.ReadAllBytes(fixture.Files.ProjectPath)); Assert.Single(fixture.Files.HistoryFiles);
            host.Input(new(InputKind.KeyDown, Key: UiKey.Escape));
            Assert.False(host.Input(new(InputKind.PointerUp, cancel.Target, cancel.Y)).PointerCaptured);
            Assert.False(model.Modified); Assert.False(model.Moving);
            Assert.Equal(12, model.Scene.Revision); Assert.Equal(12, model.TimelineStart);
            Assert.Equal(initial, File.ReadAllBytes(fixture.Files.ProjectPath)); Assert.Single(fixture.Files.HistoryFiles);
            host.Capture("cancelled", new { revision = 12 });
            var commit = Drag(); host.Capture("move-draft", new { start = 14 });
            if (conflict) File.AppendAllText(fixture.Files.ProjectPath, " "); // Same revision, changed exact bytes.
            var current = File.ReadAllBytes(fixture.Files.ProjectPath);
            host.Input(new(InputKind.PointerUp, commit.Target, commit.Y));
            host.PumpUntil(() => model.Idle && !model.Saving);
            if (conflict)
            {
                Assert.True(model.Stale); Assert.True(model.Modified); Assert.False(model.CanSave);
                Assert.Contains("Конфликт", model.Status);
                Assert.Equal(current, File.ReadAllBytes(fixture.Files.ProjectPath)); Assert.Single(fixture.Files.HistoryFiles);
                model.Save(); host.Update(); // A stale draft is never replayed.
                Assert.Equal(current, File.ReadAllBytes(fixture.Files.ProjectPath)); Assert.Single(fixture.Files.HistoryFiles);
                host.Capture("conflict", new { revision = 12, draft = 14 });
                Assert.Empty(frames.Calls);
                model.Cancel(); model.Open(); host.PumpUntil(() => model.Idle);
                Assert.Equal(0, Assert.Single(frames.Calls).Frame); // Explicit reload reads its initial frame.
                frames.Calls.Clear();
                Assert.False(model.Stale); Assert.False(model.Modified); Assert.Equal(12, model.TimelineStart);
            }
            else
            {
                Assert.False(model.HasError, model.Status); Assert.False(model.Modified);
                Assert.Equal(13, model.Scene.Revision); Assert.Equal(14, model.TimelineStart);
                var saved = ProjectFileCodec.Read(File.ReadAllBytes(fixture.Files.ProjectPath));
                Assert.Equal(14, saved.Project.Montage.Clips.Single(row => row.Id == "clip-main").StartFrame);
                Assert.Equal(2, fixture.Files.HistoryFiles.Length);
                Assert.Contains(fixture.Files.HistoryFiles, path => File.ReadAllBytes(path).SequenceEqual(initial));
            }
            host.Capture("result", new { revision = model.Scene.Revision, start = model.TimelineStart });
            Assert.Empty(frames.Calls);
            var observed = File.ReadAllBytes(fixture.Files.ProjectPath);
            host.Update(); host.Update();
            Assert.Equal(observed, File.ReadAllBytes(fixture.Files.ProjectPath));
        }
        finally
        {
            try { host?.Dispose(); }
            finally
            {
                await app.StopAsync();
                if (child is not null) { Assert.True(child.HasExited); child.Dispose(); }
            }
        }
        using (ProjectWriterLease.Acquire(BackendProjectAccess.Read(fixture.Options))) { }
    }
}
