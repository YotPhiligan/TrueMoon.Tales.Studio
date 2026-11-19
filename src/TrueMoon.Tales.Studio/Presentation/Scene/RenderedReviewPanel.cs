using System.Globalization;
using TrueMoon.Alloy.Rendering.Skia;
using TrueMoon.Alloy.Runtime;
using TrueMoon.Argentis;
using TrueMoon.Tales.Studio.Backend;
using TrueMoon.Tales.Studio.Infrastructure;
using TrueMoon.Tales.Studio.Media;
using TrueMoon.Tales.Studio.Models;
using TrueMoon.Tales.Studio.Presentation.Shell;

namespace TrueMoon.Tales.Studio.Presentation.Scene;

internal sealed class RenderedReviewPanel : Border, IUiFrameParticipant
{
    private readonly SceneViewModel _model;
    private readonly Image _picture = new();
    private readonly Text _status = new(), _frame = new();
    private readonly TextBox _position = new() { Value = "0", AccessibleName = "Кадр предпросмотра", MinWidth = 70, Width = 80 };
    private readonly Button _play, _seek, _previous, _next;
    private SkiaImageSource? _image;
    private byte[]? _shown;

    internal RenderedReviewPanel(SceneViewModel model, bool segment, Action close, BackendReviewRange? range = null)
    {
        _model = model;
        Padding = new Thickness(16, 40, 16, 16);
        var title = Text.Plain(range is { } bounds ? $"Предпросмотр · кадры {bounds.Start}–{bounds.End - 1}" :
            segment ? "Предпросмотр сегмента" : "Предпросмотр проекта");
        title.FontSize = 22;
        _play = Command("Старт", model.TogglePlayback);
        _previous = Command("Пред. кадр", () => model.Step(-1));
        _next = Command("След. кадр", () => model.Step(1));
        _seek = Command("Перейти", () =>
        {
            if (model.Scene is { } scene && int.TryParse(_position.Value.Trim(), NumberStyles.None, CultureInfo.InvariantCulture, out var frame) &&
                frame >= 0 && frame < scene.Clip.DurationFrames)
                model.SeekTimeline((double)scene.Clip.StartFrame + frame);
            else model.Explain("Кадр за пределами предпросмотра.");
        });
        var header = new StudioHeading(title, Command("К монтажу", close));
        var footer = VStack.Create().Configure(stack => stack.Spacing = 6).WithChildren(_status,
            new StudioHeading(HStack.Create().Configure(row => row.Spacing = 8).WithChildren(
                _play, _previous, _next, _position, _seek), _frame));
        SetContent(new PreviewLayout(header, new PreviewSurface(_picture), footer));
        model.Open();
        Refresh();
    }

    public bool NeedsFrame => _model.NeedsFrame;
    internal void Pause() { _model.PausePlayback(); Refresh(); }
    public void AdvanceFrame(TimeSpan elapsed, IInputContext context)
    {
        if (_model.Poll(elapsed)) Refresh();
    }

    private Button Command(string name, Action execute) => Button.Simple(name).OnClick(() =>
    {
        execute();
        if (!IsDisposed) Refresh();
    });

    private void Refresh()
    {
        if (!ReferenceEquals(_shown, _model.Pixels))
        {
            var next = _model.Pixels is { } pixels ? SkiaImageSource.CopyBgraPixels(FrameDecoder.Width, FrameDecoder.Height, pixels) : null;
            _picture.Source = next; _image?.Dispose(); _image = next; _shown = _model.Pixels;
        }
        _status.Value = _model.HasError || _model.MediaStatus.Length == 0 ? _model.Status : _model.MediaStatus;
        _status.HasError = _model.HasError;
        _frame.Value = _model.FrameLabel;
        _play.Value = _model.PlayLabel;
        _play.IsEnabled = _model.CanPlay;
        _seek.IsEnabled = _model.CanSeek && !_model.Playing;
        _previous.IsEnabled = _model.CanPrevious && !_model.Playing;
        _next.IsEnabled = _model.CanNext && !_model.Playing;
    }

    public override void Dispose()
    {
        _model.Dispose();
        _picture.Source = null;
        _image?.Dispose(); _image = null;
        base.Dispose();
    }
}

internal sealed class RenderedReviewSource(BackendRenderedMontage review) : ISceneService
{
    public bool SupportsTrim => false;
    public Task<SceneSnapshot> OpenAsync(string? clipId, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return Task.FromResult(review.Scene());
    }
    public Task<MediaInfo> MediaInfoAsync(Take take, CancellationToken cancellationToken) => Task.FromResult(take.VideoInfo!);
    public Uri Media(string assetId) => BackendFrameDecoder.Reference(assetId, review.Revision, review.Fingerprint);
    public Task<bool> HasAudioAsync(string assetId, int stream, CancellationToken token) => Task.FromResult(true);
    public Task<SceneSnapshot> MoveAsync(SceneSnapshot scene, int requestedStart, CancellationToken cancellationToken) => throw new NotSupportedException();
    public Task<SceneSnapshot> TrimAsync(SceneSnapshot scene, double start, double end, CancellationToken cancellationToken,
        int? timelineStart = null) => throw new NotSupportedException();
}
