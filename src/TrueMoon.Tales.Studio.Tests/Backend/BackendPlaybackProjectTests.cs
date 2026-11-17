using System.Text.Json;
using TrueMoon.Tales.Studio.Backend;
using TrueMoon.Tales.Studio.Contracts.Projects;
using TrueMoon.Tales.Studio.Persistence;
using TrueMoon.Tales.Studio.Tests.Persistence;
using Xunit;

namespace TrueMoon.Tales.Studio.Tests.Backend;

public sealed class BackendPlaybackProjectTests
{
    private static ProjectContract Project(Dictionary<string, JsonElement>? properties = null) =>
        ProjectFileCodec.Read(PersistenceFixture.LegacyBytes()).Project is { } project
            ? project with { Montage = project.Montage with { Properties = properties ?? [] } }
            : throw new InvalidOperationException();

    private static JsonElement Json(string value) { using var document = JsonDocument.Parse(value); return document.RootElement.Clone(); }

    [Fact]
    public void PlaybackWireBuffer_CopiesManagedBytesAndReturnsWholeRentalExactlyOnce()
    {
        var original = new byte[] { 1, 2, 3 };
        var reply = new BackendPlaybackReply { DataBuffer = original };
        original[0] = 9;
        Assert.Equal(new byte[] { 1, 2, 3 }, reply.Data);
        var rental = MemoryPoolUtils.Create(3);
        new byte[] { 4, 5, 6 }.AsSpan().CopyTo(rental.Span);
        reply.DataBuffer = rental;
        Assert.Equal(new byte[] { 4, 5, 6 }, reply.Data);
        Assert.False(MemoryPoolUtils.TryReturn(rental));
    }

    [Fact]
    public void AudioProjection_MixesNativeMusicAndClipEventOnAbsoluteSampleGrid_WithoutChangingProject()
    {
        var project = Project(new()
        {
            ["clip_audio"] = Json("true"),
            ["music"] = Json("""{"asset_id":"music","start_frame":0,"duration_frames":240,"source_in_frame":12,"gain":0.3,"muted":false}"""),
            ["sound_events"] = Json("""[{"asset_id":"event","segment_id":"segment-main","clip_id":"clip-main","offset_frames":6,"duration_frames":6,"source_in_frame":3,"stream":1,"gain":0.5,"muted":false}]""")
        });
        var before = System.Text.Json.JsonSerializer.Serialize(project);
        var scene = BackendPlaybackProject.Scene(project, "clip-main");
        Assert.Equal(3, scene.Audio!.Length);
        var native = scene.Audio.Single(row => row.AssetId == "video-a");
        Assert.True(native.Optional); Assert.Equal(0, native.OutputSample); Assert.Equal(48000, native.Samples);
        Assert.Equal(1, native.Rate); Assert.Equal(0, native.SourceSeconds); Assert.Equal(1, native.SourceDuration);
        var music = scene.Audio.Single(row => row.AssetId == "music");
        Assert.False(music.Optional); Assert.Equal(1, music.SourceSeconds); Assert.Equal(48000, music.Samples); Assert.Equal(.3, music.Gain);
        var sound = scene.Audio.Single(row => row.AssetId == "event");
        Assert.Equal(12000, sound.OutputSample); Assert.Equal(12000, sound.Samples); Assert.Equal(.125, sound.SourceSeconds);
        Assert.Equal(1, sound.Stream); Assert.Equal(.5, sound.Gain);
        Assert.Equal(before, System.Text.Json.JsonSerializer.Serialize(project));
    }

    [Fact]
    public void NativeMuteAndMutedEvents_DoNotContribute_AndAbsentAudioMeansSilence()
    {
        var project = Project(new() { ["clip_audio"] = Json("true"), ["sound_events"] = Json("""[{"asset_id":"event","segment_id":"segment-main","offset_frames":0,"duration_frames":240,"source_in_frame":0,"stream":0,"gain":1,"muted":true}]""") });
        project = project with { Montage = project.Montage with { Clips = project.Montage.Clips.Select(row => row with { MuteNativeAudio = true }).ToArray() } };
        Assert.Empty(BackendPlaybackProject.Scene(project, "clip-main").Audio!);
        Assert.Empty(BackendPlaybackProject.Scene(Project(), "clip-main").Audio!);
    }

    [Theory]
    [InlineData("missing")]
    [InlineData("")]
    public void UnknownClipIsRejected(string clip) => Assert.Throws<InvalidDataException>(() => BackendPlaybackProject.Scene(Project(), clip));

    [Theory]
    [InlineData(0, 1)]
    [InlineData(14401, 1)]
    [InlineData(24, .249)]
    [InlineData(24, 4.001)]
    public void DurationAndSpeedLimitsAreEnforced(int frames, double speed)
    {
        var project = Project();
        project = project with { Montage = project.Montage with { Clips = project.Montage.Clips.Select(row => row with { DurationFrames = frames, Speed = speed }).ToArray() } };
        Assert.Throws<InvalidDataException>(() => BackendPlaybackProject.Scene(project, "clip-main"));
    }

    [Theory]
    [InlineData("-0.01")]
    [InlineData("1.01")]
    public void InvalidSavedGainIsRejected(string gain)
    {
        var project = Project(new() { ["music"] = Json("{\"asset_id\":\"music\",\"start_frame\":0,\"duration_frames\":24,\"source_in_frame\":0,\"gain\":" + gain + ",\"muted\":false}") });
        Assert.Throws<InvalidDataException>(() => BackendPlaybackProject.Scene(project, "clip-main"));
    }

    [Theory]
    [InlineData(".wav")]
    [InlineData(".mp3")]
    [InlineData(".flac")]
    [InlineData(".ogg")]
    [InlineData(".m4a")]
    public void AudioInputUsesLocalProtocolAndExplicitDemuxer(string extension)
    {
        var path = Path.GetFullPath("sound" + extension);
        var args = BackendMediaDecoder.Input(path, audio: true);
        Assert.Equal("file,pipe", args[Array.IndexOf(args, "-protocol_whitelist") + 1]);
        Assert.Equal(path, args[^1]); Assert.Contains("-f", args);
        Assert.Throws<InvalidDataException>(() => BackendMediaDecoder.Input(path));
    }

    [Theory]
    [InlineData("http://example.invalid/source.wav")]
    [InlineData("\\\\server\\share\\sound.wav")]
    public void AudioDoesNotExpandRemoteAuthority(string path) => Assert.Throws<UnauthorizedAccessException>(() => BackendMediaDecoder.Input(path, audio: true));
}
