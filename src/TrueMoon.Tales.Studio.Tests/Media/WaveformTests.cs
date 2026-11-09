using TrueMoon.Tales.Studio.Media;
using Xunit;

namespace TrueMoon.Tales.Studio.Tests.Media;

public sealed class WaveformTests
{
    [Fact]
    public void Peaks_UseBothChannelsAbsoluteMagnitudeAndClipToFullScale()
    {
        float[] pcm = [.1f, -.8f, .4f, .2f, .6f, -.3f, 1.4f, -.5f];
        var before = pcm.ToArray();

        Assert.Equal(new[] { .8f, 1f }, Waveform.Peaks(pcm, 2));
        Assert.Equal(before, pcm);
    }

    [Fact]
    public void Peaks_SilencePreservesBinsAndEmptyHasNoWave()
    {
        Assert.Empty(Waveform.Peaks([]));
        Assert.Equal(new float[8], Waveform.Peaks(new float[16]));
    }

    [Fact]
    public void Peaks_DoNotCreateMoreBinsThanStereoSampleFrames()
    {
        Assert.Equal(new[] { .7f, .4f }, Waveform.Peaks([-.7f, .2f, .1f, -.4f], 1024));
    }
}
