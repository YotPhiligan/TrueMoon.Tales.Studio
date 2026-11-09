using System.ComponentModel;
using System.Net;
using System.Text.Json;
using TrueMoon.Tales.Studio.Editing;
using TrueMoon.Tales.Studio.Infrastructure;
using TrueMoon.Tales.Studio.Media;
using TrueMoon.Tales.Studio.Models;

namespace TrueMoon.Tales.Studio.Presentation.Scene;

// App-owned state. Async results are applied only when the window polls on its UI owner thread.
internal sealed partial class SceneViewModel(StudioHttpClient api, IFrameDecoder decoder, IPreviewMedia? playback = null) : IDisposable
{
    private readonly IPreviewMedia? _playback = playback;
    private readonly CancellationTokenSource _closing = new();
    private Task<Action>? _pending;
    private bool _stale;
    private int _takeIndex, _frameIndex;
    private double _start, _end;
    private MediaInfo? _media;
    private MediaInfo? _assignedMedia;
    private Task<byte[]>? _seekTask;
    private SeekRequest? _seekRequested, _seekRunning;
    private TimelineTrim? _trim;
    private TrimEdge _trimEdge;
    private int _timelineStart;
    private double _trimPreviewSource;
    private sealed record SeekRequest(int Take, int Frame, MediaInfo Media);

    public SceneSnapshot? Scene { get; private set; }
    public byte[]? Pixels { get; private set; }
    public string Status { get; private set; } = "Открытие проекта…";
    public bool HasError { get; private set; }
    public bool NeedsFrame => _pending is not null || _seekTask is not null || _audioPreparation is not null || _playing;
    public bool Saving { get; private set; }
    public bool Idle => _pending is null && _seekTask is null && !_playing;
    public bool CanReload => _pending is null && _seekTask is null && !Modified && !Trimming;
    public bool Stale => _stale;
    public bool Loaded => Scene is not null && _media is not null;
    public bool Assigned => Loaded && Scene!.Takes[_takeIndex].Id == Scene.Clip.TakeId;
    public bool Modified => Scene is { } scene && (_start != scene.Clip.SourceInFrame || _end != scene.Clip.SourceEnd || _timelineStart != scene.Clip.StartFrame);
    public bool Trimming => _trim is not null;
    public bool CanSeek => Loaded && _pending is null;
    public bool CanBeginTrim => Editable && !Modified && !Trimming;
    public int TimelineStart => _timelineStart;
    public int TimelineDuration => Scene is { } scene ? TimelineTrim.Duration(_start, _end, scene.Clip.Speed ?? 1) : 0;
    public double TimelineCursor => _playCursor ? _timelineStart + _playFrame : Scene is { } scene ? _timelineStart + (DesiredSourceFrame - _start) / (scene.Clip.Speed ?? 1) : 0;
    private double DesiredSourceFrame => _seekRequested is { } seek ? seek.Frame * Scene!.Fps / seek.Media.Fps : CurrentSourceFrame;
    public int TakeIndex => _takeIndex;
    public int FrameIndex => _frameIndex;
    public double Start => _start;
    public double End => _end;
    public double CurrentSourceFrame => Loaded ? Boundary(_frameIndex) : 0;
    public bool CanPrevious => Loaded && _pending is null && _seekTask is null && _frameIndex > 0;
    public bool CanNext => Loaded && _pending is null && _seekTask is null && _frameIndex + 1 < _media!.FrameCount;
    public bool CanSave => Editable && Modified && !Trimming;
    private bool Editable => Loaded && _pending is null && Assigned && !_stale && !_playing;
    public bool CanSetStart => Editable && Idle && !Trimming && Boundary(_frameIndex) >= Scene!.Clip.SourceInFrame && Boundary(_frameIndex) < _end;
    public bool CanSetEnd => Editable && Idle && !Trimming && Boundary(_frameIndex + 1) > _start && Boundary(_frameIndex + 1) <= Scene!.Clip.SourceEnd;
    public string FrameLabel => Loaded ? $"Кадр {_frameIndex:0000} / {_media!.FrameCount - 1:0000}   ·   {_media.Fps:0.###} кадр/с" : "Кадр —";
    public string TakeLabel => Loaded ? Scene!.Takes[_takeIndex].Name ?? $"Дубль {_takeIndex + 1}" : "Предпросмотр";
    public string EditLabel => _stale ? "Обновите ревизию" : Trimming ? "Обрезка · отпустите для сохранения" : Modified ? "Черновик обрезки" : "Изменений нет";

    public bool Poll(TimeSpan elapsed = default)
    {
        var changed = PollPlayback(elapsed) | PollSeek();
        if (_pending is not { IsCompleted: true } complete) return changed;
        _pending = null;
        Saving = false;
        try { complete.GetAwaiter().GetResult()(); }
        catch (HttpRequestException error) when (error.StatusCode == HttpStatusCode.Conflict)
        {
            Fail("Конфликт ревизий. Откройте актуальную сцену перед сохранением.");
            _stale = true;
        }
        catch (OperationCanceledException) { Fail("Истекло время ожидания. Откройте сцену заново, чтобы сверить состояние."); }
        catch (Exception error) when (error is HttpRequestException or IOException or InvalidDataException or JsonException
            or Win32Exception or InvalidOperationException) { Fail(error.Message); }
        return true;
    }

