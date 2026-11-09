using TrueMoon.Tales.Studio.Editing;
using TrueMoon.Tales.Studio.Media;
using TrueMoon.Tales.Studio.Models;

namespace TrueMoon.Tales.Studio.Presentation.Scene;

internal sealed partial class SceneViewModel
{
    private CancellationTokenSource? _audioScope;
    private Task<PreviewAudio>? _audioPreparation;
    private PreviewAudio? _audio;
    private IPreviewVideo? _video;
    private IPreviewSound? _sound;
    private PlaybackFrame? _future;
    private bool _playing, _starting, _playCursor;
    private int _playFrame, _playFrom, _audioOffset;
    private long _lastPlayedSamples;
    private TimeSpan _stalledAudio;

    public bool Playing => _playing;
    public string PlayLabel => _playing ? "Пауза" : _audioPreparation is not null ? "Звук…" : "Старт";
    public string MediaStatus { get; private set; } = "";
    public float[]? AudioPeaks => !Modified ? _audio?.Peaks : null;
    public bool CanPlay => _playing || Loaded && _pending is null && _seekTask is null && !Modified && !Trimming && !_stale && _audio is not null;

    private void PreparePlayback(SceneSnapshot scene)
    {
        PausePlayback();
        _playCursor = false;
        _audioScope?.Cancel(); _audioScope?.Dispose(); _audioScope = null;
        if (_audioPreparation is { } old) Observe(old);
        _audioPreparation = null; _audio = null;
        if (_playback is null) return;
        _audioScope = CancellationTokenSource.CreateLinkedTokenSource(_closing.Token);
        MediaStatus = "Подготовка звуковой волны…";
        _audioPreparation = _playback.PrepareAsync(scene, _audioScope.Token);
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
            { MediaStatus = "Звук не подготовлен · " + error.Message; }
            changed = true;
        }
        if (!_playing) return changed;
        try
        {
            if (_video!.Error is { } videoError) throw new InvalidDataException("Видео: " + videoError.Message, videoError);
            if (_sound!.Error is { } audioError) throw new InvalidDataException("Звук: " + audioError.Message, audioError);
            if (_starting)
            {
                if (!_video.TryRead(out var first))
                {
                    if (_video.Complete) throw new InvalidDataException("Не получен первый кадр просмотра");
                    return changed;
                }
                PresentPlayback(first!); _starting = false;
                _sound.Play();
                MediaStatus = _sound.Warning ?? "Воспроизведение";
                Status = MediaStatus;
                return true;
            }
            var played = _sound.PlayedSamples;
            if (played == _lastPlayedSamples) _stalledAudio += elapsed;
            else { _lastPlayedSamples = played; _stalledAudio = TimeSpan.Zero; }
            if (_stalledAudio > TimeSpan.FromSeconds(3) && played < _audio!.SampleFrames - _audioOffset)
                throw new InvalidDataException("Звуковые часы не продвигаются");
            var target = Math.Min(Scene!.Clip.DurationFrames - 1, _playFrom + (int)Math.Floor(played * Scene.Fps / AudioTimeline.SampleRate));
            if (_playFrame != target) { _playFrame = target; changed = true; }
            PlaybackFrame? latest = null;
            while (true)
            {
                if (_future is null && !_video.TryRead(out _future)) break;
                if (_future!.LocalFrame > target) break;
                latest = _future; _future = null;
            }
            if (latest is not null) { PresentPlayback(latest, setCursor: false); changed = true; }
            var remaining = _audio!.SampleFrames - _audioOffset;
            if (_sound.PlayedSamples >= remaining && _future is null && _video.Complete)
            { PausePlayback(); MediaStatus = Status = "Конец клипа"; changed = true; }
        }
        catch (Exception error) when (error is IOException or InvalidDataException or InvalidOperationException or NAudio.MmException)
        { PausePlayback(); Fail("Просмотр остановлен · " + error.Message); MediaStatus = Status; changed = true; }
        return changed;
    }

    private void PresentPlayback(PlaybackFrame frame, bool setCursor = true)
    {
        var index = Array.FindIndex(Scene!.Takes, take => take.Id == Scene.Clip.TakeId);
        Present(frame.Pixels, index, frame.NativeFrame, _assignedMedia!);
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
            _sound = _playback.Sound(_audio!, frame);
            _video = _playback.Video(Scene, _assignedMedia!, frame, _closing.Token);
            _playFrom = _playFrame = frame; _audioOffset = _audio!.Offset(frame);
            _starting = _playing = _playCursor = true;
            _lastPlayedSamples = 0; _stalledAudio = TimeSpan.Zero;
            HasError = false; Status = "Подготовка просмотра…";
            MediaStatus = "Подготовка видео…";
        }
        catch (Exception error) when (error is IOException or InvalidDataException or InvalidOperationException or NAudio.MmException or System.ComponentModel.Win32Exception)
        { PausePlayback(); Fail(error.Message); }
    }

    public void PausePlayback()
    {
        if (_playing) MediaStatus = Status = "Пауза";
        _playing = _starting = false;
        var sound = _sound; var video = _video;
        _sound = null; _video = null; _future = null;
        try { sound?.Dispose(); }
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
