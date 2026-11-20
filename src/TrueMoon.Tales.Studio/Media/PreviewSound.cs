using NAudio.Wave;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using TrueMoon.Tales.Studio.Editing;

namespace TrueMoon.Tales.Studio.Media;

internal static class PreviewSound
{
    public static IPreviewSound Create(PreviewAudio audio, int localFrame)
    {
        if (!audio.Peaks.Any(peak => peak > 0)) return new SilentClock(null);
        if (!OperatingSystem.IsWindows()) return new SilentClock("Звуковой вывод доступен в Windows");
        try { return new Device(new Provider(audio, localFrame)); }
        catch (Exception error) when (error is NAudio.MmException or InvalidOperationException)
        { return new SilentClock("Звук недоступен · " + error.Message); }
    }

    public static IPreviewSound CreateBuffered(IWaveProvider source)
    {
        if (!OperatingSystem.IsWindows()) return new BufferedClock(source, "Звуковой вывод доступен в Windows");
        try { return new Device(source); }
        catch (Exception error) when (error is NAudio.MmException or InvalidOperationException)
        { return new BufferedClock(source, "Звук недоступен · " + error.Message); }
    }

    [SupportedOSPlatform("windows")]
    private sealed class Device : IPreviewSound
    {
        private readonly WaveOut _output = new() { BufferMilliseconds = 40, NumberOfBuffers = 3 };
        private Exception? _error;
        public string? Warning => null;
        public Exception? Error => Volatile.Read(ref _error);
        public long PlayedSamples => _output.GetPosition() / (sizeof(float) * AudioTimeline.Channels);
        public Device(IWaveProvider source)
        {
            try { _output.Init(source); _output.PlaybackStopped += Stopped; }
            catch { _output.Dispose(); throw; }
        }
        private void Stopped(object? sender, StoppedEventArgs args) { if (args.Exception is { } error) Volatile.Write(ref _error, error); }
        public void Play() => _output.Play();
        public void Dispose() { _output.PlaybackStopped -= Stopped; _output.Dispose(); }
    }

    private sealed class Provider(PreviewAudio audio, int frame) : IWaveProvider
    {
        private int _position = audio.Offset(frame) * AudioTimeline.Channels;
        public WaveFormat WaveFormat { get; } = WaveFormat.CreateIeeeFloatWaveFormat(AudioTimeline.SampleRate, AudioTimeline.Channels);
        public int Read(byte[] buffer, int offset, int count)
            => Read(buffer.AsSpan(offset, count));
        public int Read(Span<byte> buffer)
        {
            var samples = Math.Min(buffer.Length / (sizeof(float) * AudioTimeline.Channels) * AudioTimeline.Channels, audio.Samples.Length - _position);
            MemoryMarshal.AsBytes(audio.Samples.AsSpan(_position, samples)).CopyTo(buffer);
            _position += samples;
            return samples * sizeof(float);
        }
    }

    private sealed class SilentClock(string? warning) : IPreviewSound
    {
        private readonly Stopwatch _clock = new();
        public long PlayedSamples => (long)(_clock.Elapsed.TotalSeconds * AudioTimeline.SampleRate);
        public string? Warning => warning;
        public Exception? Error => null;
        public void Play() => _clock.Start();
        public void Dispose() => _clock.Stop();
    }

    private sealed class BufferedClock(IWaveProvider source, string warning) : IPreviewSound
    {
        private readonly CancellationTokenSource _stop = new();
        private Task? _drain;
        private long _played;
        private Exception? _error;

        public long PlayedSamples => Interlocked.Read(ref _played);
        public string Warning => warning;
        public Exception? Error => Volatile.Read(ref _error);

        public void Play()
        {
            var token = _stop.Token;
            _drain ??= Task.Run(() => ConsumeAsync(token));
        }

        private async Task ConsumeAsync(CancellationToken token)
        {
            var clock = Stopwatch.StartNew();
            var buffer = new byte[960 * source.WaveFormat.BlockAlign];
            long samples = 0;
            try
            {
                while (!token.IsCancellationRequested)
                {
                    var bytes = source.Read(buffer);
                    if (bytes == 0) return;
                    samples += bytes / source.WaveFormat.BlockAlign;
                    var wait = TimeSpan.FromSeconds(samples / (double)AudioTimeline.SampleRate) - clock.Elapsed;
                    if (wait > TimeSpan.Zero) await Task.Delay(wait, token).ConfigureAwait(false);
                    Interlocked.Exchange(ref _played, samples);
                }
            }
            catch (OperationCanceledException) when (token.IsCancellationRequested) { }
            catch (Exception error) { Volatile.Write(ref _error, error); }
        }

        public void Dispose()
        {
            _stop.Cancel();
            if (_drain is null) _stop.Dispose();
            else _ = _drain.ContinueWith(_ => _stop.Dispose(), CancellationToken.None, TaskContinuationOptions.ExecuteSynchronously, TaskScheduler.Default);
        }
    }
}