    private bool PollSeek()
    {
        if (_seekTask is not { IsCompleted: true } complete) return false;
        var request = _seekRunning!;
        _seekTask = null; _seekRunning = null;
        try
        {
            var pixels = complete.GetAwaiter().GetResult();
            if (request == _seekRequested)
            {
                Present(pixels, request.Take, request.Frame, request.Media);
                _seekRequested = null;
                if (!HasError && !Saving && !Trimming && !Modified) Status = "Кадр монтажного клипа";
            }
        }
        catch (Exception error) when (error is OperationCanceledException or IOException or InvalidDataException or Win32Exception or InvalidOperationException)
        {
            if (request == _seekRequested) _seekRequested = null;
            if (!HasError && !Saving) Fail(error.Message);
        }
        StartSeek();
        return true;
    }

    private void RequestClipPreview(double source, bool lastIncluded = false)
    {
        if (!CanSeek || _assignedMedia is not { } media) return;
        var index = Array.FindIndex(Scene!.Takes, take => take.Id == Scene.Clip.TakeId);
        var native = source * media.Fps / Scene.Fps;
        var frame = Math.Clamp(lastIncluded ? (int)Math.Ceiling(native) - 1 : (int)Math.Floor(native), 0, media.FrameCount - 1);
        _seekRequested = new(index, frame, media);
        StartSeek();
    }

    private void StartSeek()
    {
        if (_seekTask is not null || _seekRequested is not { } request) return;
        if (request.Take == _takeIndex && request.Frame == _frameIndex && Pixels is not null)
        { _seekRequested = null; return; }
        _seekRunning = request;
        _seekTask = decoder.ReadAsync(api.Media(Scene!.Takes[request.Take].VideoAssetId), request.Frame, request.Media.Fps, _closing.Token);
    }

    public void Open()
    {
        PausePlayback(); _playCursor = false;
        if (!CanReload) { Status = "Сначала сохраните или отмените черновик"; return; }
        OpenClip(Scene?.Clip.Id);
    }

    public void SelectClip(string id)
    {
        PausePlayback(); _playCursor = false;
        if (!CanReload) { Status = "Сначала сохраните или отмените черновик"; return; }
        OpenClip(id);
    }

    private void OpenClip(string? clipId)
    {
        Begin("Читаю сцену с сервера…", async token =>
        {
            var scene = await api.OpenAsync(clipId, token);
            var index = Array.FindIndex(scene.Takes, take => take.Id == scene.Clip.TakeId);
            var take = scene.Takes[index];
            var media = await api.MediaInfoAsync(take, token);
            var frame = Math.Clamp((int)Math.Floor(scene.Clip.SourceInFrame * media.Fps / scene.Fps), 0, media.FrameCount - 1);
            var pixels = await decoder.ReadAsync(api.Media(take.VideoAssetId), frame, media.Fps, token);
            return () =>
            {
                Present(pixels, index, frame, media);
                _assignedMedia = media;
                SetScene(scene);
                Status = "Проект открыт";
            };
        });
    }

    public void SelectTake(int index)
    {
        PausePlayback(); _playCursor = false;
        if (Loaded && Idle && !Trimming && index >= 0 && index < Scene!.Takes.Length) Preview(index, 0);
    }

    public void Step(int delta)
    {
        PausePlayback(); _playCursor = false;
        if (delta == -1 && CanPrevious || delta == 1 && CanNext) Preview(_takeIndex, _frameIndex + delta);
    }

    public void ReturnToClip() => PreviewClipSource(_start);
    public void GoToEnd() => PreviewClipSource(_end, lastIncluded: true);

    public void SeekTimeline(double frame)
    {
        PausePlayback(); _playCursor = false;
        if (!CanSeek || !double.IsFinite(frame)) return;
        var local = Math.Clamp(Math.Floor(frame) - _timelineStart, 0, TimelineDuration - 1);
        RequestClipPreview(_start + local * (Scene!.Clip.Speed ?? 1));
    }

    public bool BeginTrim(TrimEdge edge)
    {
        PausePlayback(); _playCursor = false;
        if (!CanBeginTrim || _assignedMedia is not { } media) return false;
        var seconds = media.FrameCount / media.Fps;
        if (double.IsFinite(media.Seconds) && media.Seconds > 0) seconds = Math.Min(seconds, media.Seconds);
        var capacity = seconds * Scene!.Fps;
        var trim = new TimelineTrim(Scene, Math.Floor(capacity + 1e-7));
        if (edge == TrimEdge.Start && !trim.CanStart) return false;
        _trim = trim; _trimEdge = edge; _trimPreviewSource = CurrentSourceFrame;
        HasError = false; Status = "Перетаскивание края · Escape отменяет";
        return true;
    }

