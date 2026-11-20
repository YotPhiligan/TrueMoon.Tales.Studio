using System.Runtime.InteropServices;
using System.Threading.Channels;
using NAudio.Wave;
using TrueMoon.Tales.Studio.Editing;
using TrueMoon.Tales.Studio.Media;
using TrueMoon.Tales.Studio.Models;

namespace TrueMoon.Tales.Studio.Backend;

// The UI receives bounded media packets, never a local path or a decoder process.
internal sealed class BackendPreviewMedia(IBackendPlaybackClient backend,
    Func<PreviewAudio, int, IPreviewSound>? soundFactory = null,
    Func<IWaveProvider, IPreviewSound>? reviewSoundFactory = null) : IPreviewMedia, IDisposable, IAsyncDisposable
{
    private readonly IBackendPlaybackClient _backend = backend;
    private readonly CancellationTokenSource _stopping = new();
    private readonly SemaphoreSlim _lease = new(1, 1);
    private readonly SemaphoreSlim _reviewAudioLease = new(1, 1);
    private readonly object _sync = new();
    private readonly HashSet<Task> _pending = [];
    private bool _disposed;

    public Task<PreviewAudio> PrepareAsync(SceneSnapshot scene, CancellationToken token) => Track(stopping => PrepareCoreAsync(scene, token, stopping));

    private async Task<PreviewAudio> PrepareCoreAsync(SceneSnapshot scene, CancellationToken token, CancellationToken stopping)
    {
        BackendConnection.ValidateScene(scene, "audio", 0);
        using var scope = CancellationTokenSource.CreateLinkedTokenSource(token, stopping);
        scope.CancelAfter(TimeSpan.FromSeconds(55));
        await _lease.WaitAsync(scope.Token).ConfigureAwait(false);
        BackendPlaybackReply? opened = null;
        try
        {
            opened = await _backend.OpenPlaybackAsync(scene, "audio", 0, scope.Token).ConfigureAwait(false);
            BackendConnection.ValidatePlaybackOpen(opened, scene, "audio", 0);
            var samples = new float[checked((int)opened.TotalSamples * AudioTimeline.Channels)];
            long offset = 0;
            while (true)
            {
                var reply = await _backend.ReadPlaybackAsync(opened, scope.Token).ConfigureAwait(false);
                BackendConnection.ValidatePlaybackRead(reply, opened);
                if (reply.Completed)
                {
                    if (offset != opened.TotalSamples) throw new InvalidDataException("Playback PCM ended before the clip boundary.");
                    return new(samples, Waveform.Peaks(samples), scene.Fps, scene.Clip.StartFrame, scene.Clip.DurationFrames);
                }
                if (reply.SampleOffset != offset) throw new InvalidDataException("Playback PCM is missing, duplicated or out of order.");
                CopySamples(reply.Data, samples, checked((int)offset * AudioTimeline.Channels));
                offset += reply.Data.Length / (sizeof(float) * AudioTimeline.Channels);
            }
        }
        finally
        {
            try { if (opened is not null) await _backend.ClosePlaybackAsync(opened.SessionId, CancellationToken.None).ConfigureAwait(false); }
            finally { _lease.Release(); }
        }
    }

    private static void CopySamples(byte[] source, float[] destination, int offset)
    {
        var values = MemoryMarshal.Cast<byte, float>(source);
        for (var i = 0; i < values.Length; i++)
        {
            if (!float.IsFinite(values[i]) || values[i] is < -1 or > 1)
                throw new InvalidDataException("Playback returned invalid PCM samples.");
            destination[offset + i] = values[i];
        }
    }

    public IPreviewVideo Video(SceneSnapshot scene, MediaInfo media, int localFrame, CancellationToken token)
    {
        BackendConnection.ValidateScene(scene, "video", localFrame);
        lock (_sync)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            var video = new VideoSequence(this, scene, localFrame, token, _stopping.Token);
            Remember(video.Completion);
            return video;
        }
    }

    public IPreviewSound Sound(PreviewAudio audio, int localFrame)
    {
        lock (_sync) ObjectDisposedException.ThrowIf(_disposed, this);
        if (localFrame < 0 || localFrame >= audio.Frames) throw new ArgumentOutOfRangeException(nameof(localFrame));
        return (soundFactory ?? PreviewSound.Create)(audio, localFrame);
    }

    internal BufferedReviewAudio BufferReview(SceneSnapshot scene, int frame, CancellationToken token)
    {
        BackendConnection.ValidateScene(scene, BackendPlaybackSession.ReviewAudio, frame);
        lock (_sync)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            var audio = new BufferedReviewAudio(_backend, _reviewAudioLease, scene, frame, token, _stopping.Token);
            Remember(audio.Completion);
            return audio;
        }
    }

    internal IPreviewSound ReviewSound(BufferedReviewAudio audio) =>
        (reviewSoundFactory ?? PreviewSound.CreateBuffered)(audio);

    private Task<T> Track<T>(Func<CancellationToken, Task<T>> create)
    {
        lock (_sync)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            var operation = create(_stopping.Token);
            Remember(operation);
            return operation;
        }
    }

    // Called with _sync held; completed tasks are removed and their faults are always observed.
    private void Remember(Task operation)
    {
        _pending.Add(operation);
        _ = operation.ContinueWith(done =>
        {
            _ = done.Exception;
            lock (_sync) _pending.Remove(done);
        }, CancellationToken.None, TaskContinuationOptions.ExecuteSynchronously, TaskScheduler.Default);
    }

    internal Task DrainAsync()
    {
        lock (_sync) return Task.WhenAll(_pending.ToArray());
    }

    public void Dispose()
    {
        lock (_sync)
        {
            if (_disposed) return;
            _disposed = true;
        }
        _stopping.Cancel();
        _ = DrainAsync().ContinueWith(done => { _ = done.Exception; _stopping.Dispose(); }, TaskScheduler.Default);
    }

    public async ValueTask DisposeAsync()
    {
        Dispose();
        try { await DrainAsync().ConfigureAwait(false); }
        catch (Exception error) when (error is IOException or OperationCanceledException) { }
    }

    private sealed class VideoSequence : IPreviewVideo
    {
        private readonly BackendPreviewMedia _owner;
        private readonly object _cleanupSync = new();
        private BackendPlaybackReply? _opened;
        private Task? _close;
        private readonly CancellationTokenSource _stop;
        private readonly CancellationTokenRegistration _interrupt;
        private readonly Channel<PlaybackFrame> _frames = Channel.CreateBounded<PlaybackFrame>(new BoundedChannelOptions(3)
        { SingleReader = true, SingleWriter = true, FullMode = BoundedChannelFullMode.Wait });
        private Exception? _error;
        private int _disposed;
        public Task Completion { get; }
        public bool Complete => Completion.IsCompleted && !_frames.Reader.TryPeek(out _);
        public Exception? Error => Volatile.Read(ref _error);
        public bool TryRead(out PlaybackFrame? frame) => _frames.Reader.TryRead(out frame);

        internal VideoSequence(BackendPreviewMedia owner, SceneSnapshot scene, int localFrame, CancellationToken token, CancellationToken stopping)
        {
            _owner = owner;
            _stop = CancellationTokenSource.CreateLinkedTokenSource(token, stopping);
            _interrupt = _stop.Token.Register(() => _ = CloseSession());
            Completion = Task.Run(() => ProduceAsync(owner, scene, localFrame, _stop.Token));
        }

        private async Task ProduceAsync(BackendPreviewMedia owner, SceneSnapshot scene, int localFrame, CancellationToken token)
        {
            BackendPlaybackReply? opened = null;
            var admitted = false;
            try
            {
                await owner._lease.WaitAsync(token).ConfigureAwait(false);
                admitted = true;
                opened = await owner._backend.OpenPlaybackAsync(scene, "video", localFrame, token).ConfigureAwait(false);
                lock (_cleanupSync) _opened = opened;
                if (Volatile.Read(ref _disposed) != 0) _ = CloseSession();
                BackendConnection.ValidatePlaybackOpen(opened, scene, "video", localFrame);
                var next = localFrame;
                var lastNative = -1;
                while (true)
                {
                    var reply = await owner._backend.ReadPlaybackAsync(opened, token).ConfigureAwait(false);
                    BackendConnection.ValidatePlaybackRead(reply, opened);
                    if (reply.Completed)
                    {
                        if (next != opened.DurationFrames) throw new InvalidDataException("Playback video ended before the clip boundary.");
                        break;
                    }
                    if (reply.LocalFrame != next || reply.NativeFrame < lastNative)
                        throw new InvalidDataException("Playback frames are missing, duplicated or out of order.");
                    next++;
                    lastNative = reply.NativeFrame;
                    await _frames.Writer.WriteAsync(new(reply.LocalFrame, reply.NativeFrame, reply.Data), token).ConfigureAwait(false);
                }
            }
            catch (OperationCanceledException) when (token.IsCancellationRequested) { }
            catch (Exception error) { Volatile.Write(ref _error, error); }
            finally
            {
                try
                {
                    await CloseSession().ConfigureAwait(false);
                }
                catch (Exception error) { Volatile.Write(ref _error, error); }
                finally
                {
                    if (admitted) owner._lease.Release();
                    _frames.Writer.TryComplete();
                    _interrupt.Dispose();
                    _stop.Dispose();
                }
            }
        }

        public void Dispose()
        {
            if (Interlocked.Exchange(ref _disposed, 1) != 0) return;
            try { _stop.Cancel(); }
            catch (ObjectDisposedException) { }
            // Remote reads can outlive cancellation of their local waiter. Interrupt the
            // known backend resource now; the producer finally joins this same close task.
            _ = CloseSession();
        }

        private Task CloseSession()
        {
            lock (_cleanupSync)
                return _opened is null ? Task.CompletedTask : _close ??= _owner._backend.ClosePlaybackAsync(_opened.SessionId, CancellationToken.None);
        }
    }
}
