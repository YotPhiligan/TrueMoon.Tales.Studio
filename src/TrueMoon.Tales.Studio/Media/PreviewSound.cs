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
        try { return new Device(audio, localFrame); }
        catch (Exception error) when (error is NAudio.MmException or InvalidOperationException)
        { return new SilentClock("Звук недоступен · " + error.Message); }
    }

    [SupportedOSPlatform("windows")]
    private sealed class Device : IPreviewSound
    {
        private readonly WaveOut _output = new() { BufferMilliseconds = 40, NumberOfBuffers = 3 };
        private Exception? _error;
        public string? Warning => null;
        public Exception? Error => Volatile.Read(ref _error);
        public long PlayedSamples => _output.GetPosition() / (sizeof(float) * AudioTimeline.Channels);
        public Device(PreviewAudio audio, int frame)
        {
            try { _output.Init(new Provider(audio, frame)); _output.PlaybackStopped += Stopped; }
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
}
