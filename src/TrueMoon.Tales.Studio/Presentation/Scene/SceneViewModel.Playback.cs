using TrueMoon.Tales.Studio.Backend;
using TrueMoon.Tales.Studio.Editing;
using TrueMoon.Tales.Studio.Media;
using TrueMoon.Tales.Studio.Models;

namespace TrueMoon.Tales.Studio.Presentation.Scene;

internal sealed partial class SceneViewModel
{
    private CancellationTokenSource? _audioScope;
    private Task<PreviewAudio>? _audioPreparation;
    private PreviewAudio? _audio;
    private BackendPreviewMedia? _reviewMedia;
    private BufferedReviewAudio? _reviewBuffer;
    private IPreviewVideo? _video;
    private IPreviewSound? _sound;
    private PlaybackFrame? _future;
    private bool _playing, _starting, _playCursor;
    private int _playFrame, _playFrom, _playSamples;
    private long _lastPlayedSamples;
    private TimeSpan _stalledAudio;

    public bool Playing => _playing;
    public string PlayLabel => _playing ? "Пауза" : _audioPreparation is not null ? "Звук…" : "Старт";
    public string MediaStatus { get; private set; } = "";
    public float[]? AudioPeaks => !Modified ? _audio?.Peaks : null;
    public bool CanPlay => _playing || Loaded && _pending is null && _seekTask is null && !Modified && !Trimming && !Moving && !_stale &&
        (_audio is not null || _reviewMedia is not null && Pixels is not null);

    private void PreparePlayback(SceneSnapshot scene)
    {
        ClearPreparedPlayback();
        // A missing/unsupported video keeps metadata editing available; it cannot start a clip preview.
        if (_playback is null || Pixels is null) return;
        if (_reviewMedia is not null)
        {
            MediaStatus = "Предпросмотр готов";
            return;
        }
        _audioScope = CancellationTokenSource.CreateLinkedTokenSource(_closing.Token);
        MediaStatus = "Подготовка звуковой волны…";
        _audioPreparation = _playback.PrepareAsync(scene, _audioScope.Token);
    }

    private void ClearPreparedPlayback()
    {
        PausePlayback();
        _playCursor = false;
        _audioScope?.Cancel(); _audioScope?.Dispose(); _audioScope = null;
        if (_audioPreparation is { } old) Observe(old);
        _audioPreparation = null; _audio = null;
    }

    private bool PollPlayback(TimeSpan elapsed)
    {
        var changed = false;
        if (_audioPreparation is { IsCompleted: true } ready)
        {
            _audioPreparation = null;
            try
            {
                _audio = ready.GetAwaiter().GetResult();
                MediaStatus = _audio.Peaks.Any(peak => peak > 0) ? "Звук монтажа · готов" : "Тишина";
            }
            catch (Exception error) when (error is IOException or InvalidDataException or HttpRequestException or System.Text.Json.JsonException or OperationCanceledException or System.ComponentModel.Win32Exception)
            {
                MarkPlaybackConflict(error);
                MediaStatus = "Звук не подготовлен · " + error.Message;
            }
            changed = true;
        }
        if (!_playing) return changed;
        try
        {
            if (_video!.Error is { } videoError) throw new InvalidDataException("Видео: " + videoError.Message, videoError);
            if ((_sound?.Error ?? _reviewBuffer?.Error) is { } audioError)
                throw new InvalidDataException("Звук: " + audioError.Message, audioError);
            if (_starting)
            {
                if (_reviewBuffer is { Ready: false }) return changed;
                if (!_video.TryRead(out var first))
                {
                    if (_video.Complete) throw new InvalidDataException("Не получен первый кадр просмотра");
                    return changed;
                }
                PresentPlayback(first!); _starting = false;
                if (_reviewBuffer is { } buffer) _sound = _reviewMedia!.ReviewSound(buffer);
                _sound!.Play();
                MediaStatus = _sound.Warning ?? "Воспроизведение";
                Status = MediaStatus;
                return true;
            }
            var played = _sound!.PlayedSamples;
            if (played == _lastPlayedSamples) _stalledAudio += elapsed;
            else { _lastPlayedSamples = played; _stalledAudio = TimeSpan.Zero; }
            if (_stalledAudio > TimeSpan.FromSeconds(3) && played < _playSamples)
                throw new InvalidDataException("Звуковые часы не продвигаются");
            var target = Math.Min(Scene!.Clip.DurationFrames - 1, _playFrom + (int)Math.Floor(played * Scene.Fps / AudioTimeline.SampleRate));
            if (_reviewBuffer is not null)
            {
                var clock = BackendPlaybackSession.SampleOffset(Scene, _playFrom) + played;
                if (target + 1 < Scene.Clip.DurationFrames && BackendPlaybackSession.SampleOffset(Scene, target + 1) <= clock) target++;
                if (target > _playFrom && BackendPlaybackSession.SampleOffset(Scene, target) > clock) target--;
            }
            if (_playFrame != target) { _playFrame = target; changed = true; }
            PlaybackFrame? latest = null;
            while (true)
            {
                if (_future is null && !_video.TryRead(out _future)) break;
                if (_future!.LocalFrame > target) break;
                latest = _future; _future = null;
            }
            if (latest is not null) { PresentPlayback(latest, setCursor: false); changed = true; }
            if (played >= _playSamples && _future is null && _video.Complete)
            { PausePlayback(); MediaStatus = Status = "Конец клипа"; changed = true; }
        }
        catch (Exception error) when (error is IOException or InvalidDataException or InvalidOperationException or NAudio.MmException or System.ComponentModel.Win32Exception)
        { PausePlayback(); MarkPlaybackConflict(error); Fail("Просмотр остановлен · " + error.Message); MediaStatus = Status; changed = true; }
        return changed;
    }

