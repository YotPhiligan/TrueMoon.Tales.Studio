using TrueMoon.Tales.Studio.Backend;
using Xunit;
using static TrueMoon.Tales.Studio.Tests.Backend.BackendPreviewMediaTests;

namespace TrueMoon.Tales.Studio.Tests.Backend;

public sealed class BackendPlaybackValidationTests
{
    [Theory]
    [InlineData("session")]
    [InlineData("kind")]
    [InlineData("clip")]
    [InlineData("revision")]
    [InlineData("sha")]
    [InlineData("fps")]
    [InlineData("source-fps")]
    [InlineData("duration")]
    [InlineData("start")]
    [InlineData("samples")]
    [InlineData("completed")]
    [InlineData("local")]
    [InlineData("native")]
    [InlineData("offset")]
    [InlineData("payload")]
    [InlineData("payload-sha")]
    public void Open_RejectsWrongSessionBaselineExtentAndUnexpectedPayload(string field)
    {
        var scene = Scene();
        var reply = Opened(scene, "video", 1);
        BackendConnection.ValidatePlaybackOpen(reply, scene, "video", 1);
        switch (field)
        {
            case "session": reply.SessionId = new('z', 32); break;
            case "kind": reply.Kind = "audio"; break;
            case "clip": reply.ClipId = "other"; break;
            case "revision": reply.Revision++; break;
            case "sha": reply.Fingerprint = new('b', 64); break;
            case "fps": reply.Fps++; break;
            case "source-fps": reply.SourceFps = double.NaN; break;
            case "duration": reply.DurationFrames++; break;
            case "start": reply.StartFrame++; break;
            case "samples": reply.TotalSamples++; break;
            case "completed": reply.Completed = true; break;
            case "local": reply.LocalFrame = 0; break;
            case "native": reply.NativeFrame = 0; break;
            case "offset": reply.SampleOffset = 1; break;
            case "payload": reply.Data = [1]; break;
            case "payload-sha": reply.DataSha256 = new('a', 64); break;
        }
        Assert.Throws<InvalidDataException>(() => BackendConnection.ValidatePlaybackOpen(reply, scene, "video", 1));
    }

    [Theory]
    [InlineData("session")]
    [InlineData("baseline")]
    [InlineData("source-fps")]
    [InlineData("length")]
    [InlineData("corrupt")]
    [InlineData("local-before-start")]
    [InlineData("local-end")]
    [InlineData("native-negative")]
    [InlineData("native-limit")]
    [InlineData("pcm-offset")]
    [InlineData("completed-with-data")]
    public void Read_RejectsChangedIdentityCorruptPixelsAndInvalidFrameBounds(string field)
    {
        var opened = Opened(Scene(), "video", 1);
        var reply = Packet(opened, new byte[BackendMediaDecoder.FrameBytes], local: 1, native: 8);
        BackendConnection.ValidatePlaybackRead(reply, opened);
        switch (field)
        {
            case "session": reply.SessionId = Guid.NewGuid().ToString("N"); break;
            case "baseline": reply.Fingerprint = new('b', 64); break;
            case "source-fps": reply.SourceFps++; break;
            case "length": reply.Data = new byte[BackendMediaDecoder.FrameBytes - 1]; break;
            case "corrupt": reply.Data[123] = 1; break;
            case "local-before-start": reply.LocalFrame = 0; break;
            case "local-end": reply.LocalFrame = opened.DurationFrames; break;
            case "native-negative": reply.NativeFrame = -1; break;
            case "native-limit": reply.NativeFrame = BackendMediaDecoder.MaxFrames; break;
            case "pcm-offset": reply.SampleOffset = 1; break;
            case "completed-with-data": reply.Completed = true; break;
        }
        Assert.Throws<InvalidDataException>(() => BackendConnection.ValidatePlaybackRead(reply, opened));
    }

    [Theory]
    [InlineData("empty")]
    [InlineData("oversize")]
    [InlineData("unaligned")]
    [InlineData("negative")]
    [InlineData("past-end")]
    [InlineData("video-index")]
    [InlineData("hash")]
    [InlineData("incomplete-end")]
    public void Read_RejectsInvalidPcmChunkShapeBoundsAndCompletion(string field)
    {
        var opened = Opened(Scene(), "audio");
        var reply = Packet(opened, new byte[8]);
        BackendConnection.ValidatePlaybackRead(reply, opened);
        switch (field)
        {
            case "empty": reply = Packet(opened, []); break;
            case "oversize": reply = Packet(opened, new byte[32776]); break;
            case "unaligned": reply = Packet(opened, new byte[7]); break;
            case "negative": reply.SampleOffset = -1; break;
            case "past-end": reply.SampleOffset = opened.TotalSamples; break;
            case "video-index": reply.LocalFrame = 0; break;
            case "hash": reply.DataSha256 = ""; break;
            case "incomplete-end": reply = Packet(opened, offset: opened.TotalSamples - 1, complete: true); break;
        }
        Assert.Throws<InvalidDataException>(() => BackendConnection.ValidatePlaybackRead(reply, opened));
    }

    [Fact]
    public void Read_AcceptsOnlyEmptyCompletionWithExactMetadata_AndRefusalRetainsConflictCode()
    {
        var opened = Opened(Scene(), "audio");
        BackendConnection.ValidatePlaybackRead(Packet(opened, offset: opened.TotalSamples, complete: true), opened);
        var error = Assert.Throws<BackendCommandException>(() => BackendConnection.ValidatePlaybackRead(new() { ErrorCode = "revision_conflict" }, opened));
        Assert.Equal("revision_conflict", error.Code);
        Assert.Throws<InvalidDataException>(() => BackendConnection.ValidatePlaybackRead(null!, opened));
    }

    [Theory]
    [InlineData("kind")]
    [InlineData("audio-seek")]
    [InlineData("negative-seek")]
    [InlineData("end-seek")]
    [InlineData("long")]
    [InlineData("sha")]
    [InlineData("infinite-fps")]
    public void Request_RejectsInvalidCapabilityBaselineKindAndClipBounds(string fault)
    {
        var scene = Scene();
        var kind = fault == "kind" ? "path" : fault == "audio-seek" ? "audio" : "video";
        var frame = fault == "audio-seek" ? 1 : fault == "negative-seek" ? -1 : fault == "end-seek" ? 3 : 0;
        if (fault == "long") scene = Scene(duration: 24 * 600 + 1);
        if (fault == "sha") scene = scene with { Fingerprint = new('z', 64) };
        if (fault == "infinite-fps") scene = scene with { Fps = double.PositiveInfinity };
        Assert.Throws<InvalidDataException>(() => BackendConnection.ValidateScene(scene, kind, frame));
    }
}
