using System.Runtime.InteropServices;
using System.Security.Cryptography;
using TrueMoon.Tales.Studio.Backend;
using TrueMoon.Tales.Studio.Media;
using TrueMoon.Tales.Studio.Models;
using Xunit;

namespace TrueMoon.Tales.Studio.Tests.Backend;

public sealed class BackendPreviewMediaTests
{
    internal static SceneSnapshot Scene(double fps = 24, int duration = 3) => new("Playback", 12, fps,
        new("window", "Сегмент", 0, 240), new("clip", "window", "source", "take", "Клип", 7, duration, 4, null, 2),
        "Клип", [new("take", "video", new(30, 3, 90))], [], Fingerprint: new('a', 64));

    internal static BackendPlaybackReply Opened(SceneSnapshot scene, string kind, int frame = 0) => new()
    {
        SessionId = Guid.NewGuid().ToString("N"), Kind = kind, ClipId = scene.Clip.Id, Revision = scene.Revision,
        Fingerprint = scene.Fingerprint!, Fps = scene.Fps, SourceFps = 30, DurationFrames = scene.Clip.DurationFrames, StartFrame = frame,
        TotalSamples = checked((int)(Math.Round((scene.Clip.StartFrame + scene.Clip.DurationFrames) / scene.Fps * 48000, MidpointRounding.ToEven) -
            Math.Round(scene.Clip.StartFrame / scene.Fps * 48000, MidpointRounding.ToEven)))
    };

    internal static BackendPlaybackReply Packet(BackendPlaybackReply opened, byte[]? data = null, int local = -1, int native = -1, int offset = 0, bool complete = false) => new()
    {
        SessionId = opened.SessionId, Kind = opened.Kind, ClipId = opened.ClipId, Revision = opened.Revision, Fingerprint = opened.Fingerprint,
        Fps = opened.Fps, SourceFps = opened.SourceFps, DurationFrames = opened.DurationFrames, StartFrame = opened.StartFrame,
        TotalSamples = opened.TotalSamples, Data = data ?? [], LocalFrame = local, NativeFrame = native, SampleOffset = offset, Completed = complete,
        DataSha256 = data is null ? "" : Convert.ToHexString(SHA256.HashData(data))
    };

    [Theory]
    [InlineData(24, 6000)]
    [InlineData(29.97, 4805)]
    public async Task Prepare_ReadsOrderedPcmChunksOnAbsoluteSampleGrid_AndClosesBeforeReturning(double fps, int expectedSamples)
    {
        var scene = Scene(fps);
        var client = new Client();
        var offset = 0;
        client.Read = (opened, _, _) =>
        {
            if (offset == expectedSamples) return Task.FromResult(Packet(opened, offset: offset, complete: true));
            var length = Math.Min(4096, expectedSamples - offset);
            var samples = Enumerable.Range(0, length * 2).Select(i => i % 2 == 0 ? .125f : -.25f).ToArray();
            var reply = Packet(opened, MemoryMarshal.AsBytes(samples.AsSpan()).ToArray(), offset: offset);
            offset += length;
            return Task.FromResult(reply);
        };
        await using var media = new BackendPreviewMedia(client);

        var audio = await media.PrepareAsync(scene, CancellationToken.None);

        Assert.Equal(expectedSamples, audio.SampleFrames);
        Assert.Equal(expectedSamples * 2, audio.Samples.Length);
        Assert.All(Enumerable.Range(0, expectedSamples), i =>
        {
            Assert.Equal(.125f, audio.Samples[i * 2]);
            Assert.Equal(-.25f, audio.Samples[i * 2 + 1]);
        });
        Assert.All(audio.Peaks, peak => Assert.Equal(.25f, peak));
        Assert.Equal(7, audio.TimelineStart);
        Assert.Equal(3, audio.Frames);
        Assert.Equal(fps, audio.Fps);
        Assert.Equal(1, client.CloseCalls);
        Assert.Equal(1, client.OpenCalls);
        Assert.Equal("audio", client.LastOpened!.Kind);
        Assert.Equal(0, client.LastOpened.StartFrame);
    }

    [Theory]
    [InlineData("offset")]
    [InlineData("nan")]
    [InlineData("infinity")]
    [InlineData("overflow")]
    [InlineData("early-end")]
    public async Task Prepare_RejectsCorruptOrTruncatedPcm_AndReleasesSession(string fault)
    {
        var client = new Client();
        client.Read = (opened, _, _) =>
        {
            if (fault == "early-end") return Task.FromResult(Packet(opened, offset: opened.TotalSamples, complete: true));
            var values = new[] { fault == "nan" ? float.NaN : fault == "infinity" ? float.PositiveInfinity : fault == "overflow" ? 1.01f : .5f, .25f };
            return Task.FromResult(Packet(opened, MemoryMarshal.AsBytes(values.AsSpan()).ToArray(), offset: fault == "offset" ? 1 : 0));
        };
        await using var media = new BackendPreviewMedia(client);

        await Assert.ThrowsAsync<InvalidDataException>(() => media.PrepareAsync(Scene(), CancellationToken.None));

        Assert.Equal(1, client.CloseCalls);
        Assert.Equal(1, client.ReadCalls);
    }