    private void PresentPlayback(PlaybackFrame frame, bool setCursor = true)
    {
        var index = Array.FindIndex(Scene!.Takes, take => take.Id == Scene.Clip.TakeId);
        Present(frame.Pixels, index, frame.NativeFrame, _assignedMedia!);
        _viewingSource = false;
        if (setCursor) _playFrame = frame.LocalFrame;
    }

    public void TogglePlayback()
    {
        if (_playing) { PausePlayback(); MediaStatus = "Пауза"; return; }
        if (!CanPlay || _playback is null) return;
        var clip = Scene!.Clip;
        var frame = _playCursor ? _playFrame : Assigned ? (int)Math.Floor((CurrentSourceFrame - _start) / (clip.Speed ?? 1)) : 0;
        frame = frame >= clip.DurationFrames - 1 ? 0 : Math.Clamp(frame, 0, clip.DurationFrames - 1);
        try
        {
            if (_reviewMedia is not null)
            {
                _reviewBuffer = _reviewMedia.BufferReview(Scene, frame, _closing.Token);
                _playSamples = _reviewBuffer.SampleFrames;
            }
            else
            {
                _sound = _playback.Sound(_audio!, frame);
                _playSamples = _audio!.SampleFrames - _audio.Offset(frame);
            }
            _video = _playback.Video(Scene, _assignedMedia!, frame, _closing.Token);
            _playFrom = _playFrame = frame;
            _starting = _playing = _playCursor = true;
            _lastPlayedSamples = 0; _stalledAudio = TimeSpan.Zero;
            HasError = false; Status = "Подготовка просмотра…";
            MediaStatus = _reviewBuffer is null ? "Подготовка видео…" : "Подготовка кадра и звука…";
        }
        catch (Exception error) when (error is IOException or InvalidDataException or InvalidOperationException or NAudio.MmException or System.ComponentModel.Win32Exception)
        { PausePlayback(); MarkPlaybackConflict(error); Fail(error.Message); }
    }

    private void MarkPlaybackConflict(Exception error)
    {
        for (Exception? cause = error; cause is not null; cause = cause.InnerException)
            if (cause is Backend.BackendCommandException { Code: "revision_conflict" })
            { _stale = true; Fail("Проект изменён. Перечитайте сцену перед воспроизведением или редактированием."); return; }
    }

    public void PausePlayback()
    {
        if (_playing) MediaStatus = Status = "Пауза";
        _playing = _starting = false;
        var sound = _sound; var video = _video; var buffer = _reviewBuffer;
        _sound = null; _video = null; _future = null; _reviewBuffer = null;
        try { buffer?.Dispose(); sound?.Dispose(); }
        finally { video?.Dispose(); }
    }

    private static void Observe(Task task) => _ = task.ContinueWith(done => _ = done.Exception,
        CancellationToken.None, TaskContinuationOptions.OnlyOnFaulted, TaskScheduler.Default);

    private void DisposePlayback()
    {
        try { PausePlayback(); }
        finally
        {
            _audioScope?.Cancel(); _audioScope?.Dispose(); _audioScope = null;
            if (_audioPreparation is { } pending) Observe(pending);
            _audioPreparation = null; _audio = null;
        }
    }
}
