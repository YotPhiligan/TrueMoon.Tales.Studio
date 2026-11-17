using System.Diagnostics;
using System.Text.Json.Nodes;
using TrueMoon.Mithril;
using TrueMoon.Tales.Studio.Backend;
using TrueMoon.Tales.Studio.Tests.Persistence;
using Xunit;

namespace TrueMoon.Tales.Studio.Tests.Backend;

public sealed class BackendMediaServiceTests
{
    [PythonWriterFact]
    public async Task MediaQuery_RejectsACommitDuringDecode_WithoutAddingAWriteOrReturningStaleData()
    {
        using var controlled = new ControlledProbe();
        var files = controlled.Fixture.Files;
        var store = files.Store();
        await store.MigrateAsync(await store.OpenAsync(), 11, "fixture");
        var baseline = await store.OpenAsync();
        using var writer = controlled.Fixture.Service();
        Assert.Empty((await writer.AcquireWriterAsync(controlled.Key, 12, baseline.Fingerprint)).ErrorCode);
        using var reader = controlled.Service(store);
        var pending = reader.MediaInfoAsync(controlled.Key, "video-a", 12, baseline.Fingerprint);
        await controlled.Started.Task.WaitAsync(TimeSpan.FromSeconds(5));
        var committed = await writer.MoveClipAsync(controlled.Key, "clip-main", 14, 12, baseline.Fingerprint);
        Assert.Empty(committed.ErrorCode);
        var expected = File.ReadAllBytes(files.ProjectPath);
        Assert.Equal(13, (await store.OpenAsync()).Project.Revision);
        File.WriteAllBytes(controlled.ReleasePath, []);
        var result = await pending.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Equal("revision_conflict", result.ErrorCode);
        Assert.Empty(result.Pixels); Assert.Empty(result.Fingerprint);
        Assert.Equal(expected, File.ReadAllBytes(files.ProjectPath));
        Assert.Equal(2, files.HistoryFiles.Length);
        Assert.True(controlled.Exited);
    }

    [PythonWriterFact]
    public async Task MediaQuery_BoundsAdmission_AndDisposalCancelsAndReapsItsActiveDecoderWithoutWriting()
    {
        using var controlled = new ControlledProbe();
        var files = controlled.Fixture.Files; var store = files.Store();
        await store.MigrateAsync(await store.OpenAsync(), 11, "fixture");
        var baseline = await store.OpenAsync();
        var expected = File.ReadAllBytes(files.ProjectPath);
        using var reader = controlled.Service(store);
        var pending = reader.MediaInfoAsync(controlled.Key, "video-a", 12, baseline.Fingerprint);
        await controlled.Started.Task.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.False(controlled.Child!.HasExited);
        var overload = await reader.MediaInfoAsync(controlled.Key, "video-a", 12, baseline.Fingerprint);
        Assert.Equal("media_busy", overload.ErrorCode);
        reader.Dispose();
        Assert.True(controlled.Exited);
        Assert.Equal("media_timeout", (await pending.WaitAsync(TimeSpan.FromSeconds(5))).ErrorCode);
        Assert.Equal(expected, File.ReadAllBytes(files.ProjectPath)); Assert.Single(files.HistoryFiles);
        Assert.Equal(baseline.Fingerprint, (await store.OpenAsync()).Fingerprint);
    }

    private sealed class ControlledProbe : IDisposable
    {
        internal BackendWriteFixture Fixture { get; } = new();
        internal string Key => Fixture.Options.SessionKey;
        internal string ReleasePath => Path.Combine(Fixture.Files.Root, "release-probe");
        internal TaskCompletionSource<bool> Started { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        internal Process? Child { get; private set; }
        private int _childId;
        private DateTime _childStarted;
        internal bool Exited
        {
            get
            {
                if (_childId == 0) return true;
                try { using var observed = Process.GetProcessById(_childId); return observed.HasExited || observed.StartTime != _childStarted; }
                catch (ArgumentException) { return true; }
            }
        }
        internal ControlledProbe()
        {
            var config = JsonNode.Parse(File.ReadAllText(Fixture.Base.ConfigPath))!;
            config["projects_root"] = Fixture.Files.ProjectDirectory;
            File.WriteAllText(Fixture.Base.ConfigPath, config.ToJsonString());
            var media = Path.Combine(Fixture.Files.ProjectDirectory, "source.mp4");
            File.WriteAllBytes(media, [1, 2, 3]);
            var project = JsonNode.Parse(File.ReadAllText(Fixture.Files.ProjectPath))!;
            project["assets"]!["video-a"]!["path"] = media;
            File.WriteAllText(Fixture.Files.ProjectPath, project.ToJsonString());
        }
        internal StudioBackendService Service(TrueMoon.Tales.Studio.Persistence.ProjectStore store)
        {
            var decoder = new BackendMediaDecoder("controlled-ffmpeg", "controlled-ffprobe", _ =>
            {
                var start = new ProcessStartInfo(Environment.GetEnvironmentVariable("STUDIO_TEST_PYTHON")!)
                {
                    UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true, RedirectStandardError = true
                };
                var code = "import pathlib,sys,time\np=pathlib.Path(sys.argv[1])\nwhile not p.exists(): time.sleep(.005)\nprint('{\"streams\":[{\"codec_type\":\"video\",\"width\":320,\"height\":240,\"avg_frame_rate\":\"30/1\",\"r_frame_rate\":\"30/1\",\"nb_frames\":\"90\"}]}')";
                foreach (var arg in new[] { "-s", "-B", "-c", code, ReleasePath }) start.ArgumentList.Add(arg);
                Child = Process.Start(start)!; _childId = Child.Id; _childStarted = Child.StartTime;
                Started.TrySetResult(true); return Child;
            });
            var reader = new BackendMediaReader(new BackendMediaResolverFactory(Fixture.Options), decoder);
            return new(Fixture.Options with { WriteEnabled = false }, store, new UnitLaunchSettings(1, 12345, "test"), reader);
        }
        public void Dispose()
        {
            try
            {
                if (Child is not null && !Exited) { Child.Kill(entireProcessTree: true); Child.WaitForExit(5000); }
            }
            finally { Child?.Dispose(); Fixture.Dispose(); }
        }
    }
}
