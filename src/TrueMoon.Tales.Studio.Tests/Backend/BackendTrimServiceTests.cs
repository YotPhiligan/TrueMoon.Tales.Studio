using System.Diagnostics;
using System.Text.Json;
using System.Text.Json.Nodes;
using TrueMoon.Mithril;
using TrueMoon.Tales.Studio.Backend;
using TrueMoon.Tales.Studio.Contracts.Serialization;
using TrueMoon.Tales.Studio.Persistence;
using TrueMoon.Tales.Studio.Tests.Persistence;
using Xunit;

namespace TrueMoon.Tales.Studio.Tests.Backend;

public sealed class BackendTrimServiceTests
{
    [PythonWriterFact]
    public async Task Trim_UsesOneCommitExactHistoryAndOpaquePreservation_ThenNoopAvoidsProbeAndWrite()
    {
        using var fixture = new ProbeFixture(); using var service = fixture.Service();
        var migrated = await fixture.Files.MigrateAsync(service);
        File.WriteAllBytes(fixture.Files.Files.ProjectPath, PersistenceFixture.Annotate(File.ReadAllBytes(fixture.Files.Files.ProjectPath)));
        var baseline = await service.OpenProjectAsync(fixture.Key);
        var original = File.ReadAllBytes(fixture.Files.Files.ProjectPath);
        var trimmed = await service.TrimClipAsync(fixture.Key, "clip-main", 4, 20, 12, 12, baseline.Fingerprint);
        Assert.Empty(trimmed.ErrorCode); Assert.Equal(1, fixture.Probes);
        var before = ProjectContractJson.Deserialize(baseline.ProjectJson);
        var after = ProjectContractJson.Deserialize(trimmed.ProjectJson);
        var expected = before with { Revision = 13, Montage = before.Montage with { Clips = before.Montage.Clips.Select(row => row.Id == "clip-main"
            ? row with { SourceInFrame = 4, SourceOutFrame = 20, DurationFrames = 16 } : row).ToArray() } };
        Assert.Equal(ProjectContractJson.Serialize(expected), ProjectContractJson.Serialize(after));
        Assert.Equal(2, fixture.Files.Files.HistoryFiles.Length);
        Assert.Contains(fixture.Files.Files.HistoryFiles, path => File.ReadAllBytes(path).SequenceEqual(original));
        using (var document = JsonDocument.Parse(File.ReadAllBytes(fixture.Files.Files.ProjectPath)))
        {
            Assert.Equal("envelope", document.RootElement.GetProperty("opaque_envelope").GetString());
            var project = document.RootElement.GetProperty("project");
            Assert.Equal("project", project.GetProperty("opaque_project").GetString());
            Assert.Equal("clip-main", PersistenceFixture.ById(project.GetProperty("montage").GetProperty("clips"), "clip-main").GetProperty("opaque_clip").GetString());
            Assert.Equal(9007199254740993L, document.RootElement.GetProperty("legacy_document").GetProperty("opaque_root").GetProperty("precise_integer").GetInt64());
        }
        Assert.True(fixture.AllExited);
        var saved = File.ReadAllBytes(fixture.Files.Files.ProjectPath);
        Assert.NotEqual(baseline.Fingerprint, trimmed.Fingerprint);
        Assert.Equal(Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(saved)), trimmed.Fingerprint);
        var noop = await service.TrimClipAsync(fixture.Key, "clip-main", 4, 20, 12, 13, trimmed.Fingerprint);
        Assert.Empty(noop.ErrorCode); Assert.Equal(trimmed.Fingerprint, noop.Fingerprint); Assert.Equal(1, fixture.Probes);
        Assert.Equal(saved, File.ReadAllBytes(fixture.Files.Files.ProjectPath)); Assert.Equal(2, fixture.Files.Files.HistoryFiles.Length);
        Assert.False(File.Exists(fixture.Files.Files.JournalPath));
    }

    [Fact]
    public async Task Trim_RequiresExistingSoleWriterAndExplicitMigration_WithoutDecoderForNoop()
    {
        using var fixture = new BackendWriteFixture(); using var service = fixture.Service();
        var baseline = await service.OpenProjectAsync(fixture.Options.SessionKey);
        Assert.Equal("writer_required", (await service.TrimClipAsync(fixture.Options.SessionKey, "clip-main", 0, 12, 12, 11, baseline.Fingerprint)).ErrorCode);
        Assert.Empty((await service.AcquireWriterAsync(fixture.Options.SessionKey, 11, baseline.Fingerprint)).ErrorCode);
        Assert.Equal("migration_required", (await service.TrimClipAsync(fixture.Options.SessionKey, "clip-main", 0, 12, 12, 11, baseline.Fingerprint)).ErrorCode);
        var migrated = await service.MigrateProjectAsync(fixture.Options.SessionKey, 11, baseline.Fingerprint);
        var saved = File.ReadAllBytes(fixture.Files.ProjectPath);
        Assert.Empty((await service.TrimClipAsync(fixture.Options.SessionKey, "clip-main", 0, 24, 12, 12, migrated.Fingerprint)).ErrorCode);
        Assert.Equal("decoder_unavailable", (await service.TrimClipAsync(fixture.Options.SessionKey, "clip-main", 0, 12, 12, 12, migrated.Fingerprint)).ErrorCode);
        Assert.Equal(saved, File.ReadAllBytes(fixture.Files.ProjectPath)); Assert.Single(fixture.Files.HistoryFiles);
        using var other = fixture.Service();
        Assert.Equal("writer_busy", (await other.AcquireWriterAsync(fixture.Options.SessionKey, 12, migrated.Fingerprint)).ErrorCode);
        await Assert.ThrowsAsync<UnauthorizedAccessException>(() => service.TrimClipAsync("wrong", "clip-main", 0, 12, 12, 12, migrated.Fingerprint));
    }

    [PythonTrimTheory]
    [InlineData(72, true)]
    [InlineData(72.001, false)]
    public async Task Trim_UsesAuthoritativeSourceFpsAndFrameCountForCapacity(double end, bool allowed)
    {
        using var fixture = new ProbeFixture(); using var service = fixture.Service();
        var baseline = await fixture.Files.MigrateAsync(service);
        var bytes = File.ReadAllBytes(fixture.Files.Files.ProjectPath);
        // Source90frames/30fps =>72projectframes@24fps, independent of caller metadata.
        var reply = await service.TrimClipAsync(fixture.Key, "clip-main", 0, end, 12, 12, baseline.Fingerprint);
        Assert.Equal(1, fixture.Probes); Assert.True(fixture.AllExited);
        if (allowed)
        { Assert.Empty(reply.ErrorCode); Assert.Equal(72, ProjectContractJson.Deserialize(reply.ProjectJson).Montage.Clips[0].DurationFrames); }
        else
        { Assert.Equal("invalid_trim", reply.ErrorCode); Assert.Equal(bytes, File.ReadAllBytes(fixture.Files.Files.ProjectPath)); Assert.Single(fixture.Files.Files.HistoryFiles); }
    }

    [PythonTrimTheory]
    [InlineData(72.8, true)]
    [InlineData(72.9, false)]
    public async Task Trim_FractionalSourceFpsUsesMeasuredExtentInsteadOfAssumingProjectCadence(double end, bool allowed)
    {
        using var fixture = new ProbeFixture(fpsRate: "30000/1001", frames: 91); using var service = fixture.Service();
        var baseline = await fixture.Files.MigrateAsync(service);
        var bytes = File.ReadAllBytes(fixture.Files.Files.ProjectPath);
        // Measured source capacity is72.8728 project frames, between both requested end values.
        var reply = await service.TrimClipAsync(fixture.Key, "clip-main", 0, end, 12, 12, baseline.Fingerprint);
        Assert.Equal(1, fixture.Probes); Assert.True(fixture.AllExited);
        if (allowed)
        { Assert.Empty(reply.ErrorCode); Assert.Equal(73, ProjectContractJson.Deserialize(reply.ProjectJson).Montage.Clips[0].DurationFrames); }
        else
        { Assert.Equal("invalid_trim", reply.ErrorCode); Assert.Equal(bytes, File.ReadAllBytes(fixture.Files.Files.ProjectPath)); Assert.Single(fixture.Files.Files.HistoryFiles); }
    }

    [Theory]
    [InlineData("revision")]
    [InlineData("bytes")]
    [InlineData("unknown")]
    [InlineData("nan")]
    [InlineData("range")]
    public async Task Trim_InvalidOrStaleRequestDoesNotProbeOrWrite(string failure)
    {
        using var fixture = new BackendWriteFixture(); using var service = fixture.Service();
        var baseline = await fixture.MigrateAsync(service);
        if (failure == "bytes") File.AppendAllText(fixture.Files.ProjectPath, " ");
        var before = File.ReadAllBytes(fixture.Files.ProjectPath);
        var result = await service.TrimClipAsync(fixture.Options.SessionKey, failure == "unknown" ? "absent" : "clip-main",
            failure == "nan" ? double.NaN : 0, failure == "range" ? 0 : 12, 12,
            failure == "revision" ? 11 : 12, baseline.Fingerprint);
        Assert.Equal(failure is "revision" or "bytes" ? "revision_conflict" : "invalid_trim", result.ErrorCode);
        Assert.Equal(before, File.ReadAllBytes(fixture.Files.ProjectPath)); Assert.Single(fixture.Files.HistoryFiles);
    }

    [PythonWriterFact]
    public async Task Trim_RejectsExactBytesChangedDuringProbe_AndReapsOwnedProbeWithoutCommit()
    {
        using var fixture = new ProbeFixture(blocked: true); using var service = fixture.Service();
        var baseline = await fixture.Files.MigrateAsync(service);
        var request = service.TrimClipAsync(fixture.Key, "clip-main", 0, 12, 12, 12, baseline.Fingerprint);
        await fixture.Started.Task.WaitAsync(TimeSpan.FromSeconds(5));
        File.AppendAllText(fixture.Files.Files.ProjectPath, " ");
        var external = File.ReadAllBytes(fixture.Files.Files.ProjectPath);
        File.WriteAllBytes(fixture.ReleasePath, []);
        var result = await request.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Equal("revision_conflict", result.ErrorCode); Assert.True(fixture.AllExited);
        Assert.Equal(external, File.ReadAllBytes(fixture.Files.Files.ProjectPath)); Assert.Single(fixture.Files.Files.HistoryFiles);
    }

    [PythonTrimTheory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Trim_CancelOrDisposeDuringProbeReapsOwnedProcessAndDoesNotWrite(bool dispose)
    {
        using var fixture = new ProbeFixture(blocked: true); using var service = fixture.Service();
        var baseline = await fixture.Files.MigrateAsync(service);
        var before = File.ReadAllBytes(fixture.Files.Files.ProjectPath);
        using var stop = new CancellationTokenSource();
        var request = service.TrimClipAsync(fixture.Key, "clip-main", 0, 12, 12, 12, baseline.Fingerprint, stop.Token);
        await fixture.Started.Task.WaitAsync(TimeSpan.FromSeconds(5));
        if (dispose) service.Dispose(); else stop.Cancel();
        if (dispose) Assert.Equal("media_timeout", (await request.WaitAsync(TimeSpan.FromSeconds(5))).ErrorCode);
        else await Assert.ThrowsAnyAsync<OperationCanceledException>(() => request);
        Assert.True(fixture.AllExited); Assert.Equal(before, File.ReadAllBytes(fixture.Files.Files.ProjectPath));
        Assert.Single(fixture.Files.Files.HistoryFiles); Assert.False(File.Exists(fixture.Files.Files.JournalPath));
    }

    [PythonWriterFact]
    public async Task Trim_WaitsForPreviewFrameAdmission_ThenProbesAndCommitsExactlyOnce()
    {
        using var fixture = new ProbeFixture(frameBlocked: true); using var service = fixture.Service();
        var baseline = await fixture.Files.MigrateAsync(service);
        var bytes = File.ReadAllBytes(fixture.Files.Files.ProjectPath);
        var frame = service.FrameAsync(fixture.Key, "video-a", 0, 12, baseline.Fingerprint);
        await fixture.FrameStarted.Task.WaitAsync(TimeSpan.FromSeconds(5));
        var trim = service.TrimClipAsync(fixture.Key, "clip-main", 4, 20, 12, 12, baseline.Fingerprint);
        Assert.False(trim.IsCompleted); Assert.Equal(1, fixture.Probes);
        Assert.Equal(bytes, File.ReadAllBytes(fixture.Files.Files.ProjectPath)); Assert.Single(fixture.Files.Files.HistoryFiles);
        File.WriteAllBytes(fixture.FrameReleasePath, []);
        Assert.Empty((await frame.WaitAsync(TimeSpan.FromSeconds(5))).ErrorCode);
        var saved = await trim.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Empty(saved.ErrorCode); Assert.Equal(13, ProjectContractJson.Deserialize(saved.ProjectJson).Revision);
        Assert.Equal(16, ProjectContractJson.Deserialize(saved.ProjectJson).Montage.Clips[0].DurationFrames);
        Assert.Equal(2, fixture.Probes); Assert.Equal(2, fixture.Files.Files.HistoryFiles.Length);
        Assert.True(fixture.AllExited);
    }

    [PythonWriterFact]
    public async Task Trim_CancelWhileWaitingForPreviewAdmission_DoesNotProbeOrWrite()
    {
        using var fixture = new ProbeFixture(frameBlocked: true); using var service = fixture.Service();
        var baseline = await fixture.Files.MigrateAsync(service);
        var bytes = File.ReadAllBytes(fixture.Files.Files.ProjectPath);
        var frame = service.FrameAsync(fixture.Key, "video-a", 0, 12, baseline.Fingerprint);
        await fixture.FrameStarted.Task.WaitAsync(TimeSpan.FromSeconds(5));
        using var stop = new CancellationTokenSource();
        var trim = service.TrimClipAsync(fixture.Key, "clip-main", 4, 20, 12, 12, baseline.Fingerprint, stop.Token);
        Assert.False(trim.IsCompleted); stop.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => trim);
        Assert.Equal(1, fixture.Probes);
        Assert.Equal(bytes, File.ReadAllBytes(fixture.Files.Files.ProjectPath)); Assert.Single(fixture.Files.Files.HistoryFiles);
        File.WriteAllBytes(fixture.FrameReleasePath, []);
        Assert.Empty((await frame.WaitAsync(TimeSpan.FromSeconds(5))).ErrorCode); Assert.True(fixture.AllExited);
    }

    private sealed class ProbeFixture : IDisposable
    {
        internal BackendWriteFixture Files { get; } = new();
        internal string Key => Files.Options.SessionKey;
        internal string ReleasePath => Path.Combine(Files.Files.Root, "release-trim-probe");
        internal string FrameReleasePath => Path.Combine(Files.Files.Root, "release-trim-frame");
        internal TaskCompletionSource<bool> Started { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        internal TaskCompletionSource<bool> FrameStarted { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private readonly List<(Process Handle, int Id, DateTime Started)> _children = [];
        private readonly bool _blocked;
        private readonly bool _frameBlocked;
        private readonly string _fpsRate;
        private readonly int _frames;
        internal int Probes { get; private set; }
        internal bool AllExited => _children.All(Exited);
        private static bool Exited((Process Handle, int Id, DateTime Started) child)
        {
            try { using var process = Process.GetProcessById(child.Id); return process.HasExited || process.StartTime != child.Started; }
            catch (ArgumentException) { return true; }
        }
        internal ProbeFixture(bool blocked = false, bool frameBlocked = false, string fpsRate = "30/1", int frames = 90)
        {
            _blocked = blocked;
            _frameBlocked = frameBlocked;
            _fpsRate = fpsRate; _frames = frames;
            var config = JsonNode.Parse(File.ReadAllText(Files.Base.ConfigPath))!;
            config["projects_root"] = Files.Files.ProjectDirectory;
            File.WriteAllText(Files.Base.ConfigPath, config.ToJsonString());
            var media = Path.Combine(Files.Files.ProjectDirectory, "Source.mp4"); File.WriteAllBytes(media, [1, 2, 3]);
            var project = JsonNode.Parse(File.ReadAllText(Files.Files.ProjectPath))!;
            project["assets"]!["video-a"]!["path"] = media;
            File.WriteAllText(Files.Files.ProjectPath, project.ToJsonString());
        }
        internal StudioBackendService Service()
        {
            var decoder = new BackendMediaDecoder("controlled-ffmpeg", "controlled-ffprobe", requested =>
            {
                var frame = requested.FileName == "controlled-ffmpeg";
                var start = new ProcessStartInfo(Environment.GetEnvironmentVariable("STUDIO_TEST_PYTHON")!)
                { UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true, RedirectStandardError = true };
                var probe = JsonSerializer.Serialize(new { streams = new[] { new { codec_type = "video", width = 320, height = 240,
                    avg_frame_rate = _fpsRate, r_frame_rate = _fpsRate, nb_frames = _frames.ToString(System.Globalization.CultureInfo.InvariantCulture) } } });
                var code = "import pathlib,sys,time\np=pathlib.Path(sys.argv[1])\n" +
                    ((frame ? _frameBlocked : _blocked) ? "while not p.exists(): time.sleep(.005)\n" : "") +
                    (frame ? "sys.stdout.buffer.write(bytes([7])*518400)" : "print(" + JsonSerializer.Serialize(probe) + ")");
                foreach (var arg in new[] { "-s", "-B", "-c", code, frame ? FrameReleasePath : ReleasePath }) start.ArgumentList.Add(arg);
                var process = Process.Start(start)!; _children.Add((process, process.Id, process.StartTime));
                if (frame) FrameStarted.TrySetResult(true); else { Probes++; Started.TrySetResult(true); }
                return process;
            });
            var reader = new BackendMediaReader(new BackendMediaResolverFactory(Files.Options), decoder);
            return new(Files.Options, Files.Files.Store(), new UnitLaunchSettings(1, 12345, "test"), reader);
        }
        public void Dispose()
        {
            try
            {
                foreach (var child in _children)
                {
                    try
                    {
                        if (!Exited(child))
                        {
                            using var process = Process.GetProcessById(child.Id);
                            if (process.StartTime == child.Started) { process.Kill(entireProcessTree: true); process.WaitForExit(5000); }
                        }
                    }
                    finally { child.Handle.Dispose(); }
                }
            }
            finally { Files.Dispose(); }
        }
    }
}

public sealed class PythonTrimTheoryAttribute : TheoryAttribute
{
    public PythonTrimTheoryAttribute()
    {
        if (string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable("STUDIO_TEST_PYTHON")))
            Skip = "Requires explicit STUDIO_TEST_PYTHON for the owned probe process.";
    }
}
