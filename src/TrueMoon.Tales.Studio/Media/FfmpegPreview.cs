using System.Globalization;
using System.Runtime.InteropServices;
using System.Threading.Channels;
using TrueMoon.Tales.Studio.Editing;
using TrueMoon.Tales.Studio.Infrastructure;
using TrueMoon.Tales.Studio.Models;

namespace TrueMoon.Tales.Studio.Media;

internal sealed class FfmpegPreview(string executable, StudioHttpClient api) : IPreviewMedia
{
    private static string Number(double value) => value.ToString("F12", CultureInfo.InvariantCulture);

    public Task<PreviewAudio> PrepareAsync(SceneSnapshot scene, CancellationToken token) => Task.Run(() => PrepareCore(scene, token), token);

    private async Task<PreviewAudio> PrepareCore(SceneSnapshot scene, CancellationToken token)
    {
        var seconds = scene.Clip.DurationFrames / scene.Fps;
        if (!double.IsFinite(seconds) || seconds is <= 0 or > 600) throw new InvalidDataException("Просмотр ограничен клипом до 10 минут");
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(token);
        deadline.CancelAfter(TimeSpan.FromSeconds(45));
        token = deadline.Token;
        var start = AudioTimeline.Sample(scene.Clip.StartFrame, scene.Fps);
        var count = checked((int)(AudioTimeline.Sample(scene.Clip.StartFrame + scene.Clip.DurationFrames, scene.Fps) - start));
        var samples = new float[checked(count * AudioTimeline.Channels)];
        foreach (var slice in scene.Audio ?? [])
        {
            token.ThrowIfCancellationRequested();
            if (slice.Optional && !await api.HasAudioAsync(slice.AssetId, slice.Stream, token).ConfigureAwait(false)) continue;
            await AddAudio(samples, slice, token).ConfigureAwait(false);
        }
        for (var i = 0; i < samples.Length; i++) samples[i] = Math.Clamp(samples[i], -1, 1);
        return new(samples, Waveform.Peaks(samples), scene.Fps, scene.Clip.StartFrame, scene.Clip.DurationFrames);
    }

    private async Task AddAudio(float[] mix, AudioSlice slice, CancellationToken token)
    {
        var tempos = new List<string>();
        var rate = slice.Rate;
        if (!double.IsFinite(rate) || rate is < .25 or > 4 || !double.IsFinite(slice.Gain) || slice.Gain is < 0 or > 1 ||
            slice.OutputSample < 0 || slice.Samples < 1 || slice.OutputSample + (long)slice.Samples > mix.Length / AudioTimeline.Channels)
            throw new InvalidDataException("Некорректный участок звука");
        while (rate < .5) { tempos.Add("atempo=0.5"); rate /= .5; }
        while (rate > 2) { tempos.Add("atempo=2"); rate /= 2; }
        tempos.Add("atempo=" + Number(rate));
        var filter = $"atrim=start={Number(slice.SourceSeconds)}:duration={Number(slice.SourceDuration)},asetpts=PTS-STARTPTS," +
                     string.Join(',', tempos) + $",aresample={AudioTimeline.SampleRate},aformat=channel_layouts=stereo,apad,atrim=end_sample={slice.Samples}";
        await using var child = new FfmpegChild(executable,
            ["-i", api.Media(slice.AssetId).AbsoluteUri, "-map", $"0:a:{slice.Stream}", "-vn", "-af", filter,
             "-ar", "48000", "-ac", "2", "-threads", "1", "-f", "f32le", "pipe:1"], token);
        var buffer = new byte[8192];
        var offset = checked(slice.OutputSample * AudioTimeline.Channels);
        var remaining = Math.Min(slice.Samples * AudioTimeline.Channels, mix.Length - offset);
        while (remaining > 0)
        {
            var bytes = Math.Min(buffer.Length, remaining * sizeof(float));
            await child.Output.ReadExactlyAsync(buffer.AsMemory(0, bytes), token).ConfigureAwait(false);
            AddSamples(mix, offset, buffer.AsSpan(0, bytes), slice.Gain);
            offset += bytes / sizeof(float); remaining -= bytes / sizeof(float);
        }
        await child.CheckAsync(token).ConfigureAwait(false);
    }

