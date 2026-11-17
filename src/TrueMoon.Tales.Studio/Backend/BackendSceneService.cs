using TrueMoon.Argentis;
using TrueMoon.Tales.Studio.Infrastructure;
using TrueMoon.Tales.Studio.Media;
using TrueMoon.Tales.Studio.Models;
using TrueMoon.Tales.Studio.Presentation.Scene;
using TrueMoon.Tales.Studio.Presentation.Shell;

namespace TrueMoon.Tales.Studio.Backend;

// Metadata editing and individual source frames; trim/export/playback remain separate stages.
internal sealed class BackendSceneService(BackendConnection backend) : ISceneService
{
    private OpenedBackendProject? _snapshot;
    public bool SupportsPreview => true;
    public bool SupportsTrim => false;
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
    public Task<SceneSnapshot> TrimAsync(SceneSnapshot scene, double start, double end, CancellationToken cancellationToken, int? timelineStart = null) =>
        Task.FromException<SceneSnapshot>(new NotSupportedException("Backend trim is not connected."));
}

internal static class BackendScenePresentation
{
    internal static IAppConfigurationContext UseBackendScene(this IAppConfigurationContext context) => context.Services(r => r
        .Singleton<ISceneService, BackendSceneService>()
        .Singleton<IFrameDecoder, BackendFrameDecoder>()
        .Singleton<SceneViewModel>(resolver => new(resolver.Resolve<ISceneService>()!, resolver.Resolve<IFrameDecoder>()!, null))
        .Singleton<IViewStyle<SceneView>, SceneViewStyle>()
        .Singleton<IViewStyle<StudioTitleBar>, StudioTitleBarStyle>()
        .Singleton<IFactory<StudioTitleBar, StudioTitleBarArguments>, StudioTitleBarFactory>()
        .Singleton<IFactory<SceneView>, SceneViewFactory>());

}
