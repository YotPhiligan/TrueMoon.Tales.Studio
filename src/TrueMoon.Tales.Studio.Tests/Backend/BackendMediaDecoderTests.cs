using System.Diagnostics;
using System.Text;
using System.Text.Json;
using TrueMoon.Tales.Studio.Backend;
using Xunit;

namespace TrueMoon.Tales.Studio.Tests.Backend;

public sealed class BackendMediaDecoderTests
{
    [Fact]
    public void ProbeParser_PreservesFractionalRateAndUsesDeclaredVideoExtent()
    {
        var info = BackendMediaDecoder.Parse(Json(rate: "24000/1001", frames: "123"));
        Assert.Equal(24000d / 1001, info.Fps, 10);
        Assert.Equal(123, info.FrameCount);
        Assert.Equal(1280, info.Width);
        Assert.Equal(720, info.Height);
    }

    [Fact]
    public void ProbeParser_UsesStreamDurationBeforeContainerDuration_AndRoundsPartialFrameUp()
    {
        var info = BackendMediaDecoder.Parse(Json(frames: "N/A", duration: "1.01", containerDuration: "600"));
        Assert.Equal(25, info.FrameCount);
        Assert.Equal(24, info.Fps);
        Assert.Equal(24, BackendMediaDecoder.Parse(Json(frames: "N/A", duration: "N/A", containerDuration: "1")).FrameCount);
    }

    [Theory]
    [InlineData("0/1", "24/1")]
    [InlineData("24/0", "24/1")]
    [InlineData("NaN/1", "24/1")]
    [InlineData("Infinity/1", "24/1")]
    [InlineData("-24/1", "24/1")]
    [InlineData("241/1", "241/1")]
    [InlineData("24", "24/1")]
    [InlineData("24/1", "25/1")]
    [InlineData("24/1", "0/0")]
    [InlineData("1/9223372036854775808", "24/1")]
    public void ProbeParser_RejectsInvalidRatesAndVariableFrameRate(string average, string nominal) =>
        Assert.Throws<InvalidDataException>(() => BackendMediaDecoder.Parse(Json(rate: average, nominal: nominal)));

    [Theory]
    [InlineData("0", 1280, 720)]
    [InlineData("-1", 1280, 720)]
    [InlineData("1.5", 1280, 720)]
    [InlineData("10000001", 1280, 720)]
    [InlineData("2147483648", 1280, 720)]
    [InlineData("1", 0, 720)]
    [InlineData("1", 1280, -1)]
    [InlineData("1", 8193, 1)]
    [InlineData("1", 8192, 2049)]
    public void ProbeParser_RejectsInvalidFrameCountsAndImageExtents(string frames, int width, int height) =>
        Assert.Throws<InvalidDataException>(() => BackendMediaDecoder.Parse(Json(frames: frames, width: width, height: height)));

    [Fact]
    public void ProbeParser_AcceptsFirstAndMaximumSupportedExtents()
    {
        Assert.Equal(new BackendVideoInfo(1, 1, 1, 1), BackendMediaDecoder.Parse(Json(rate: "1/1", frames: "1", width: 1, height: 1)));
        Assert.Equal(new BackendVideoInfo(240, BackendMediaDecoder.MaxFrames, 8192, 2048),
            BackendMediaDecoder.Parse(Json(rate: "240/1", frames: BackendMediaDecoder.MaxFrames.ToString(), width: 8192, height: 2048)));
    }

    [Theory]
    [InlineData("0")]
    [InlineData("-1")]
    [InlineData("NaN")]
    [InlineData("Infinity")]
    [InlineData("86400.01")]
    [InlineData("0.0000000000001")]
    public void ProbeParser_RejectsInvalidDurationFallback(string duration) =>
        Assert.Throws<InvalidDataException>(() => BackendMediaDecoder.Parse(Json(frames: "N/A", duration: duration)));

    [Theory]
    [InlineData("{")]
    [InlineData("{}")]
    [InlineData("{\"streams\":[]}")]
    [InlineData("{\"streams\":[{},{}]}")]
    [InlineData("{\"streams\":[{\"codec_type\":\"audio\"}]}")]
    public void ProbeParser_RejectsMissingVideoAndMalformedResponses(string json) =>
        Assert.Throws<InvalidDataException>(() => BackendMediaDecoder.Parse(Encoding.UTF8.GetBytes(json)));

    [Fact]
    public void ProbeParser_RejectsOversizedJsonAndUnboundedDurationDerivedFrameCount()
    {
        Assert.Throws<InvalidDataException>(() => BackendMediaDecoder.Parse(new byte[BackendMediaDecoder.MaxProbeBytes + 1]));
        Assert.Throws<InvalidDataException>(() => BackendMediaDecoder.Parse(Json(rate: "240/1", frames: "N/A", duration: "86400")));
    }

