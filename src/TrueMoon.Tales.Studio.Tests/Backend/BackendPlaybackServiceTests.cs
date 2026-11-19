using System.Diagnostics;
using System.Text.Json.Nodes;
using TrueMoon.Mithril;
using TrueMoon.Tales.Studio.Backend;
using TrueMoon.Tales.Studio.Persistence;
using Xunit;

namespace TrueMoon.Tales.Studio.Tests.Backend;

public sealed class BackendPlaybackServiceTests
{
    [PythonWriterFact]
    public async Task ContinuousSession_BoundsAdmissionAndPreservesBytes_ExplicitCloseReapsOneOwnedDecoder()
    {
        using var fixture = new ControlledPlayback();
        var baseline = await fixture.MigrateAsync();
        using var service = fixture.Service();
        var opened = await fixture.OpenAsync(service, baseline);
        Assert.Empty(opened.ErrorCode); Assert.Equal(16, StudioBackendService.ProtocolVersion);
        Assert.Equal(48000, opened.TotalSamples); Assert.Equal("video", opened.Kind); Assert.Empty(opened.Data);
        var first = await service.ReadPlaybackAsync(fixture.Key, opened.SessionId);
        Assert.Empty(first.ErrorCode); Assert.Equal(0, first.LocalFrame); Assert.Equal(0, first.NativeFrame);
        Assert.Equal(BackendMediaDecoder.FrameBytes, first.Data.Length); Assert.All(first.Data, value => Assert.Equal(7, value));
        Assert.Equal("media_busy", (await fixture.OpenAsync(service, baseline)).ErrorCode);
        Assert.Equal("playback_not_found", (await service.ReadPlaybackAsync(fixture.Key, Guid.NewGuid().ToString("N"))).ErrorCode);
        var second = await service.ReadPlaybackAsync(fixture.Key, opened.SessionId);
        Assert.Equal(1, second.LocalFrame); Assert.Equal(1, second.NativeFrame);
        Assert.False(fixture.VideoExited); Assert.Equal(1, fixture.VideoStarts);
        Assert.True((await service.ClosePlaybackAsync(fixture.Key, opened.SessionId)).Completed);
        Assert.True(fixture.VideoExited); Assert.True((await service.ClosePlaybackAsync(fixture.Key, opened.SessionId)).Completed);
        fixture.AssertUnchanged();
    }

    [PythonWriterFact]
    public async Task CloseInterruptsBlockedRead_AndDisposalReapsItsOnlyChild()
    {
        using var fixture = new ControlledPlayback("stall");
        var baseline = await fixture.MigrateAsync();
        using var service = fixture.Service();
        var opened = await fixture.OpenAsync(service, baseline);
        await fixture.Started.Task.WaitAsync(TimeSpan.FromSeconds(5));
        var read = service.ReadPlaybackAsync(fixture.Key, opened.SessionId);
        Assert.False(read.IsCompleted);
        var closed = await service.ClosePlaybackAsync(fixture.Key, opened.SessionId).WaitAsync(TimeSpan.FromSeconds(5));
        Assert.True(closed.Completed); Assert.True(fixture.VideoExited);
        Assert.Equal("media_timeout", (await read.WaitAsync(TimeSpan.FromSeconds(5))).ErrorCode);
        var again = await fixture.OpenAsync(service, baseline);
        Assert.Empty(again.ErrorCode);
        service.Dispose();
        Assert.True(fixture.VideoExited); fixture.AssertUnchanged();
    }

