using TrueMoon.Tales.Studio.Editing;
using TrueMoon.Tales.Studio.Models;
using Xunit;

namespace TrueMoon.Tales.Studio.Tests.Editing;

public sealed class AudioTimelineCompositionTests
{
    [Fact]
    public void AudioTimeline_CombinesNativeMusicAndEventOnOneAbsoluteWindow()
    {
        var clip = new Clip("clip", "segment", "block", "take", null, 10, 8, 4, 20, 2);
        var music = new AudioMusic("music", 8, 12, 3, .3, false);
        var voice = new AudioEvent("voice", "segment", null, 6, 3, 7, 2, .7, false);
        var slices = AudioTimeline.Build(clip, "video", 24, [clip], [new("segment", "Scene", 8, 20)], true, music, [voice]);

        Assert.Equal(new[] { "video", "music", "voice" }, slices.Select(slice => slice.AssetId));
        Assert.Equal(new[] { 0, 0, 8000 }, slices.Select(slice => slice.OutputSample));
        Assert.Equal(new[] { 16000, 16000, 6000 }, slices.Select(slice => slice.Samples));
        Assert.Equal(new[] { 2d, 1d, 1d }, slices.Select(slice => slice.Rate));
        Assert.Equal(new[] { 1d, .3, .7 }, slices.Select(slice => slice.Gain));
        Assert.Equal(new[] { true, false, false }, slices.Select(slice => slice.Optional));
        Assert.Equal(2, slices[2].Stream);
        Assert.Equal(7d / 24, slices[2].SourceSeconds, 12);
    }
}