    public void MoveTrim(int delta)
    {
        if (_trim is null) return;
        var draft = _trim.Move(_trimEdge, delta);
        (_timelineStart, _start, _end) = (draft.TimelineStart, draft.SourceStart, draft.SourceEnd);
        RequestClipPreview(_trimEdge == TrimEdge.Start ? _start : _end, lastIncluded: _trimEdge == TrimEdge.End);
    }

    public void CompleteTrim()
    {
        if (_trim is null) return;
        _trim = null;
        if (Modified) Save();
        else Status = "Границы клипа не изменились";
    }

    public void CancelTrim()
    {
        if (_trim is null) return;
        _trim = null;
        (_timelineStart, _start, _end) = (Scene!.Clip.StartFrame, Scene.Clip.SourceInFrame, Scene.Clip.SourceEnd);
        RequestClipPreview(_trimPreviewSource);
        Status = "Перетаскивание края отменено";
    }

    private void PreviewClipSource(double source, bool lastIncluded = false)
    {
        PausePlayback(); _playCursor = false;
        if (!Loaded || !Idle) return;
        var scene = Scene!;
        var index = Array.FindIndex(scene.Takes, take => take.Id == scene.Clip.TakeId);
        var take = scene.Takes[index];
        Begin("Читаю кадр клипа…", async token =>
        {
            var media = await api.MediaInfoAsync(take, token);
            var native = source * media.Fps / scene.Fps;
            var frame = Math.Clamp(lastIncluded ? (int)Math.Ceiling(native) - 1 : (int)Math.Floor(native), 0, media.FrameCount - 1);
            var pixels = await decoder.ReadAsync(api.Media(take.VideoAssetId), frame, media.Fps, token);
            return () => { Present(pixels, index, frame, media); Status = "Кадр монтажного клипа"; };
        });
    }

    public void Explain(string text) => Status = text;

    private void Preview(int index, int frame)
    {
        var take = Scene!.Takes[index];
        var known = index == _takeIndex ? _media : null;
        Begin("Читаю кадр…", async token =>
        {
            var media = known ?? await api.MediaInfoAsync(take, token);
            var pixels = await decoder.ReadAsync(api.Media(take.VideoAssetId), frame, media.Fps, token);
            return () => { Present(pixels, index, frame, media); Status = "Кадр загружен"; };
        });
    }

    public void SetBoundary(bool start)
    {
        if (start ? !CanSetStart : !CanSetEnd) return;
        if (start) _start = Boundary(_frameIndex); else _end = Boundary(_frameIndex + 1);
        HasError = false;
        Status = "Обрезка в черновике";
    }

    public void Cancel()
    {
        if (!Idle || Scene is null) return;
        (_timelineStart, _start, _end) = (Scene.Clip.StartFrame, Scene.Clip.SourceInFrame, Scene.Clip.SourceEnd);
        Status = "Черновик обрезки отменён";
    }

    private double Boundary(int frame) => frame * Scene!.Fps / _media!.Fps;

    public void Save()
    {
        if (!CanSave) return;
        var scene = Scene!;
        var (start, end, timelineStart) = (_start, _end, _timelineStart);
        // An uncertain write requires a fresh snapshot before another edit.
        _stale = true;
        Saving = true;
        Begin("Сохраняю обрезку…", async token =>
        {
            var saved = await api.TrimAsync(scene, start, end, token, timelineStart != scene.Clip.StartFrame ? timelineStart : null);
            return () => { SetScene(saved); Status = $"Сохранено · ревизия {saved.Revision}"; };
        });
    }

    private void Begin(string status, Func<CancellationToken, Task<Action>> operation)
    {
        if (_pending is not null) return;
        Status = status;
        HasError = false;
        _pending = operation(_closing.Token);
    }

    private void SetScene(SceneSnapshot scene)
    {
        Scene = scene;
        _stale = false;
        (_timelineStart, _start, _end) = (scene.Clip.StartFrame, scene.Clip.SourceInFrame, scene.Clip.SourceEnd);
        PreparePlayback(scene);
    }

    private void Present(byte[] pixels, int index, int frame, MediaInfo media)
    {
        Pixels = pixels;
        (_takeIndex, _frameIndex, _media) = (index, frame, media);
    }

    private void Fail(string message) { Status = message; HasError = true; }

    public void Dispose()
    {
        DisposePlayback();
        _closing.Cancel();
        if (_pending is { } pending)
            _ = pending.ContinueWith(task => _ = task.Exception, TaskContinuationOptions.OnlyOnFaulted);
        if (_seekTask is { } seek)
            _ = seek.ContinueWith(task => _ = task.Exception, TaskContinuationOptions.OnlyOnFaulted);
        _closing.Dispose();
        Pixels = null;
    }
}