    [PythonWriterFact]
    public async Task CancelledReadClosesResource_WithoutReplayOrWrite()
    {
        using var fixture = new ControlledPlayback("stall");
        var baseline = await fixture.MigrateAsync();
        using var service = fixture.Service();
        var opened = await fixture.OpenAsync(service, baseline);
        await fixture.Started.Task.WaitAsync(TimeSpan.FromSeconds(5));
        using var cancelled = new CancellationTokenSource();
        var read = service.ReadPlaybackAsync(fixture.Key, opened.SessionId, cancelled.Token);
        cancelled.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => read);
        Assert.True(fixture.VideoExited);
        Assert.Equal("playback_not_found", (await service.ReadPlaybackAsync(fixture.Key, opened.SessionId)).ErrorCode);
        fixture.AssertUnchanged();
    }

    [PythonWriterFact]
    public async Task StaleExactBytesRejectNextPayloadAndReapDecoder_WithoutAddingHistory()
    {
        using var fixture = new ControlledPlayback();
        var baseline = await fixture.MigrateAsync();
        using var service = fixture.Service();
        var opened = await fixture.OpenAsync(service, baseline);
        await fixture.Started.Task.WaitAsync(TimeSpan.FromSeconds(5));
        File.AppendAllText(fixture.Files.ProjectPath, " ");
        var changed = File.ReadAllBytes(fixture.Files.ProjectPath);
        var reply = await service.ReadPlaybackAsync(fixture.Key, opened.SessionId);
        Assert.Equal("revision_conflict", reply.ErrorCode); Assert.Empty(reply.Data); Assert.True(fixture.VideoExited);
        Assert.Equal(changed, File.ReadAllBytes(fixture.Files.ProjectPath)); Assert.Single(fixture.Files.HistoryFiles);
    }

    [PythonWriterFact]
    public async Task TruncatedDecoderIsAnError_AndCannotClaimSuccessfulCompletion()
    {
        using var fixture = new ControlledPlayback("truncated");
        var baseline = await fixture.MigrateAsync();
        using var service = fixture.Service();
        var opened = await fixture.OpenAsync(service, baseline);
        var reply = await service.ReadPlaybackAsync(fixture.Key, opened.SessionId);
        Assert.Equal("media_io_error", reply.ErrorCode); Assert.False(reply.Completed); Assert.Empty(reply.Data);
        Assert.True(fixture.VideoExited); fixture.AssertUnchanged();
    }

    [PythonWriterFact]
    public async Task IdleAbandonmentReapsDecoder_AndAllowsFreshSession()
    {
        using var fixture = new ControlledPlayback("stall");
        var baseline = await fixture.MigrateAsync();
        using var service = fixture.Service();
        var opened = await fixture.OpenAsync(service, baseline);
        await fixture.Started.Task.WaitAsync(TimeSpan.FromSeconds(5));
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(25));
        while (!fixture.VideoExited) await Task.Delay(25, deadline.Token);
        Assert.Equal("media_timeout", (await service.ReadPlaybackAsync(fixture.Key, opened.SessionId)).ErrorCode);
        Assert.Empty((await fixture.OpenAsync(service, baseline)).ErrorCode);
        service.Dispose(); Assert.True(fixture.VideoExited); fixture.AssertUnchanged();
    }

    [PythonWriterFact]
    public async Task InvalidBaselineKindAndPathNeverLaunchVideo_AndPreserveWriterAvailability()
    {
        using var fixture = new ControlledPlayback();
        var baseline = await fixture.MigrateAsync();
        using var service = fixture.Service();
        Assert.Equal("revision_conflict", (await service.OpenPlaybackAsync(fixture.Key, "clip-main", "video", 0, 13, baseline.Fingerprint)).ErrorCode);
        Assert.Equal("invalid_request", (await service.OpenPlaybackAsync(fixture.Key, "clip-main", "network", 0, 12, baseline.Fingerprint)).ErrorCode);
        Assert.Equal("invalid_request", (await service.OpenPlaybackAsync(fixture.Key, "clip-main", "audio", 1, 12, baseline.Fingerprint)).ErrorCode);
        var document = JsonNode.Parse(File.ReadAllText(fixture.Files.ProjectPath))!;
        document["project"]!["properties"]!["assets"]!["video-a"]!["path"] = "http://example.invalid/movie.mp4";
        File.WriteAllText(fixture.Files.ProjectPath, document.ToJsonString());
        var changed = await fixture.Files.Store().OpenAsync();
        Assert.Equal("access_denied", (await fixture.OpenAsync(service, changed)).ErrorCode);
        Assert.Equal(0, fixture.VideoStarts); Assert.Single(fixture.Files.HistoryFiles);
    }

    private sealed class ControlledPlayback : IDisposable
    {
        private readonly BackendWriteFixture _fixture = new();
        private readonly string _mode;
        private byte[]? _baseline;
        private readonly List<(int Id, DateTime Started)> _children = [];
        internal TrueMoon.Tales.Studio.Tests.Persistence.PersistenceFixture Files => _fixture.Files;
        internal string Key => _fixture.Options.SessionKey;
        internal int VideoStarts { get; private set; }
        internal TaskCompletionSource<bool> Started { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        internal bool VideoExited => _children.All(child => !Alive(child));

        internal ControlledPlayback(string mode = "frames")
        {
            _mode = mode;
            var config = JsonNode.Parse(File.ReadAllText(_fixture.Base.ConfigPath))!;
            config["projects_root"] = Files.ProjectDirectory;
            File.WriteAllText(_fixture.Base.ConfigPath, config.ToJsonString());
            var path = Path.Combine(Files.ProjectDirectory, "source.mp4"); File.WriteAllBytes(path, [1]);
            var project = JsonNode.Parse(File.ReadAllText(Files.ProjectPath))!;
            project["assets"]!["video-a"]!["path"] = path;
            File.WriteAllText(Files.ProjectPath, project.ToJsonString());
        }

        internal async Task<ProjectFileDocument> MigrateAsync()
        {
            var store = Files.Store();
            await store.MigrateAsync(await store.OpenAsync(), 11, "fixture");
            _baseline = File.ReadAllBytes(Files.ProjectPath);
            return await store.OpenAsync();
        }

        internal StudioBackendService Service()
        {
            var decoder = new BackendMediaDecoder("controlled-video", "controlled-probe", start =>
            {
                var probe = start.FileName == "controlled-probe";
                var processStart = new ProcessStartInfo(Environment.GetEnvironmentVariable("STUDIO_TEST_PYTHON")!)
                { UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true, RedirectStandardError = true };
                var code = probe ? "print('{\"streams\":[{\"codec_type\":\"video\",\"width\":320,\"height\":240,\"avg_frame_rate\":\"30/1\",\"r_frame_rate\":\"30/1\",\"nb_frames\":\"900\"}]}')" :
                    _mode == "stall" ? "import time;time.sleep(60)" : _mode == "truncated" ? "import sys;sys.stdout.buffer.write(b'x')" :
                    "import sys,time\nb=bytes([7])*518400\nfor i in range(1000):\n sys.stdout.buffer.write(b);sys.stdout.buffer.flush()\ntime.sleep(60)";
                foreach (var arg in new[] { "-s", "-B", "-c", code }) processStart.ArgumentList.Add(arg);
                var process = Process.Start(processStart)!;
                if (!probe) { VideoStarts++; _children.Add((process.Id, process.StartTime)); Started.TrySetResult(true); }
                return process;
            });
            return new(_fixture.Options with { WriteEnabled = false }, Files.Store(writable: false), new UnitLaunchSettings(1, 12345, "test"),
                new BackendMediaReader(new BackendMediaResolverFactory(_fixture.Options), decoder));
        }
        internal Task<BackendPlaybackReply> OpenAsync(StudioBackendService service, ProjectFileDocument baseline) =>
            service.OpenPlaybackAsync(Key, "clip-main", "video", 0, baseline.Project.Revision, baseline.Fingerprint);
        internal void AssertUnchanged()
        {
            Assert.Equal(_baseline, File.ReadAllBytes(Files.ProjectPath)); Assert.Single(Files.HistoryFiles); Assert.False(File.Exists(Files.JournalPath));
        }
        private static bool Alive((int Id, DateTime Started) child)
        {
            try { using var process = Process.GetProcessById(child.Id); return !process.HasExited && process.StartTime == child.Started; }
            catch (ArgumentException) { return false; }
        }
        public void Dispose()
        {
            foreach (var child in _children)
                if (Alive(child)) { using var process = Process.GetProcessById(child.Id); if (process.StartTime == child.Started) { process.Kill(entireProcessTree: true); process.WaitForExit(5000); } }
            _fixture.Dispose();
        }
    }
}
