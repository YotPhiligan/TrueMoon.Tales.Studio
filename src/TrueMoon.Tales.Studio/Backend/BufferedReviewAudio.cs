using System.Runtime.InteropServices;
using System.Threading.Channels;
using NAudio.Wave;
using TrueMoon.Tales.Studio.Editing;
using TrueMoon.Tales.Studio.Models;

namespace TrueMoon.Tales.Studio.Backend;

internal sealed class BufferedReviewAudio : IWaveProvider, IDisposable
{
    internal const int BufferChunks = 8;
    private const int StartSamples = 12288;
    private readonly Channel<byte[]> _packets = Channel.CreateBounded<byte[]>(new BoundedChannelOptions(BufferChunks)
    { SingleReader = true, SingleWriter = true, FullMode = BoundedChannelFullMode.Wait });
    private readonly CancellationTokenSource _stop;
    private readonly CancellationToken _token;
    private byte[]? _packet;
    private int _position, _disposed;
    private bool _ready;
    private Exception? _error;

    public WaveFormat WaveFormat { get; } = WaveFormat.CreateIeeeFloatWaveFormat(AudioTimeline.SampleRate, AudioTimeline.Channels);
    internal Task Completion { get; }
    internal int SampleFrames { get; }
    internal bool Ready => Volatile.Read(ref _ready);
    internal Exception? Error => Volatile.Read(ref _error);

    internal BufferedReviewAudio(IBackendPlaybackClient backend, SemaphoreSlim gate, SceneSnapshot scene, int frame,
        CancellationToken cancellation, CancellationToken stopping)
    {
        var first = BackendPlaybackSession.SampleOffset(scene, frame);
        SampleFrames = BackendPlaybackSession.SampleOffset(scene, scene.Clip.DurationFrames) - first;
        _stop = CancellationTokenSource.CreateLinkedTokenSource(cancellation, stopping);
        _token = _stop.Token;
        Completion = Task.Run(() => ProduceAsync(backend, gate, scene, frame, first, _token));
    }

    private async Task ProduceAsync(IBackendPlaybackClient backend, SemaphoreSlim gate, SceneSnapshot scene, int frame,
        int first, CancellationToken token)
    {
        var admitted = false;
        try
        {
            await gate.WaitAsync(token).ConfigureAwait(false);
            admitted = true;
            var opened = await backend.OpenPlaybackAsync(scene, BackendPlaybackSession.ReviewAudio, frame, token).ConfigureAwait(false);
            var closing = new Lazy<Task>(async () => await backend.ClosePlaybackAsync(opened.SessionId, CancellationToken.None).ConfigureAwait(false));
            using var interrupt = token.Register(() => _ = closing.Value);
            try
            {
                BackendConnection.ValidatePlaybackOpen(opened, scene, BackendPlaybackSession.ReviewAudio, frame);
                var offset = first;
                while (true)
                {
                    var packet = await backend.ReadPlaybackAsync(opened, token).ConfigureAwait(false);
                    BackendConnection.ValidatePlaybackRead(packet, opened);
                    if (packet.SampleOffset != offset) throw new InvalidDataException("Review PCM is out of sequence.");
                    if (packet.Completed) break;
                    ValidateSamples(packet.Data);
                    await _packets.Writer.WriteAsync(packet.Data, token).ConfigureAwait(false);
                    offset += packet.Data.Length / WaveFormat.BlockAlign;
                    if (offset - first >= Math.Min(StartSamples, SampleFrames)) Volatile.Write(ref _ready, true);
                }
            }
            finally { await closing.Value.ConfigureAwait(false); }
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested) { }
        catch (Exception error) { Volatile.Write(ref _error, error); }
        finally
        {
            if (admitted) gate.Release();
            _packets.Writer.TryComplete();
        }
    }

    private static void ValidateSamples(byte[] data)
    {
        foreach (var sample in MemoryMarshal.Cast<byte, float>(data))
            if (!float.IsFinite(sample) || sample is < -1 or > 1) throw new InvalidDataException("Invalid review PCM sample.");
    }

    // WaveOut calls on its playback thread. Wait for real samples on underrun
    // so the audio clock never advances by synthesized silence.
    public int Read(Span<byte> buffer)
    {
        var written = 0;
        var count = buffer.Length - buffer.Length % WaveFormat.BlockAlign;
        try
        {
            while (written < count && !_token.IsCancellationRequested)
            {
                if (_packet is null)
                {
                    if (!_packets.Reader.WaitToReadAsync(_token).AsTask().GetAwaiter().GetResult()) break;
                    if (!_packets.Reader.TryRead(out _packet)) continue;
                    _position = 0;
                }
                var take = Math.Min(_packet.Length - _position, count - written);
                _packet.AsSpan(_position, take).CopyTo(buffer[written..]);
                _position += take;
                written += take;
                if (_position == _packet.Length) _packet = null;
            }
        }
        catch (OperationCanceledException) when (_token.IsCancellationRequested) { }
        return written;
    }

    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0) return;
        _stop.Cancel();
        _ = Completion.ContinueWith(_ => _stop.Dispose(), CancellationToken.None, TaskContinuationOptions.ExecuteSynchronously, TaskScheduler.Default);
    }
}
