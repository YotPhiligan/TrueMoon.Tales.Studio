using TrueMoon;
using TrueMoon.Argentis;
using TrueMoon.Dependencies;
using TrueMoon.Tales.Studio.Backend;
using TrueMoon.Tales.Studio.Infrastructure;
using TrueMoon.Tales.Studio.Persistence;
using TrueMoon.Tales.Studio.Presentation.Scene;
using TrueMoon.Tales.Studio.Tests.Backend;
using TrueMoon.Tales.Studio.Tests.Support;
using Xunit;

namespace TrueMoon.Tales.Studio.Tests.Raster;

public sealed class BackendLostReplyTests
{
    [Fact]
    public async Task LostReply_AfterRealCommit_KeepsDraftAndDoesNotReplay_UntilExplicitReload()
    {
        using var fixture = new BackendWriteFixture();
        var store = fixture.Files.Store();
        await store.MigrateAsync(await store.OpenAsync(), 11, "fixture");
        DropReply? dropped = null;
        await using var app = App.Builder(_ => { }).Setup(context => context.UseProjectBackend(fixture.Options, false)
            .UseBackendScene().Services(r => r.Singleton<ISceneService>(resolver => new BackendSceneService(new BackendConnection(
                dropped = new(resolver.Resolve<IStudioBackend>()!), fixture.Options, resolver.Resolve<BackendProcessFactory>()!))))).Build();
        RasterWindow<SceneView>? host = null;
        try
        {
            await app.StartAsync();
            var model = app.Services.Resolve<SceneViewModel>()!;
            host = new(app.Services.Resolve<IFactory<SceneView>>()!.Create()!, "backend-lost-reply", 1280, 820);
            host.PumpUntil(() => model.Loaded || model.HasError);
            Assert.True(model.Loaded, model.Status);
            var strip = Assert.Single(RasterWindow<SceneView>.Walk(host.Window).OfType<SceneStrip>());
            var clip = strip.ClipBounds;
            var x = clip.X + clip.Width / 2; var y = clip.Y + clip.Height / 2;
            var target = x + strip.Bounds.Width * 2 / model.Scene!.Segment.DurationFrames;
            host.Input(new(InputKind.PointerDown, x, y)); host.Input(new(InputKind.PointerMove, target, y));
            Assert.True(model.Moving); Assert.Equal(14, model.TimelineStart);
            host.Capture("draft", new { revision = 12 });
            host.Input(new(InputKind.PointerUp, target, y)); host.PumpUntil(() => model.Idle && !model.Saving);
            Assert.True(model.HasError); Assert.True(model.Stale); Assert.True(model.Modified); Assert.False(model.CanSave);
            Assert.Equal(1, dropped!.MoveCalls);
            Assert.Equal(12, model.Scene.Revision); Assert.Equal(14, model.TimelineStart);
            var disk = ProjectFileCodec.Read(File.ReadAllBytes(fixture.Files.ProjectPath));
            Assert.Equal(13, disk.Project.Revision); Assert.Equal(14, disk.Project.Montage.Clips[0].StartFrame);
            Assert.Equal(2, fixture.Files.HistoryFiles.Length);
            model.Save(); host.Update(); host.Update();
            Assert.Equal(1, dropped.MoveCalls); Assert.Equal(2, fixture.Files.HistoryFiles.Length);
            host.Capture("uncertain", new { uiRevision = 12, diskRevision = 13, calls = dropped.MoveCalls });
            model.Cancel(); model.Open(); host.PumpUntil(() => model.Idle);
            Assert.False(model.HasError, model.Status); Assert.False(model.Stale); Assert.False(model.Modified);
            Assert.Equal(13, model.Scene.Revision); Assert.Equal(14, model.TimelineStart); Assert.Equal(1, dropped.MoveCalls);
            host.Capture("reloaded", new { revision = 13 });
        }
        finally { try { host?.Dispose(); } finally { await app.StopAsync(); } }
    }

