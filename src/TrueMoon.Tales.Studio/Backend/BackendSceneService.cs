using TrueMoon.Argentis;
using TrueMoon.Tales.Studio.Infrastructure;
using TrueMoon.Tales.Studio.Media;
using TrueMoon.Tales.Studio.Models;
using TrueMoon.Tales.Studio.Presentation.Scene;
using TrueMoon.Tales.Studio.Presentation.Shell;

namespace TrueMoon.Tales.Studio.Backend;

// Metadata editing, source frames and selected-clip playback share the owned backend.
internal sealed class BackendSceneService(BackendConnection backend) : ISceneService
{
    private OpenedBackendProject? _snapshot;
    public bool SupportsPreview => true;
    public bool SupportsTrim => true;
    public bool UsesTrimLayout => true;
    public bool PreviewOptional => true;
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

    private SceneSnapshot Map(OpenedBackendProject snapshot, string? clipId)
    {
        _snapshot = snapshot;
        return BackendSceneMapping.Map(snapshot.Project, snapshot.ScenarioActive, clipId) with { Fingerprint = snapshot.Fingerprint };
    }
    public async Task<MediaInfo> MediaInfoAsync(Take take, CancellationToken cancellationToken)
    {
        var snapshot = _snapshot ?? throw new InvalidOperationException("Open the project first.");
        try
        {
            var result = await backend.MediaInfoAsync(take.VideoAssetId, snapshot.Project.Revision, snapshot.Fingerprint, cancellationToken).ConfigureAwait(false);
            return new(result.Fps, result.FrameCount / result.Fps, result.FrameCount);
        }
        catch (BackendCommandException e) when (e.Code is "media_not_found" or "access_denied" or "invalid_media" or "decoder_unavailable")
        { return take.VideoInfo ?? throw new InvalidDataException("Missing clip extent."); }
    }
    public Uri Media(string assetId) => _snapshot is { } snapshot
        ? BackendFrameDecoder.Reference(assetId, snapshot.Project.Revision, snapshot.Fingerprint)
        : throw new InvalidOperationException("Open the project first.");
    public Task<bool> HasAudioAsync(string assetId, int stream, CancellationToken token) => Task.FromResult(false);
    public async Task<SceneSnapshot> TrimAsync(SceneSnapshot scene, double start, double end, CancellationToken cancellationToken, int? timelineStart = null) =>
        Map(await backend.TrimAsync(scene.Clip.Id, start, end, timelineStart ?? scene.Clip.StartFrame, scene.Revision,
            scene.Fingerprint ?? throw new InvalidDataException("Missing backend baseline."), cancellationToken).ConfigureAwait(false), scene.Clip.Id);
}

internal static class BackendScenePresentation
{
    internal static IAppConfigurationContext UseBackendScene(this IAppConfigurationContext context) => context.Services(r => r
        .Singleton<ISceneService, BackendSceneService>()
        .Singleton<IFrameDecoder, BackendFrameDecoder>()
        .Singleton<IPreviewMedia>(resolver => new BackendPreviewMedia(resolver.Resolve<BackendConnection>()!))
        .Singleton<SceneViewModel>(resolver => new(resolver.Resolve<ISceneService>()!, resolver.Resolve<IFrameDecoder>()!, resolver.Resolve<IPreviewMedia>()))
        .Singleton<IViewStyle<SceneView>, SceneViewStyle>()
        .Singleton<IViewStyle<StudioTitleBar>, StudioTitleBarStyle>()
        .Singleton<IFactory<StudioTitleBar, StudioTitleBarArguments>, StudioTitleBarFactory>()
        .Singleton<IFactory<SceneView>>(resolver => new SceneViewFactory(resolver.Resolve<SceneViewModel>()!,
            resolver.Resolve<IViewStyle<SceneView>>()!, resolver.Resolve<IFactory<StudioTitleBar, StudioTitleBarArguments>>()!,
            () => new SceneExport(resolver.Resolve<BackendConnection>()!, resolver.Resolve<BackendOptions>()!))));

}
