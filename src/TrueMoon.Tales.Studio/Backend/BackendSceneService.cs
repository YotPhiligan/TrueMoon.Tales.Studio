using TrueMoon.Argentis;
using TrueMoon.Tales.Studio.Infrastructure;
using TrueMoon.Tales.Studio.Media;
using TrueMoon.Tales.Studio.Models;
using TrueMoon.Tales.Studio.Presentation.Scene;
using TrueMoon.Tales.Studio.Presentation.Shell;

namespace TrueMoon.Tales.Studio.Backend;

// Move-only metadata editing. Media resolver/preview and trim are a separate migration stage.
internal sealed class BackendSceneService(BackendConnection backend) : ISceneService
{
    public bool SupportsPreview => false;
    public async Task<SceneSnapshot> OpenAsync(string? clipId, CancellationToken cancellationToken)
    {
        var snapshot = await backend.OpenAsync(cancellationToken).ConfigureAwait(false);
        if (snapshot.SchemaVersion != 2) throw new IOException("Сначала явно выполните --migrate-project на изолированной копии проекта.");
        snapshot = await backend.AcquireWriterAsync(snapshot, cancellationToken).ConfigureAwait(false);
        return Map(snapshot, clipId);
    }
    public async Task<SceneSnapshot> MoveAsync(SceneSnapshot scene, int requestedStart, CancellationToken cancellationToken) =>
        Map(await backend.MoveAsync(scene.Clip.Id, requestedStart, scene.Revision,
            scene.Fingerprint ?? throw new InvalidDataException("Missing backend baseline."), cancellationToken).ConfigureAwait(false), scene.Clip.Id);

    private static SceneSnapshot Map(OpenedBackendProject snapshot, string? clipId) =>
        BackendSceneMapping.Map(snapshot.Project, snapshot.ScenarioActive, clipId) with { Fingerprint = snapshot.Fingerprint };
    public Task<MediaInfo> MediaInfoAsync(Take take, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return Task.FromResult(take.VideoInfo ?? throw new InvalidDataException("Missing clip extent."));
    }
    public Uri Media(string assetId) => throw new NotSupportedException("Backend media preview is not connected.");
    public Task<bool> HasAudioAsync(string assetId, int stream, CancellationToken token) => Task.FromResult(false);
    public Task<SceneSnapshot> TrimAsync(SceneSnapshot scene, double start, double end, CancellationToken cancellationToken, int? timelineStart = null) =>
        Task.FromException<SceneSnapshot>(new NotSupportedException("Backend trim is not connected."));
}

internal static class BackendScenePresentation
{
    internal static IAppConfigurationContext UseBackendScene(this IAppConfigurationContext context) => context.Services(r => r
        .Singleton<ISceneService, BackendSceneService>()
        .Singleton<IFrameDecoder>(_ => new NoBackendFrames())
        .Singleton<SceneViewModel>(resolver => new(resolver.Resolve<ISceneService>()!, resolver.Resolve<IFrameDecoder>()!, null))
        .Singleton<IViewStyle<SceneView>, SceneViewStyle>()
        .Singleton<IViewStyle<StudioTitleBar>, StudioTitleBarStyle>()
        .Singleton<IFactory<StudioTitleBar, StudioTitleBarArguments>, StudioTitleBarFactory>()
        .Singleton<IFactory<SceneView>, SceneViewFactory>());

    private sealed class NoBackendFrames : IFrameDecoder
    {
        public Task<byte[]> ReadAsync(Uri media, int frame, double fps, CancellationToken cancellationToken) =>
            Task.FromException<byte[]>(new NotSupportedException("Backend media preview is not connected."));
    }
}
