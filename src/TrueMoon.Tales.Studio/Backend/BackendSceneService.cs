using TrueMoon.Argentis;
using TrueMoon.Tales.Studio.Infrastructure;
using TrueMoon.Tales.Studio.Media;
using TrueMoon.Tales.Studio.Models;
using TrueMoon.Tales.Studio.Presentation.Scene;
using TrueMoon.Tales.Studio.Presentation.Scenario;
using TrueMoon.Tales.Studio.Presentation.Shell;
using System.Text.Json;

namespace TrueMoon.Tales.Studio.Backend;

// Metadata editing, source frames and selected-clip playback share the owned backend.
internal sealed class BackendSceneService(BackendConnection backend, string? initialFingerprint = null) : ISceneService
{
    private OpenedBackendProject? _snapshot;
    public bool SupportsPreview => true;
    public bool SupportsTrim => true;
    public bool UsesTrimLayout => true;
    public bool PreviewOptional => true;
    public bool SupportsMontageEdits => true;
    public async Task<SceneSnapshot> OpenAsync(string? clipId, CancellationToken cancellationToken) =>
        Map(await OpenOwnedAsync(cancellationToken).ConfigureAwait(false), clipId);

    public async Task<EditorOpening> OpenEditorAsync(string? clipId, CancellationToken cancellationToken) =>
        Opening(await OpenOwnedAsync(cancellationToken).ConfigureAwait(false), clipId);

    public async Task<EditorOpening> EditMontageAsync(ScenarioCommand command, string? clipId, CancellationToken token)
    {
        var before = _snapshot ?? throw new InvalidOperationException("Open the project first.");
        var after = await backend.EditScenarioAsync(command, before, token).ConfigureAwait(false);
        var selected = after.Project.Montage.Clips.Any(row => row.Id == clipId && row.TakeId is not null &&
            after.Project.Sources.Any(source => source.Id == row.SourceId &&
                source.Takes.Any(take => take.Id == row.TakeId && take.VideoAssetId is not null))) ? clipId : null;
        return Opening(after, selected);
    }

    private EditorOpening Opening(OpenedBackendProject snapshot, string? clipId)
    {
        var project = snapshot.Project;
        if (BackendSceneMapping.HasVideoClip(project)) return new(Map(snapshot, clipId));
        _snapshot = snapshot;
        string? dimensions = project.Properties.TryGetValue("output_size", out var size) &&
            size.ValueKind == JsonValueKind.Array && size.GetArrayLength() == 2 &&
            size[0].ValueKind == JsonValueKind.Number && size[1].ValueKind == JsonValueKind.Number &&
            size[0].TryGetInt32(out var width) && size[1].TryGetInt32(out var height) && width > 0 && height > 0
            ? $"{width} × {height}" : null;
        return new(null, new(project.Name, project.Revision, project.Fps, dimensions, snapshot.CanUndoMontage, snapshot.CanRedoMontage));
    }

    private async Task<OpenedBackendProject> OpenOwnedAsync(CancellationToken cancellationToken)
    {
        var snapshot = await backend.OpenAsync(cancellationToken).ConfigureAwait(false);
        if (snapshot.SchemaVersion != 2) throw new IOException("Сначала явно выполните --migrate-project на изолированной копии проекта.");
        if (_snapshot is null && initialFingerprint is not null &&
            !string.Equals(snapshot.Fingerprint, initialFingerprint, StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException("Проект изменился после выбора. Обновите список проектов.");
        return await backend.AcquireWriterAsync(snapshot, cancellationToken).ConfigureAwait(false);
    }
    public async Task<SceneSnapshot> MoveAsync(SceneSnapshot scene, int requestedStart, CancellationToken cancellationToken) =>
        Map(await backend.MoveAsync(scene.Clip.Id, requestedStart, scene.Revision,
            scene.Fingerprint ?? throw new InvalidDataException("Missing backend baseline."), cancellationToken).ConfigureAwait(false), scene.Clip.Id);

    private SceneSnapshot Map(OpenedBackendProject snapshot, string? clipId)
    {
        _snapshot = snapshot;
        return BackendSceneMapping.Map(snapshot.Project, snapshot.ScenarioActive, clipId) with
        {
            Fingerprint = snapshot.Fingerprint, CanUndoMontage = snapshot.CanUndoMontage, CanRedoMontage = snapshot.CanRedoMontage
        };
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
    internal static IAppConfigurationContext UseBackendScene(this IAppConfigurationContext context, Action<string?>? initialOpen = null) =>
        context.Services(r => r
        .Singleton<ISceneService>(resolver => new BackendSceneService(resolver.Resolve<BackendConnection>()!,
            resolver.Resolve<BackendOptions>()!.InitialFingerprint))
        .Singleton<IFrameDecoder, BackendFrameDecoder>()
        .Singleton<IPreviewMedia>(resolver => new BackendPreviewMedia(resolver.Resolve<BackendConnection>()!))
        .Singleton<SceneViewModel>(resolver => new(resolver.Resolve<ISceneService>()!, resolver.Resolve<IFrameDecoder>()!, resolver.Resolve<IPreviewMedia>()))
        .Singleton<IViewStyle<SceneView>, SceneViewStyle>()
        .Singleton<IViewStyle<StudioTitleBar>, StudioTitleBarStyle>()
        .Singleton<IFactory<StudioTitleBar, StudioTitleBarArguments>, StudioTitleBarFactory>()
        .Singleton<IFactory<SceneView>>(resolver => new SceneViewFactory(resolver.Resolve<SceneViewModel>()!,
            resolver.Resolve<IViewStyle<SceneView>>()!, resolver.Resolve<IFactory<StudioTitleBar, StudioTitleBarArguments>>()!,
            () => new SceneExport(resolver.Resolve<BackendConnection>()!, resolver.Resolve<BackendOptions>()!), initialOpen,
            () => new ScenarioWorkspace(resolver.Resolve<BackendConnection>()!))));

}
