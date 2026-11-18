using System.Globalization;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Threading.Channels;
using TrueMoon.Tales.Studio.Editing;
using TrueMoon.Tales.Studio.Models;

namespace TrueMoon.Tales.Studio.Backend;

internal sealed class BackendPlaybackSession : IAsyncDisposable
{
    internal const int AudioChunkBytes = 32768;
    private readonly Channel<BackendPlaybackReply> _queue = Channel.CreateBounded<BackendPlaybackReply>(new BoundedChannelOptions(3)
    { SingleReader = true, SingleWriter = true, FullMode = BoundedChannelFullMode.Wait });
    private readonly CancellationTokenSource _stop;
    private readonly CancellationTokenSource _idle = new(TimeSpan.FromSeconds(20));
    private readonly List<FileStream> _leases;
    private readonly Task _producer;
    private readonly BackendPlaybackReply _metadata;
    private readonly int _nativeOffset;
    private readonly Action _validatePaths;
    private int _disposed;
    internal string Id => _metadata.SessionId;
    internal long Revision => _metadata.Revision;
    internal string Fingerprint => _metadata.Fingerprint;
    internal bool Expired => _stop.IsCancellationRequested;
    internal Task Completion => _producer;
    internal void Cancel() { try { _stop.Cancel(); } catch (ObjectDisposedException) { } }
    internal void ValidatePaths() => _validatePaths();

    internal BackendPlaybackSession(BackendMediaDecoder decoder, SceneSnapshot scene, BackendVideoInfo video,
        string videoPath, IReadOnlyList<(AudioSlice Slice, string Path)> audio, List<FileStream> leases,
        string kind, int localFrame, string fingerprint, CancellationToken stopping, Action validatePaths, int nativeOffset = 0)
    {
        _leases = leases;
        _nativeOffset = nativeOffset;
        _validatePaths = validatePaths;
        _stop = CancellationTokenSource.CreateLinkedTokenSource(stopping, _idle.Token);
        _stop.CancelAfter(TimeSpan.FromMinutes(12));
        _metadata = new()
        {
            SessionId = Guid.NewGuid().ToString("N"), Kind = kind, ClipId = scene.Clip.Id,
            Revision = scene.Revision, Fingerprint = fingerprint, Fps = scene.Fps, SourceFps = video.Fps,
            DurationFrames = scene.Clip.DurationFrames, StartFrame = localFrame,
            TotalSamples = checked((int)(AudioTimeline.Sample((double)scene.Clip.StartFrame + scene.Clip.DurationFrames, scene.Fps) - AudioTimeline.Sample(scene.Clip.StartFrame, scene.Fps)))
        };
        _producer = Task.Run(() => ProduceAsync(decoder, scene, video, videoPath, audio, _stop.Token));
    }

    internal BackendPlaybackReply Metadata(bool completed = false) => new()
    {
        SessionId = Id, Kind = _metadata.Kind, ClipId = _metadata.ClipId,
        Revision = Revision, Fingerprint = Fingerprint, Fps = _metadata.Fps, SourceFps = _metadata.SourceFps,
        DurationFrames = _metadata.DurationFrames, StartFrame = _metadata.StartFrame, TotalSamples = _metadata.TotalSamples,
        Completed = completed, SampleOffset = completed && _metadata.Kind == "audio" ? _metadata.TotalSamples : 0
    };

    internal async Task<BackendPlaybackReply> ReadAsync(CancellationToken token)
    {
        // A caller actively waiting for a bounded chunk owns the idle lease.
        _idle.CancelAfter(Timeout.InfiniteTimeSpan);
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(token, _stop.Token);
        try
        {
            while (await _queue.Reader.WaitToReadAsync(linked.Token).ConfigureAwait(false))
                if (_queue.Reader.TryRead(out var value)) return value;
            await _producer.ConfigureAwait(false);
            return Metadata(completed: true);
        }
        finally { if (!_idle.IsCancellationRequested) _idle.CancelAfter(TimeSpan.FromSeconds(20)); }
    }

    private async Task ProduceAsync(BackendMediaDecoder decoder, SceneSnapshot scene, BackendVideoInfo info,
        string videoPath, IReadOnlyList<(AudioSlice Slice, string Path)> audio, CancellationToken token)
    {
        Exception? failure = null;
        try
        {
            if (_metadata.Kind == "video") await VideoAsync(decoder, scene, info, videoPath, token).ConfigureAwait(false);
            else await AudioAsync(decoder, audio, token).ConfigureAwait(false);
        }
        catch (Exception error) { failure = error; }
        finally
        {
            foreach (var lease in _leases)
                try { await lease.DisposeAsync().ConfigureAwait(false); }
                catch (IOException error) { failure ??= error; }
            _queue.Writer.TryComplete(failure);
        }
    }

    private static string Number(double value) => value.ToString("F12", CultureInfo.InvariantCulture);