    [Theory]
    [InlineData(-1)]
    [InlineData(24)]
    public async Task Read_RejectsOutOfRangeFrameBeforeStartingAProcess(int frame)
    {
        var starts = 0;
        var decoder = new BackendMediaDecoder("not-used", "not-used", _ => { starts++; throw new InvalidOperationException("Should not start"); });
        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(() => decoder.ReadAsync(Local("video.mp4"), frame, new(24, 24, 1280, 720), default));
        Assert.Equal(0, starts);
    }

    [Fact]
    public async Task Read_RejectsInvalidMetadataBeforeStartingAProcess()
    {
        var starts = 0;
        var decoder = new BackendMediaDecoder("not-used", "not-used", _ => { starts++; throw new InvalidOperationException("Should not start"); });
        foreach (var info in new[] { new BackendVideoInfo(double.NaN, 24, 1280, 720), new(241, 24, 1280, 720),
            new(24, 0, 1280, 720), new(24, 24, 8192, 8192) })
            await Assert.ThrowsAsync<InvalidDataException>(() => decoder.ReadAsync(Local("video.mp4"), 0, info, default));
        Assert.Equal(0, starts);
    }

    [Fact]
    public async Task Operations_RejectRemoteRelativeAndPlaylistInputsBeforeStartingAProcess()
    {
        var starts = 0;
        var decoder = new BackendMediaDecoder("not-used", "not-used", _ => { starts++; throw new InvalidOperationException("Should not start"); });
        foreach (var input in new[] { "relative.mp4", "http://127.0.0.1/video.mp4", "\\\\server\\share\\video.mp4" })
            await Assert.ThrowsAsync<UnauthorizedAccessException>(() => decoder.ProbeAsync(input, default));
        foreach (var input in new[] { "video.m3u8", "video.ffconcat", "video.mpd", "video.txt" })
            await Assert.ThrowsAsync<InvalidDataException>(() => decoder.ProbeAsync(Local(input), default));
        Assert.Equal(0, starts);
    }