    private static void AddSamples(float[] mix, int offset, ReadOnlySpan<byte> bytes, double gain)
    {
        var floats = MemoryMarshal.Cast<byte, float>(bytes);
        for (var i = 0; i < floats.Length; i++)
        {
            if (!float.IsFinite(floats[i])) throw new InvalidDataException("Некорректный PCM");
            mix[offset + i] += (float)(floats[i] * gain);
        }
    }

    public IPreviewVideo Video(SceneSnapshot scene, MediaInfo media, int localFrame, CancellationToken token) =>
        new VideoSequence(executable, api.Media(scene.Takes.First(take => take.Id == scene.Clip.TakeId).VideoAssetId), scene, media, localFrame, token);

    public IPreviewSound Sound(PreviewAudio audio, int localFrame) => PreviewSound.Create(audio, localFrame);

    private sealed class VideoSequence : IPreviewVideo
    {
        private readonly CancellationTokenSource _stop;
        private readonly Channel<PlaybackFrame> _frames = Channel.CreateBounded<PlaybackFrame>(new BoundedChannelOptions(3)
        { SingleReader = true, SingleWriter = true, FullMode = BoundedChannelFullMode.Wait });
        private readonly Task _producer;
        private Exception? _error;
        public Task Completion => _producer;
        public bool Complete => _producer.IsCompleted && !_frames.Reader.TryPeek(out _);
        public Exception? Error => Volatile.Read(ref _error);
        public bool TryRead(out PlaybackFrame? frame) => _frames.Reader.TryRead(out frame);

        public VideoSequence(string executable, Uri source, SceneSnapshot scene, MediaInfo media, int localFrame, CancellationToken token)
        {
            _stop = CancellationTokenSource.CreateLinkedTokenSource(token);
            _producer = Task.Run(() => Produce(executable, source, scene, media, localFrame, _stop.Token));
        }

        private async Task Produce(string executable, Uri source, SceneSnapshot scene, MediaInfo media, int localFrame, CancellationToken token)
        {
            try
            {
                var clip = scene.Clip;
                int Native(int frame) => Math.Clamp((int)Math.Floor((clip.SourceInFrame + frame * (clip.Speed ?? 1)) * media.Fps / scene.Fps), 0, media.FrameCount - 1);
                var native = Native(localFrame);
                var seek = Math.Max(0, native - .25) / media.Fps;
                await using var child = new FfmpegChild(executable,
                    ["-ss", Number(seek), "-i", source.AbsoluteUri, "-map", "0:v:0", "-an", "-vsync", "0", "-vf",
                     $"scale={FrameDecoder.Width}:{FrameDecoder.Height}:force_original_aspect_ratio=decrease,pad={FrameDecoder.Width}:{FrameDecoder.Height}:(ow-iw)/2:(oh-ih)/2",
                     "-threads", "1", "-pix_fmt", "bgra", "-f", "rawvideo", "pipe:1"], token);
                var scratch = new byte[FrameDecoder.Width * FrameDecoder.Height * 4];
                byte[]? pixels = null;
                var last = native - 1;
                for (var frame = localFrame; frame < clip.DurationFrames; frame++)
                {
                    var desired = Native(frame);
                    while (last < desired)
                    {
                        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(token);
                        deadline.CancelAfter(TimeSpan.FromSeconds(10));
                        await child.Output.ReadExactlyAsync(scratch, deadline.Token).ConfigureAwait(false);
                        last++;
                        if (last == desired) pixels = scratch.ToArray();
                    }
                    await _frames.Writer.WriteAsync(new(frame, desired, pixels!), token).ConfigureAwait(false);
                }
            }
            catch (OperationCanceledException) when (token.IsCancellationRequested) { }
            catch (Exception error) { Volatile.Write(ref _error, error); }
            finally { _frames.Writer.TryComplete(); }
        }

        public void Dispose()
        {
            _stop.Cancel();
            _ = _producer.ContinueWith(_ => _stop.Dispose(), TaskScheduler.Default);
        }
    }
}
