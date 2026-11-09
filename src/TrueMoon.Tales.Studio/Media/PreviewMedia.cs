using TrueMoon.Tales.Studio.Editing;
using TrueMoon.Tales.Studio.Models;

namespace TrueMoon.Tales.Studio.Media;

internal sealed record PreviewAudio(float[] Samples, float[] Peaks, double Fps, int TimelineStart, int Frames)
{
    public int SampleFrames => Samples.Length / AudioTimeline.Channels;
    public int Offset(int frame) => checked((int)(AudioTimeline.Sample(TimelineStart + frame, Fps) - AudioTimeline.Sample(TimelineStart, Fps)));
}

internal sealed record PlaybackFrame(int LocalFrame, int NativeFrame, byte[] Pixels);

internal interface IPreviewVideo : IDisposable
{
    Task Completion { get; }
    bool TryRead(out PlaybackFrame? frame);
    bool Complete { get; }
    Exception? Error { get; }
}

internal interface IPreviewSound : IDisposable
{
    long PlayedSamples { get; }
    string? Warning { get; }
    Exception? Error { get; }
    void Play();
}

internal interface IPreviewMedia
{
    Task<PreviewAudio> PrepareAsync(SceneSnapshot scene, CancellationToken token);
    IPreviewVideo Video(SceneSnapshot scene, MediaInfo media, int localFrame, CancellationToken token);
    IPreviewSound Sound(PreviewAudio audio, int localFrame);
}

internal static class Waveform
{
    public static float[] Peaks(float[] samples, int bins = 1024)
    {
        if (samples.Length == 0) return [];
        var peaks = new float[Math.Min(bins, samples.Length / AudioTimeline.Channels)];
        for (var index = 0; index < samples.Length; index++)
        {
            var bin = (int)((long)(index / AudioTimeline.Channels) * peaks.Length / (samples.Length / AudioTimeline.Channels));
            peaks[bin] = Math.Max(peaks[bin], Math.Min(1, Math.Abs(samples[index])));
        }
        return peaks;
    }
}