    private async Task VideoAsync(BackendMediaDecoder decoder, SceneSnapshot scene, BackendVideoInfo info, string path, CancellationToken token)
    {
        var clip = scene.Clip;
        int Native(int frame) => Math.Clamp(checked((int)Math.Floor((clip.SourceInFrame + frame * (clip.Speed ?? 1)) * info.Fps / scene.Fps)), 0, info.FrameCount - 1);
        var first = Native(_metadata.StartFrame);
        var arguments = new List<string> { "-ss", Number(Math.Max(0, first - .25) / info.Fps) };
        arguments.AddRange(BackendMediaDecoder.Input(path));
        arguments.AddRange(["-map", "0:v:0", "-an", "-sn", "-dn", "-fps_mode", "passthrough", "-vf",
            "scale=480:270:force_original_aspect_ratio=decrease,pad=480:270:(ow-iw)/2:(oh-ih)/2", "-threads", "1", "-pix_fmt", "bgra", "-f", "rawvideo", "pipe:1"]);
        await using var child = decoder.Stream(arguments, token);
        var scratch = new byte[BackendMediaDecoder.FrameBytes];
        byte[]? pixels = null; var previous = first - 1;
        for (var frame = _metadata.StartFrame; frame < clip.DurationFrames; frame++)
        {
            var desired = Native(frame);
            while (previous < desired)
            {
                await child.ReadExactlyAsync(scratch, token).ConfigureAwait(false);
                previous++;
                if (previous == desired) pixels = scratch.ToArray();
            }
            var reply = Metadata(); reply.LocalFrame = frame; reply.NativeFrame = desired - _nativeOffset; reply.Data = pixels!;
            reply.DataSha256 = Convert.ToHexString(SHA256.HashData(reply.Data));
            await _queue.Writer.WriteAsync(reply, token).ConfigureAwait(false);
        }
        // Selected-clip completion stops the decoder even if the source has a long tail.
    }

    private async Task AudioAsync(BackendMediaDecoder decoder, IReadOnlyList<(AudioSlice Slice, string Path)> slices, CancellationToken token)
    {
        var arguments = new List<string> { "-f", "lavfi", "-i", "anullsrc=r=48000:cl=stereo" };
        var filters = new List<string> { $"[0:a:0]atrim=end_sample={_metadata.TotalSamples}[base]" };
        var inputs = "[base]"; var count = 1;
        var streams = new Dictionary<(string Path, int Stream), bool>();
        foreach (var (slice, path) in slices)
        {
            if (!streams.TryGetValue((path, slice.Stream), out var available))
                streams.Add((path, slice.Stream), available = await decoder.HasAudioAsync(path, slice.Stream, token).ConfigureAwait(false));
            if (!available)
            {
                if (slice.Optional) continue;
                throw new InvalidDataException("Required registered audio stream is missing.");
            }
            arguments.AddRange(BackendMediaDecoder.Input(path, audio: true));
            var tempos = new List<string>(); var rate = slice.Rate;
            while (rate < .5) { tempos.Add("atempo=0.5"); rate /= .5; }
            while (rate > 2) { tempos.Add("atempo=2"); rate /= 2; }
            // Unity rate preserves the original samples; atempo=1 can alter a short event's tail.
            if (rate != 1) tempos.Add("atempo=" + Number(rate));
            filters.Add($"[{count}:a:{slice.Stream}]atrim=start={Number(slice.SourceSeconds)}:duration={Number(slice.SourceDuration)},asetpts=PTS-STARTPTS," +
                (tempos.Count == 0 ? "" : string.Join(',', tempos) + ",") + $"aresample=48000,aformat=sample_fmts=flt:channel_layouts=stereo,apad,atrim=end_sample={slice.Samples}," +
                $"volume={Number(slice.Gain)}:precision=float,adelay={slice.OutputSample}S|{slice.OutputSample}S[a{count}]");
            inputs += $"[a{count}]"; count++;
        }
        filters.Add(count == 1 ? "[base]anull[mix]" :
            inputs + $"amix=inputs={count}:duration=first:dropout_transition=0:normalize=0,aeval=exprs='clip(val(0),-1,1)|clip(val(1),-1,1)',atrim=end_sample={_metadata.TotalSamples}[mix]");
        arguments.AddRange(["-filter_complex_threads", "1", "-filter_complex", string.Join(';', filters), "-map", "[mix]", "-vn", "-sn", "-dn",
            "-ar", "48000", "-ac", "2", "-threads", "1", "-f", "f32le", "pipe:1"]);
        await using var child = decoder.Stream(arguments, token);
        for (var offset = 0; offset < _metadata.TotalSamples;)
        {
            var frames = Math.Min(AudioChunkBytes / 8, _metadata.TotalSamples - offset);
            var bytes = new byte[frames * 8];
            await child.ReadExactlyAsync(bytes, token).ConfigureAwait(false);
            ValidatePcm(bytes);
            var reply = Metadata(); reply.SampleOffset = offset; reply.Data = bytes;
            reply.DataSha256 = Convert.ToHexString(SHA256.HashData(bytes));
            await _queue.Writer.WriteAsync(reply, token).ConfigureAwait(false);
            offset += frames;
        }
        await child.CheckAsync(token).ConfigureAwait(false);
    }

    private static void ValidatePcm(ReadOnlySpan<byte> bytes)
    {
        foreach (var value in MemoryMarshal.Cast<byte, float>(bytes))
            if (!float.IsFinite(value) || value is < -1 or > 1) throw new InvalidDataException("Invalid mixed PCM sample.");
    }

    public async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0) return;
        _stop.Cancel();
        try { await _producer.WaitAsync(TimeSpan.FromSeconds(7)).ConfigureAwait(false); }
        finally { _stop.Dispose(); _idle.Dispose(); }
    }
}