    // Only fault injection is doubled: the invocation, process, store and UI are real.
    private sealed class DropReply(IStudioBackend inner) : IStudioBackend
    {
        public Task<BackendTextReply> ReadAdaptationTextAsync(string key, string reference, int first, int last, CancellationToken cancellationToken = default) => inner.ReadAdaptationTextAsync(key, reference, first, last, cancellationToken);
        public Task<BackendExportReply> CheckReadinessAsync(string key, long revision, string hash, CancellationToken cancellationToken = default) => inner.CheckReadinessAsync(key, revision, hash, cancellationToken);
        public Task<BackendExportReply> WorkingReviewAsync(string key, string operation, string id, long revision, string hash, int startFrame, int endFrame, CancellationToken cancellationToken = default) => inner.WorkingReviewAsync(key, operation, id, revision, hash, startFrame, endFrame, cancellationToken);
        public Task<BackendProjectReply> EditScenarioAsync(string key, string command, long revision, string hash, CancellationToken cancellationToken = default) =>
            inner.EditScenarioAsync(key, command, revision, hash, cancellationToken);
        public Task<BackendExportReply> CheckExportAsync(string key, string profile, long revision, string hash, CancellationToken cancellationToken = default) => inner.CheckExportAsync(key, profile, revision, hash, cancellationToken);
        public Task<BackendExportReply> FindExportAsync(string key, string clientKey, CancellationToken cancellationToken = default) => inner.FindExportAsync(key, clientKey, cancellationToken);
        public Task<BackendExportReply> StartExportAsync(string key, string clientKey, string profile, long revision, string hash, CancellationToken cancellationToken = default) => inner.StartExportAsync(key, clientKey, profile, revision, hash, cancellationToken);
        public Task<BackendExportReply> ExportStatusAsync(string key, string id, CancellationToken cancellationToken = default) => inner.ExportStatusAsync(key, id, cancellationToken);
        public Task<BackendExportReply> CancelExportAsync(string key, string id, long revision, CancellationToken cancellationToken = default) => inner.CancelExportAsync(key, id, revision, cancellationToken);
        public Task<BackendExportReply> RecoverExportAsync(string key, string id, CancellationToken cancellationToken = default) => inner.RecoverExportAsync(key, id, cancellationToken);
        public Task<BackendExportReply> ExportChunkAsync(string key, string id, string sha, long artifactOffset, CancellationToken cancellationToken = default) => inner.ExportChunkAsync(key, id, sha, artifactOffset, cancellationToken);
        public Task<BackendProjectReply> TrimClipAsync(string key, string id, double sourceInFrame, double sourceOutFrame, int start, long revision, string fingerprint, CancellationToken cancellationToken = default) => inner.TrimClipAsync(key, id, sourceInFrame, sourceOutFrame, start, revision, fingerprint, cancellationToken);
        public Task<BackendPlaybackReply> OpenPlaybackAsync(string key, string clipId, string kind, int localFrame, long revision, string fingerprint, CancellationToken cancellationToken = default) => inner.OpenPlaybackAsync(key, clipId, kind, localFrame, revision, fingerprint, cancellationToken);
        public Task<BackendPlaybackReply> ReadPlaybackAsync(string key, string id, CancellationToken cancellationToken = default) => inner.ReadPlaybackAsync(key, id, cancellationToken);
        public Task<BackendPlaybackReply> ClosePlaybackAsync(string key, string id, CancellationToken cancellationToken = default) => inner.ClosePlaybackAsync(key, id, cancellationToken);
        public Task<BackendMediaReply> MediaInfoAsync(string key, string id, long revision, string hash, CancellationToken cancellationToken = default) => inner.MediaInfoAsync(key, id, revision, hash, cancellationToken);
        public Task<BackendMediaReply> FrameAsync(string key, string id, int frame, long revision, string hash, CancellationToken cancellationToken = default) => inner.FrameAsync(key, id, frame, revision, hash, cancellationToken);
        internal int MoveCalls;
        public Task<BackendIdentity> HelloAsync(string key, CancellationToken cancellationToken = default) => inner.HelloAsync(key, cancellationToken);
        public Task<BackendProjectReply> OpenProjectAsync(string key, CancellationToken cancellationToken = default) => inner.OpenProjectAsync(key, cancellationToken);
        public Task<BackendProjectReply> AcquireWriterAsync(string key, long revision, string fingerprint, CancellationToken cancellationToken = default) => inner.AcquireWriterAsync(key, revision, fingerprint, cancellationToken);
        public Task<BackendProjectReply> MigrateProjectAsync(string key, long revision, string fingerprint, CancellationToken cancellationToken = default) => inner.MigrateProjectAsync(key, revision, fingerprint, cancellationToken);
        public Task<BackendProjectReply> RecoverProjectAsync(string key, CancellationToken cancellationToken = default) => inner.RecoverProjectAsync(key, cancellationToken);
        public async Task<BackendProjectReply> MoveClipAsync(string key, string id, int start, long revision, string fingerprint, CancellationToken cancellationToken = default)
        {
            MoveCalls++;
            var reply = await inner.MoveClipAsync(key, id, start, revision, fingerprint, cancellationToken);
            Assert.Empty(reply.ErrorCode);
            throw new TrueMoon.Enerit.InvocationException("Simulated lost reply after real commit.");
        }
    }
}
