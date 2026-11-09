using TrueMoon.Tales.Studio.Editing;
using TrueMoon.Tales.Studio.Media;
using TrueMoon.Tales.Studio.Models;
using Xunit;

namespace TrueMoon.Tales.Studio.Tests.Editing;

public sealed class AudioTimelineTests
{
    private static Clip Clip(int start = 10, int duration = 8) =>
        new("clip", "segment", "block", "take", null, start, duration, 4, 4 + duration * 2, 2);

    [Fact]
    public void NativeAudio_UsesRetimedSourceAndUnretimedOutputWindow()
    {
        var clip = Clip();
        var slice = Assert.Single(AudioTimeline.Build(clip, "video", 24, [clip], [], true, null, null));

        Assert.Equal("video", slice.AssetId);
        Assert.Equal(0, slice.Stream);
        Assert.Equal(4d / 24, slice.SourceSeconds, 12);
        Assert.Equal(16d / 24, slice.SourceDuration, 12);
        Assert.Equal(0, slice.OutputSample);
        Assert.Equal(16000, slice.Samples);
        Assert.Equal(2, slice.Rate);
        Assert.Equal(1, slice.Gain);
        Assert.True(slice.Optional);
    }

    [Fact]
    public void NativeAudio_PreservesPartialSourceEndAndPadsOutputDuration()
    {
        var clip = Clip() with { SourceOutFrame = 19 };
        var slice = Assert.Single(AudioTimeline.Build(clip, "video", 24, [clip], [], true, null, null));

        Assert.Equal(15d / 24, slice.SourceDuration, 12);
        Assert.Equal(16000, slice.Samples);
        Assert.Equal(2, slice.Rate);
    }

    [Theory]
    [InlineData(false, false)]
    [InlineData(true, true)]
    public void NativeAudio_RespectsGlobalAndClipMute(bool enabled, bool muted)
    {
        var clip = Clip() with { MuteNativeAudio = muted };
        Assert.Empty(AudioTimeline.Build(clip, "video", 24, [clip], [], enabled, null, null));
    }

    [Theory]
    [InlineData(8, 8, 5d / 24, 6d / 24, 0, 12000)]
    [InlineData(14, 8, 3d / 24, 4d / 24, 8000, 8000)]
    public void Music_ClipsToSelectedWindowAndOffsetsSource(int start, int duration, double source, double length, int offset, int count)
    {
        var clip = Clip();
        var music = new AudioMusic("music", start, duration, 3, .25, false);
        var slice = Assert.Single(AudioTimeline.Build(clip, "video", 24, [clip], [], false, music, null));

        Assert.Equal("music", slice.AssetId);
        Assert.Equal(source, slice.SourceSeconds, 12);
        Assert.Equal(length, slice.SourceDuration, 12);
        Assert.Equal(offset, slice.OutputSample);
        Assert.Equal(count, slice.Samples);
        Assert.Equal(1, slice.Rate);
        Assert.Equal(.25, slice.Gain);
        Assert.False(slice.Optional);
    }

    [Theory]
    [InlineData(2, 8, 1, false)] // Ends exactly at the selected clip's start.
    [InlineData(18, 5, 1, false)] // Starts exactly at the selected clip's end.
    [InlineData(10, 8, 0, false)]
    [InlineData(10, 8, 1, true)]
    public void Music_ExcludesOutsideMutedAndZeroGain(int start, int duration, double gain, bool muted)
    {
        var clip = Clip();
        var music = new AudioMusic("music", start, duration, 0, gain, muted);
        Assert.Empty(AudioTimeline.Build(clip, "video", 24, [clip], [], false, music, null));
    }

    [Fact]
    public void SoundEvents_UseOwnerOffsetsAndClipBoundariesWithoutInheritingVideoSpeed()
    {
        var selected = Clip();
        var other = new Clip("owner", "segment", "block", "take", null, 8, 6, 0, 24, 4);
        AudioEvent[] events =
        [
            new("voice", "segment", null, 8, 12, 7, 2, .4, false),
            new("effect", "segment", "owner", 3, 10, 5, 3, .7, false),
        ];
        var slices = AudioTimeline.Build(selected, "video", 24, [selected, other], [new("segment", "Scene", 4, 30)], false, null, events);

        Assert.Equal(2, slices.Length);
        var voice = Assert.Single(slices, slice => slice.AssetId == "voice");
        Assert.Equal(2, voice.Stream);
        Assert.Equal(7d / 24, voice.SourceSeconds, 12);
        Assert.Equal(6d / 24, voice.SourceDuration, 12);
        Assert.Equal(4000, voice.OutputSample);
        Assert.Equal(12000, voice.Samples);
        var effect = Assert.Single(slices, slice => slice.AssetId == "effect");
        Assert.Equal(3, effect.Stream);
        Assert.Equal(5d / 24, effect.SourceSeconds, 12);
        Assert.Equal(3d / 24, effect.SourceDuration, 12);
        Assert.Equal(2000, effect.OutputSample);
        Assert.Equal(6000, effect.Samples);
        Assert.All(slices, slice => Assert.Equal(1, slice.Rate));
    }

    [Fact]
    public void SoundEvents_OmitMissingOwnersExpiredClipEventsAndMutedTracks()
    {
        var selected = Clip();
        AudioEvent[] sounds =
        [
            new("missing-clip", "segment", "deleted", 0, 8, 0, 0, 1, false),
            new("missing-segment", "deleted", null, 0, 8, 0, 0, 1, false),
            new("after-owner", "segment", selected.Id, 8, 8, 0, 0, 1, false),
            new("muted", "segment", selected.Id, 0, 8, 0, 0, 1, true),
            new("zero-gain", "segment", selected.Id, 0, 8, 0, 0, 0, false),
        ];
        Assert.Empty(AudioTimeline.Build(selected, "video", 24, [selected], [new("segment", "Scene", 0, 30)], false, null, sounds));
    }

    [Fact]
    public void SoundEvent_StartingBeforeWindowAdvancesSourceByTheClippedPrefix()
    {
        var selected = Clip();
        var sound = new AudioEvent("voice", "segment", null, 0, 20, 3, 1, .5, false);
        var slice = Assert.Single(AudioTimeline.Build(selected, "video", 24, [selected], [new("segment", "Scene", 8, 30)], false, null, [sound]));

        Assert.Equal(5d / 24, slice.SourceSeconds, 12);
        Assert.Equal(8d / 24, slice.SourceDuration, 12);
        Assert.Equal(0, slice.OutputSample);
        Assert.Equal(16000, slice.Samples);
    }

    [Fact]
    public void FractionalFps_UsesAbsoluteSampleRoundingAtBothWindowEdges()
    {
        const double fps = 23.976;
        var clip = Clip(249, 3);
        var music = new AudioMusic("music", 250, 1, 0, 1, false);
        var slice = Assert.Single(AudioTimeline.Build(clip, "video", fps, [clip], [], false, music, null));

        Assert.Equal(2003, slice.OutputSample);
        Assert.Equal(2002, slice.Samples);
        var audio = new PreviewAudio(new float[12014], [.1f], fps, 249, 3);
        Assert.Equal(6007, audio.SampleFrames);
        Assert.Equal(2003, audio.Offset(1));
        Assert.Equal(4005, audio.Offset(2));
        Assert.Equal(6007, audio.Offset(3));
        Assert.Equal(4_147_200_000L, AudioTimeline.Sample(518400, 6));
    }
}