    [Fact]
    public async Task Read_PreCanceled_DoesNotStartAChild()
    {
        var starts = 0;
        var decoder = new BackendMediaDecoder("not-used", "not-used", _ => { starts++; throw new InvalidOperationException("Should not start"); });
        using var canceled = new CancellationTokenSource(); canceled.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => decoder.ReadAsync(Local("video.mp4"), 0, new(24, 24, 1280, 720), canceled.Token));
        Assert.Equal(0, starts);
    }

    [PythonWriterFact]
    public async Task Probe_PassesForcedLocalContainer_DisablesExternalReferences_AndReapsChild()
    {
        await using var fixture = new DecoderProcess("probe");
        foreach (var (name, format) in new[] { ("Кадр с пробелом.mp4", "mov"), ("video.mkv", "matroska"), ("video.avi", "avi") })
        {
            var actual = await fixture.Decoder.ProbeAsync(Local(name), default);
            Assert.Equal(new BackendVideoInfo(24, 24, 1280, 720), actual);
            var args = fixture.Invocations[^1].ArgumentList.ToArray();
            AssertPair(args, "-protocol_whitelist", "file,pipe");
            AssertPair(args, "-f", format);
            AssertPair(args, "-i", Local(name));
            AssertPair(args, "-select_streams", "v:0");
            if (format == "mov") { AssertPair(args, "-enable_drefs", "0"); AssertPair(args, "-use_absolute_path", "0"); }
            else Assert.DoesNotContain("-enable_drefs", args);
        }
        Assert.All(fixture.Children, child => Assert.True(child.HasExited));
    }

    [PythonWriterFact]
    public async Task Read_ReadsExactlyOneFrame_UsesExplicitCpuVideoArguments_AndReapsChild()
    {
        await using var fixture = new DecoderProcess("frame");
        var expected = Enumerable.Range(0, BackendMediaDecoder.FrameBytes).Select(index => (byte)index).ToArray();
        foreach (var frame in new[] { 0, 23 })
        {
            var pixels = await fixture.Decoder.ReadAsync(Local("video.mp4"), frame, new(24, 24, 1280, 720), default);
            Assert.Equal(expected, pixels);
        }
        Assert.All(fixture.Children, child => Assert.True(child.HasExited));
        var args = fixture.Invocations[1].ArgumentList.ToArray();
        AssertPair(args, "-map", "0:v:0");
        AssertPair(args, "-frames:v", "1");
        AssertPair(args, "-ss", "0.947916667");
        AssertPair(args, "-pix_fmt", "bgra");
        AssertPair(args, "-protocol_whitelist", "file,pipe");
        Assert.Contains("-an", args); Assert.Contains("-sn", args); Assert.Contains("-dn", args);
        Assert.Equal("pipe:1", args[^1]);
        Assert.All(fixture.Invocations, invocation => { Assert.False(invocation.UseShellExecute); Assert.True(invocation.CreateNoWindow); });
    }

    [PythonWriterFact]
    public async Task Read_RejectsEofExitCodeAndOutputOverflow_AndReapsEveryChild()
    {
        foreach (var (mode, message) in new[] { ("eof", "incomplete"), ("failed", "code 7"), ("output-overflow", "output limit"), ("error-overflow", "output limit") })
        {
            await using var fixture = new DecoderProcess(mode);
            var error = await Assert.ThrowsAsync<InvalidDataException>(() => fixture.Decoder.ReadAsync(Local("video.mp4"), 0, new(24, 24, 1280, 720), default).WaitAsync(TimeSpan.FromSeconds(5)));
            Assert.Contains(message, error.Message);
            Assert.True(fixture.Children.Single().HasExited);
        }
    }

    [PythonWriterFact]
    public async Task Read_Cancellation_ReapsOnlyItsOwnedChild_AndPreservesOtherProcess()
    {
        await using var fixture = new DecoderProcess("hang");
        using var cancellation = new CancellationTokenSource();
        var other = fixture.StartOther();
        var pending = fixture.Decoder.ReadAsync(Local("video.mp4"), 0, new(24, 24, 1280, 720), cancellation.Token);
        await fixture.Started.Task.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.False(fixture.Children.Single().HasExited);
        cancellation.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => pending.WaitAsync(TimeSpan.FromSeconds(5)));
        Assert.True(fixture.Children.Single().HasExited);
        Assert.False(other.HasExited);
    }

    private static byte[] Json(string rate = "24/1", string? nominal = null, string frames = "24", string duration = "1",
        string containerDuration = "1", int width = 1280, int height = 720) =>
        JsonSerializer.SerializeToUtf8Bytes(new { streams = new[] { new { codec_type = "video", width, height,
            avg_frame_rate = rate, r_frame_rate = nominal ?? rate, nb_frames = frames, duration } }, format = new { duration = containerDuration } });

    private static string Local(string name) => Path.Combine(Path.GetTempPath(), "studio-media-decoder-test", name);
    private static void AssertPair(string[] args, string name, string value)
    {
        var index = Array.IndexOf(args, name); Assert.True(index >= 0, name); Assert.Equal(value, args[index + 1]);
    }

    private sealed class DecoderProcess : IAsyncDisposable
    {
        private readonly string _root = Path.Combine(Path.GetTempPath(), "studio-decoder-" + Guid.NewGuid().ToString("N"));
        private readonly string _python = Environment.GetEnvironmentVariable("STUDIO_TEST_PYTHON")!;
        private readonly List<Process> _others = [];
        internal List<Process> Children { get; } = [];
        internal List<ProcessStartInfo> Invocations { get; } = [];
        internal TaskCompletionSource<bool> Started { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        internal BackendMediaDecoder Decoder { get; }

        internal DecoderProcess(string mode)
        {
            Directory.CreateDirectory(_root);
            File.WriteAllText(Path.Combine(_root, "child.py"), """
                import json,sys,time
                mode=sys.argv[1]
                if mode=='probe':
                    print(json.dumps({'streams':[{'codec_type':'video','width':1280,'height':720,'avg_frame_rate':'24/1','r_frame_rate':'24/1','nb_frames':'24'}]}))
                elif mode=='frame':
                    sys.stdout.buffer.write(bytes(range(256))*2025)
                elif mode=='eof':
                    sys.stdout.buffer.write(b'incomplete')
                elif mode=='failed':
                    sys.stderr.buffer.write(b'bounded diagnostic');sys.exit(7)
                elif mode=='output-overflow':
                    sys.stdout.buffer.write(bytes(518401));sys.stdout.buffer.flush();time.sleep(60)
                elif mode=='error-overflow':
                    sys.stderr.buffer.write(bytes(16385));sys.stderr.buffer.flush();time.sleep(60)
                else:
                    time.sleep(60)
                """);
            Decoder = new("ffmpeg-not-launched", "ffprobe-not-launched", invocation =>
            {
                Invocations.Add(invocation);
                var process = Start(mode);
                var observed = Process.GetProcessById(process.Id); _ = observed.Handle;
                Children.Add(observed); Started.TrySetResult(true);
                return process;
            });
        }

        private Process Start(string mode)
        {
            var info = new ProcessStartInfo(_python) { UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true, RedirectStandardError = true };
            foreach (var argument in new[] { "-s", "-B", Path.Combine(_root, "child.py"), mode }) info.ArgumentList.Add(argument);
            return Process.Start(info)!;
        }

        internal Process StartOther()
        {
            var process = Start("hang"); _ = process.Handle; _others.Add(process); return process;
        }

        public async ValueTask DisposeAsync()
        {
            try
            {
                foreach (var process in Children.Concat(_others))
                {
                    try
                    {
                        if (!process.HasExited) process.Kill(entireProcessTree: true);
                        await process.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(5));
                    }
                    finally { process.Dispose(); }
                }
            }
            finally { Directory.Delete(_root, recursive: true); }
        }
    }
}