    [Fact]
    public async Task Prepare_CancellationClosesWithIndependentToken_AndNeverReplaysOpen()
    {
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var client = new Client();
        client.Read = async (_, _, token) =>
        {
            entered.SetResult();
            await Task.Delay(Timeout.InfiniteTimeSpan, token);
            throw new InvalidOperationException();
        };
        await using var media = new BackendPreviewMedia(client);
        using var cancellation = new CancellationTokenSource();
        var pending = media.PrepareAsync(Scene(), cancellation.Token);
        await entered.Task.WaitAsync(TimeSpan.FromSeconds(3));

        cancellation.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => pending);

        Assert.Equal(1, client.OpenCalls);
        Assert.Equal(1, client.ReadCalls);
        Assert.Equal(1, client.CloseCalls);
        Assert.False(client.CloseTokenCancelled);
    }

    [Fact]
    public async Task Video_EmitsExactOrderedFramesAtRequestedSeek_ThenClosesAtEnd()
    {
        var client = new Client();
        client.Read = (opened, call, _) => Task.FromResult(call == 3 ? Packet(opened, complete: true) :
            Packet(opened, Enumerable.Repeat((byte)(call + 10), BackendMediaDecoder.FrameBytes).ToArray(), local: opened.StartFrame + call - 1, native: 20 + call));
        await using var media = new BackendPreviewMedia(client);
        using var video = media.Video(Scene(duration: 5), new(30, 3, 90), 3, CancellationToken.None);
        await video.Completion.WaitAsync(TimeSpan.FromSeconds(3));

        Assert.Null(video.Error);
        Assert.False(video.Complete);
        Assert.True(video.TryRead(out var first));
        Assert.Equal(3, first!.LocalFrame);
        Assert.Equal(21, first.NativeFrame);
        Assert.All(first.Pixels, value => Assert.Equal(11, value));
        Assert.True(video.TryRead(out var second));
        Assert.Equal(4, second!.LocalFrame);
        Assert.Equal(22, second.NativeFrame);
        Assert.All(second.Pixels, value => Assert.Equal(12, value));
        Assert.False(video.TryRead(out _));
        Assert.True(video.Complete);
        Assert.Equal(1, client.CloseCalls);
        Assert.Equal(1, client.OpenCalls);
        Assert.Equal(3, client.LastOpened!.StartFrame);
    }

    [Theory]
    [InlineData("missing")]
    [InlineData("early-end")]
    [InlineData("reversed-source")]
    public async Task Video_ReportsOrderingOrTruncationError_AndClosesSession(string fault)
    {
        var client = new Client();
        client.Read = (opened, call, _) => Task.FromResult(fault == "early-end" ? Packet(opened, complete: true) :
            Packet(opened, new byte[BackendMediaDecoder.FrameBytes], local: fault == "missing" ? 1 : call - 1, native: call == 1 ? 20 : 19));
        await using var media = new BackendPreviewMedia(client);
        using var video = media.Video(Scene(), new(30, 3, 90), 0, CancellationToken.None);
        await video.Completion.WaitAsync(TimeSpan.FromSeconds(3));

        Assert.IsType<InvalidDataException>(video.Error);
        Assert.Equal(1, client.CloseCalls);
        Assert.Equal(1, client.OpenCalls);
        Assert.Equal(fault == "reversed-source" ? 2 : 1, client.ReadCalls);
    }

    [Fact]
    public async Task Video_BoundsQueueAndDisposalDrainsClose_BeforeAdmittingNextPlayback()
    {
        var fourthRead = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var closing = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var releaseClose = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var client = new Client();
        client.Read = (opened, call, _) =>
        {
            if (call == 4) fourthRead.TrySetResult();
            return Task.FromResult(Packet(opened, new byte[BackendMediaDecoder.FrameBytes], local: call - 1, native: call));
        };
        client.Close = async (_, token) =>
        {
            Assert.False(token.IsCancellationRequested);
            closing.TrySetResult();
            await releaseClose.Task.WaitAsync(TimeSpan.FromSeconds(3));
        };
        await using var media = new BackendPreviewMedia(client);
        using var first = media.Video(Scene(duration: 24), new(30, 3, 90), 0, CancellationToken.None);
        try
        {
            await fourthRead.Task.WaitAsync(TimeSpan.FromSeconds(3));
            Assert.Equal(4, client.ReadCalls); // Three queued packets plus one producer waiting for capacity.
            first.Dispose();
            await closing.Task.WaitAsync(TimeSpan.FromSeconds(3));
            Assert.False(first.Completion.IsCompleted);
            using var secondToken = new CancellationTokenSource();
            var second = media.PrepareAsync(Scene(), secondToken.Token);
            Assert.False(second.IsCompleted); // Prepare reaches the lease wait synchronously.
            Assert.Equal(1, client.OpenCalls);
            secondToken.Cancel();
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => second.WaitAsync(TimeSpan.FromSeconds(3)));
            Assert.Equal(1, client.OpenCalls);
            releaseClose.SetResult();
            await first.Completion.WaitAsync(TimeSpan.FromSeconds(3));
            Assert.Equal(1, client.CloseCalls);
        }
        finally { releaseClose.TrySetResult(); }
    }

    [Fact]
    public async Task Video_DisposeInterruptsKnownRemoteSession_BeforePendingReadCanReturn()
    {
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var closed = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var client = new Client();
        client.Read = async (_, _, token) =>
        {
            entered.SetResult();
            // A remote operation may still be active after cancellation of its local waiter.
            await closed.Task.WaitAsync(TimeSpan.FromSeconds(3));
            token.ThrowIfCancellationRequested();
            throw new InvalidOperationException("The read must have been cancelled.");
        };
        client.Close = (_, token) =>
        {
            Assert.False(token.IsCancellationRequested);
            closed.TrySetResult();
            return Task.CompletedTask;
        };
        await using var media = new BackendPreviewMedia(client);
        using var video = media.Video(Scene(), new(30, 3, 90), 0, CancellationToken.None);
        await entered.Task.WaitAsync(TimeSpan.FromSeconds(3));

        video.Dispose();
        await video.Completion.WaitAsync(TimeSpan.FromSeconds(3));

        Assert.True(closed.Task.IsCompletedSuccessfully);
        Assert.Null(video.Error);
        Assert.Equal(1, client.ReadCalls);
        Assert.Equal(1, client.CloseCalls);
        Assert.Equal(1, client.OpenCalls);
    }

    [Fact]
    public async Task AdapterDisposalCancelsActiveRead_AndWaitsForSessionCleanup()
    {
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var client = new Client();
        client.Read = async (_, _, token) =>
        {
            entered.SetResult();
            await Task.Delay(Timeout.InfiniteTimeSpan, token);
            throw new InvalidOperationException();
        };
        var media = new BackendPreviewMedia(client);
        using var video = media.Video(Scene(), new(30, 3, 90), 0, CancellationToken.None);
        await entered.Task.WaitAsync(TimeSpan.FromSeconds(3));

        await media.DisposeAsync();

        Assert.True(video.Completion.IsCompletedSuccessfully);
        Assert.Null(video.Error);
        Assert.Equal(1, client.CloseCalls);
        Assert.Throws<ObjectDisposedException>(() => media.Video(Scene(), new(30, 3, 90), 0, CancellationToken.None));
        await Assert.ThrowsAsync<ObjectDisposedException>(() => media.PrepareAsync(Scene(), CancellationToken.None));
    }

    private sealed class Client : IBackendPlaybackClient
    {
        internal int OpenCalls, ReadCalls, CloseCalls;
        internal bool CloseTokenCancelled;
        internal BackendPlaybackReply? LastOpened;
        internal Func<BackendPlaybackReply, int, CancellationToken, Task<BackendPlaybackReply>> Read = (_, _, _) => throw new NotSupportedException();
        internal Func<string, CancellationToken, Task>? Close;
        public Task<BackendPlaybackReply> OpenPlaybackAsync(SceneSnapshot scene, string kind, int localFrame, CancellationToken token)
        {
            token.ThrowIfCancellationRequested();
            Interlocked.Increment(ref OpenCalls);
            LastOpened = Opened(scene, kind, localFrame);
            return Task.FromResult(LastOpened);
        }
        public Task<BackendPlaybackReply> ReadPlaybackAsync(BackendPlaybackReply opened, CancellationToken token) => Read(opened, Interlocked.Increment(ref ReadCalls), token);
        public async Task ClosePlaybackAsync(string sessionId, CancellationToken token)
        {
            Interlocked.Increment(ref CloseCalls);
            CloseTokenCancelled = token.IsCancellationRequested;
            Assert.Equal(LastOpened!.SessionId, sessionId);
            if (Close is not null) await Close(sessionId, token);
        }
    }
}
