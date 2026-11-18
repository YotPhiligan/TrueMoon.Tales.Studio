using System.Diagnostics;
using System.Net;
using System.Net.Http.Json;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Nodes;
using TrueMoon;
using TrueMoon.Argentis;
using TrueMoon.Dependencies;
using TrueMoon.Tales.Studio.Backend;
using TrueMoon.Tales.Studio.Backend.Http;
using TrueMoon.Tales.Studio.Presentation.Scene;
using TrueMoon.Tales.Studio.Tests.Support;
using Xunit;

namespace TrueMoon.Tales.Studio.Tests.Backend;

public sealed class BackendExportEndToEndTests
{
    [BackendMediaFact]
    public async Task Export_FromWorkspace_SavesCheckedMp4_AndReopensExistingJobByKey()
    {
        using var files = new ExportFixture();
        await files.PrepareAsync();
        byte[] saved;
        byte[][] history;
        string key;
        string id;
        string sha;
        var output = Path.Combine(files.OutputDirectory, "montage.mp4");
        await using (var session = await files.OpenAsync(workspace: true))
        {
            using var host = Workspace(session);
            var model = session.App.Services.Resolve<SceneViewModel>()!;
            Ready(host, model);
            var timeline = host.Window.Timeline;
            Assert.True(timeline.Bounds.Width > host.Window.Preview.Bounds.Width);
            host.Click(ButtonIn(host.Window.Preview, "След. кадр"));
            Ready(host, model);
            var cursor = model.TimelineCursor;
            host.Click(ButtonIn(host.Window.Preview, "Исходник"));
            host.PumpUntil(() => model.ViewingSource && model.Idle);
            Assert.Equal(cursor, model.TimelineCursor);
            host.Click(ButtonIn(host.Window.Preview, "Монтаж"));
            host.PumpUntil(() => !model.ViewingSource && model.Idle);
            Assert.Equal(cursor, model.TimelineCursor);

            var before = File.ReadAllBytes(files.ProjectPath);
            host.Click(ButtonIn(host.Window.Inspector, "Начало по кадру"));
            Assert.True(model.Modified);
            host.Click(ButtonIn(timeline, "Компактно"));
            Assert.True(model.Modified);
            Assert.Equal(host.Window.Preview.Bounds.Width, timeline.Bounds.Width);
            host.Resize(880, 640, 1.5f);
            host.Window.ApplyTheme(Theme.Light);
            host.Update();
            Assert.Same(timeline, host.Window.Timeline);
            Assert.True(model.Modified);
            Assert.Equal(before, File.ReadAllBytes(files.ProjectPath));

            var entry = ButtonIn(host.Window.Heading, "Экспорт MP4");
            host.Click(entry);
            var panel = WaitForExport(host);
            Assert.False(ButtonIn(panel, "Проверить монтаж").IsEnabled);
            Assert.Empty(files.Requests());
            host.Input(new(InputKind.KeyDown, Key: UiKey.Escape));
            Assert.Null(host.Window.Overlays.Popup);
            Assert.True(entry.IsFocused);

            host.Click(host.Window.SaveButton);
            host.PumpUntil(() => !model.Modified && model.Scene!.Revision == 13 || model.HasError, TimeSpan.FromSeconds(15));
            Assert.False(model.HasError, model.Status);
            saved = File.ReadAllBytes(files.ProjectPath);
            history = files.History();
            Assert.Equal(2, history.Length);
            Assert.Equal("revision_conflict", Assert.Throws<BackendCommandException>(() =>
                session.Connection.CheckExportAsync("project", 12, Hash(before), CancellationToken.None).GetAwaiter().GetResult()).Code);
            Assert.Equal("export_not_found", Assert.Throws<BackendCommandException>(() =>
                session.Connection.FindExportAsync("missing-workspace-job", CancellationToken.None).GetAwaiter().GetResult()).Code);
            Assert.Empty(files.Requests());
            host.Click(entry);
            panel = WaitForExport(host);
            var check = ButtonIn(panel, "Проверить монтаж");
            host.PumpUntil(() => check.IsEnabled);
            host.Click(check);
            var start = ButtonIn(panel, "Запустить экспорт");
            host.PumpUntil(() => start.IsEnabled, TimeSpan.FromSeconds(16));
            Assert.Contains(RasterWindow<SceneView>.Walk(panel).OfType<Text>(), row =>
                row.Value.Contains("160 × 120", StringComparison.Ordinal) && row.Value.Contains("рев. 13", StringComparison.Ordinal));
            Assert.Empty(files.Requests());
            host.Capture("export-checked", new { model.Scene!.Revision });
            host.Click(start);
            var save = ButtonIn(panel, "Сохранить MP4");
            host.PumpUntil(() => save.IsEnabled, TimeSpan.FromSeconds(30));
            key = File.ReadAllText(files.Checkpoint());
            var job = session.Connection.FindExportAsync(key, CancellationToken.None).GetAwaiter().GetResult();
            id = job.Id;
            sha = Assert.IsType<string>(job.ArtifactSha256);
            Assert.Equal(13, job.ProjectRevision);
            Assert.Single(files.Requests());
            host.Click(save);
            host.PumpUntil(() => File.Exists(output) && save.IsEnabled, TimeSpan.FromSeconds(16));
            Assert.Equal(sha, Hash(File.ReadAllBytes(output)), StringComparer.OrdinalIgnoreCase);
            host.Click(save);
            host.PumpUntil(() => RasterWindow<SceneView>.Walk(panel).OfType<Text>().Any(row =>
                row.Value.Contains("уже существует", StringComparison.Ordinal)));
            Assert.Equal(sha, Hash(File.ReadAllBytes(output)), StringComparer.OrdinalIgnoreCase);
            files.AssertProject(saved, history);
        }

        await using (var session = await files.OpenAsync(workspace: true))
        {
            using var host = Workspace(session);
            Ready(host, session.App.Services.Resolve<SceneViewModel>()!);
            host.Click(ButtonIn(host.Window.Heading, "Экспорт MP4"));
            var panel = WaitForExport(host);
            var recover = ButtonIn(panel, "Восстановить");
            host.PumpUntil(() => recover.IsEnabled, TimeSpan.FromSeconds(15));
            host.Click(recover);
            host.PumpUntil(() => ButtonIn(panel, "Сохранить MP4").IsEnabled, TimeSpan.FromSeconds(15));
            var job = session.Connection.FindExportAsync(key, CancellationToken.None).GetAwaiter().GetResult();
            Assert.Equal(id, job.Id);
            Assert.Equal(sha, job.ArtifactSha256);
            Assert.Single(files.Requests());
            files.AssertProject(saved, history);
        }
    }

    [BackendMediaFact]
    public async Task Export_ActiveWorkspaceJobCancels_AndReopenDoesNotSubmit()
    {
        using var files = new ExportFixture(longRender: true);
        await files.PrepareAsync();
        var bytes = File.ReadAllBytes(files.ProjectPath);
        var history = files.History();
        string key;
        string id;
        await using (var session = await files.OpenAsync(workspace: true))
        {
            using var host = Workspace(session);
            Ready(host, session.App.Services.Resolve<SceneViewModel>()!);
            host.Click(ButtonIn(host.Window.Heading, "Экспорт MP4"));
            var panel = WaitForExport(host);
            var check = ButtonIn(panel, "Проверить монтаж");
            host.PumpUntil(() => check.IsEnabled);
            host.Click(check);
            var start = ButtonIn(panel, "Запустить экспорт");
            host.PumpUntil(() => start.IsEnabled, TimeSpan.FromSeconds(16));
            host.Click(start);
            var cancel = ButtonIn(panel, "Отменить экспорт");
            host.PumpUntil(() => cancel.IsEnabled, TimeSpan.FromSeconds(16));
            key = File.ReadAllText(files.Checkpoint());
            var job = session.Connection.FindExportAsync(key, CancellationToken.None).GetAwaiter().GetResult();
            id = job.Id;
            var directory = files.JobDirectory(id);
            host.PumpUntil(() => File.Exists(Path.Combine(directory, "Video.mp4")) && cancel.IsEnabled, TimeSpan.FromSeconds(20));
            host.Click(cancel);
            host.PumpUntil(() => RasterWindow<SceneView>.Walk(panel).OfType<Text>().Any(row =>
                row.Value.Contains("Экспорт отменён", StringComparison.Ordinal)), TimeSpan.FromSeconds(12));
            Assert.False(ButtonIn(panel, "Сохранить MP4").IsEnabled);
            WaitForWorkerLockAsync(directory, CancellationToken.None).GetAwaiter().GetResult();
            files.AssertProject(bytes, history);
        }
        await using (var session = await files.OpenAsync(workspace: true))
        {
            using var host = Workspace(session);
            Ready(host, session.App.Services.Resolve<SceneViewModel>()!);
            host.Click(ButtonIn(host.Window.Heading, "Экспорт MP4"));
            var panel = WaitForExport(host);
            host.PumpUntil(() => RasterWindow<SceneView>.Walk(panel).OfType<Text>().Any(row =>
                row.Value.Contains("Экспорт отменён", StringComparison.Ordinal)));
            Assert.False(ButtonIn(panel, "Сохранить MP4").IsEnabled);
            Assert.Equal(id, session.Connection.FindExportAsync(key, CancellationToken.None).GetAwaiter().GetResult().Id);
            Assert.Single(files.Requests());
            files.AssertProject(bytes, history);
        }
    }

    private static RasterWindow<SceneView> Workspace(ExportSession session) =>
        new(session.App.Services.Resolve<IFactory<SceneView>>()!.Create()!, "workspace-export", 1280, 820);

    private static Button ButtonIn(Element root, string label) =>
        Assert.Single(RasterWindow<SceneView>.Walk(root).OfType<Button>(), button => button.Value == label);

    private static SceneExportPanel WaitForExport(RasterWindow<SceneView> host)
    {
        host.PumpUntil(() => host.Window.Overlays.Popup is SceneExportPanel);
        return Assert.IsType<SceneExportPanel>(host.Window.Overlays.Popup);
    }

    private static void Ready(RasterWindow<SceneView> host, SceneViewModel model)
    {
        host.PumpUntil(() => model.Loaded && model.Pixels is not null && model.Idle || model.HasError, TimeSpan.FromSeconds(15));
        Assert.False(model.HasError, model.Status);
    }

    [BackendMediaFact]
    public async Task Export_TrimmedMontageViaCli_DownloadsVerifiedMp4_AndRecoversAfterRestart()
    {
        using var files = new ExportFixture();
        await files.PrepareAsync();
        string id;
        string sha;
        string requestHash;
        byte[] finishedProject;
        string jobDirectory;
        await using (var session = await files.OpenAsync())
        {
            var opened = await session.Connection.OpenAsync();
            var saved = await session.Connection.TrimAsync("clip-main", 4, 20, 4, opened.Project.Revision, opened.Fingerprint);
            Assert.Equal(13, saved.Project.Revision);
            var projectBytes = File.ReadAllBytes(files.ProjectPath);
            var history = files.History();
            var start = await session.CliAsync("backend-export-start", "--client-key", "trimmed-export-first",
                "--profile", "project", "--revision", "13", "--sha256", saved.Fingerprint);
            id = start["id"]!.GetValue<string>();
            requestHash = start["request_sha256"]!.GetValue<string>();
            jobDirectory = files.JobDirectory(id);
            var requestPath = Path.Combine(jobDirectory, "Request.json");
            var requestBytes = File.ReadAllBytes(requestPath);
            Assert.Equal(requestHash, Hash(requestBytes), StringComparer.OrdinalIgnoreCase);
            var request = JsonNode.Parse(requestBytes)!;
            Assert.Equal(13, request["project_revision"]!.GetValue<int>());
            Assert.Equal(saved.Fingerprint, request["project_fingerprint"]!.GetValue<string>(), StringComparer.OrdinalIgnoreCase);
            Assert.Equal(4, request["clips"]![0]!["start_frame"]!.GetValue<int>());
            Assert.Equal(4d, request["clips"]![0]!["source_in_frame"]!.GetValue<double>());
            Assert.Equal(20d, request["clips"]![0]!["source_out_frame"]!.GetValue<double>());
            Assert.All(request["assets"]!.AsObject(), row =>
                Assert.Equal(Hash(File.ReadAllBytes(row.Value!["path"]!.GetValue<string>())),
                    row.Value["expected_sha256"]!.GetValue<string>(), StringComparer.OrdinalIgnoreCase));
            files.AssertProject(projectBytes, history);

            // Changes after submission belong to the next export, even when a client repeats Start.
            var moved = await session.Connection.MoveAsync("clip-main", 8, 13, saved.Fingerprint);
            Assert.Equal(14, moved.Project.Revision);
            finishedProject = File.ReadAllBytes(files.ProjectPath);
            var finishedHistory = files.History();
            var same = await session.CliAsync("backend-export-start", "--client-key", "trimmed-export-first",
                "--profile", "project", "--revision", "13", "--sha256", saved.Fingerprint);
            Assert.Equal(id, same["id"]!.GetValue<string>());
            Assert.Single(files.Requests());
            var result = await session.UntilAsync(id, row => row["state"]!.GetValue<string>() != "running", jobDirectory);
            Assert.True(result["state"]!.GetValue<string>() == "completed", FailureDetails(jobDirectory, result));
            Assert.Equal(13, result["project_revision"]!.GetValue<int>());
            Assert.Equal(72, result["frames"]!.GetValue<int>());
            sha = result["artifact_sha256"]!.GetValue<string>();
            var output = Path.Combine(files.Files.Files.Root, "verified.mp4");
            await session.CliAsync("backend-export-download", "--id", id, "--sha256", sha, "--output", output);
            Assert.Equal(result["artifact_size_bytes"]!.GetValue<long>(), new FileInfo(output).Length);
            Assert.Equal(sha, Hash(File.ReadAllBytes(output)), StringComparer.OrdinalIgnoreCase);
            await files.AssertMediaAsync(output);
            files.AssertProject(finishedProject, finishedHistory);
            Assert.Equal(requestBytes, File.ReadAllBytes(requestPath));
        }

        await using (var session = await files.OpenAsync())
        {
            var recovered = await session.CliAsync("backend-export-recover", "--id", id);
            Assert.Equal("completed", recovered["state"]!.GetValue<string>());
            Assert.Equal(sha, recovered["artifact_sha256"]!.GetValue<string>());
            Assert.Equal(requestHash, recovered["request_sha256"]!.GetValue<string>());
            var artifact = Path.Combine(jobDirectory, "Draft.mp4");
            using (var stream = File.Open(artifact, FileMode.Open, FileAccess.ReadWrite))
            {
                var changed = (byte)(stream.ReadByte() ^ 1);
                stream.Position = 0;
                stream.WriteByte(changed);
            }
            var corrupt = await session.PostAsync("export-recover", new { id });
            Assert.Equal("failed", corrupt["state"]!.GetValue<string>());
            Assert.Equal("invalid_export_artifact", corrupt["error_code"]!.GetValue<string>());
            using var chunk = await session.RawAsync("export-chunk", new { id, artifact_sha256 = sha, offset = 0 });
            Assert.False(chunk.IsSuccessStatusCode);
            Assert.Equal(finishedProject, File.ReadAllBytes(files.ProjectPath));
            Assert.Single(files.Requests());
        }
    }

    [BackendMediaTheory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Export_ActiveWorkerIsReaped_AndJobDoesNotReplay(bool crash)
    {
        using var files = new ExportFixture(longRender: true);
        await files.PrepareAsync();
        var bytes = File.ReadAllBytes(files.ProjectPath);
        var history = files.History();
        string id;
        byte[] request;
        string jobDirectory;
        await using (var session = await files.OpenAsync())
        {
            var observed = await session.Connection.OpenAsync();
            var started = await session.PostAsync("export-start", new
            {
                client_key = "shutdown-test-export", profile = "project",
                expected_revision = observed.Project.Revision, fingerprint = observed.Fingerprint
            });
            id = started["id"]!.GetValue<string>();
            jobDirectory = files.JobDirectory(id);
            request = File.ReadAllBytes(Path.Combine(jobDirectory, "Request.json"));
            using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(20));
            while (!File.Exists(Path.Combine(jobDirectory, "Video.mp4")) && !deadline.IsCancellationRequested)
            {
                var status = await session.PostAsync("export-status", new { id });
                if (status["state"]!.GetValue<string>() != "running")
                    Assert.Fail(FailureDetails(jobDirectory, status));
                await Task.Delay(20);
            }
            var active = await session.PostAsync("export-status", new { id });
            Assert.True(File.Exists(Path.Combine(jobDirectory, "Video.mp4")), FailureDetails(jobDirectory, active));
            Assert.Equal("running", active["state"]!.GetValue<string>());
            if (crash)
            {
                var factory = session.App.Services.Resolve<BackendProcessFactory>()!;
                Assert.True(factory.Owned!.IsServing(observed.Identity.ProcessId));
                using var backend = Process.GetProcessById(observed.Identity.ProcessId);
                // Kill just the owned backend. Its worker must respond to pipe closure independently.
                backend.Kill();
                await backend.WaitForExitAsync(deadline.Token);
                await WaitForWorkerLockAsync(jobDirectory, deadline.Token);
            }
            else
            {
                using var stale = await session.RawAsync("export-cancel", new { id, expected_job_revision = active["revision"]!.GetValue<long>() + 1 });
                Assert.Equal(HttpStatusCode.Conflict, stale.StatusCode);
                await session.CliAsync("backend-export-cancel", "--id", id, "--job-revision",
                    active["revision"]!.GetValue<long>().ToString(System.Globalization.CultureInfo.InvariantCulture));
                var cancelled = await session.UntilAsync(id, row => row["state"]!.GetValue<string>() == "cancelled", jobDirectory);
                Assert.Null(cancelled["artifact_sha256"]);
                await WaitForWorkerLockAsync(jobDirectory, deadline.Token);
            }
        }

        await using (var session = await files.OpenAsync())
        {
            var recovered = await session.CliAsync("backend-export-recover", "--id", id);
            Assert.Equal(crash ? "interrupted" : "cancelled", recovered["state"]!.GetValue<string>());
            var repeated = await session.CliAsync("backend-export-start", "--client-key", "shutdown-test-export", "--profile", "project",
                "--revision", "12", "--sha256", recovered["project_fingerprint"]!.GetValue<string>());
            Assert.Equal(id, repeated["id"]!.GetValue<string>());
            Assert.Equal(recovered["state"]!.GetValue<string>(), repeated["state"]!.GetValue<string>());
            Assert.Equal(request, File.ReadAllBytes(Path.Combine(jobDirectory, "Request.json")));
            Assert.Single(files.Requests());
            await WaitForWorkerLockAsync(jobDirectory, CancellationToken.None);
            files.AssertProject(bytes, history);
        }
    }

    private static async Task WaitForWorkerLockAsync(string folder, CancellationToken token)
    {
        if (OperatingSystem.IsMacOS()) throw new PlatformNotSupportedException();
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(token);
        deadline.CancelAfter(TimeSpan.FromSeconds(5));
        while (true)
        {
            deadline.Token.ThrowIfCancellationRequested();
            try
            {
                using var owner = File.Open(Path.Combine(folder, "Worker.lock"), FileMode.Open, FileAccess.ReadWrite, FileShare.ReadWrite);
                owner.Lock(0, 1);
                owner.Unlock(0, 1);
                return;
            }
            catch (IOException) { await Task.Delay(20, deadline.Token); }
        }
    }

    private static string Hash(byte[] bytes) => Convert.ToHexString(SHA256.HashData(bytes));

    private static string FailureDetails(string directory, JsonNode status)
    {
        var log = Path.Combine(directory, "Worker.log");
        var ffmpeg = Path.Combine(directory, "FFmpeg.log");
        var files = string.Join(", ", Directory.EnumerateFiles(directory).Select(Path.GetFileName));
        return $"{status}\nFiles: {files}\n{ReadLog(log)}\n{ReadLog(ffmpeg)}";

        static string ReadLog(string path)
        {
            try
            {
                using var file = File.Open(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
                using var reader = new StreamReader(file);
                return reader.ReadToEnd();
            }
            catch (IOException) { return ""; }
        }
    }

    private sealed class ExportFixture(bool longRender = false) : IDisposable
    {
        internal BackendWriteFixture Files { get; } = new();
        internal string ProjectPath => Files.Files.ProjectPath;
        private string Root => Files.Files.Root;
        private string Exports => Path.Combine(Root, "exports");
        internal string OutputDirectory => Exports;
        private string Media => Path.Combine(Root, "media");
        private static string Ffmpeg => Environment.GetEnvironmentVariable("STUDIO_TEST_FFMPEG")!;
        private static string Ffprobe => Environment.GetEnvironmentVariable("STUDIO_TEST_FFPROBE")!;
        internal string[] Requests() => Directory.GetFiles(Exports, "Request.json", SearchOption.AllDirectories);
        internal string Checkpoint() => Directory.GetFiles(Exports, "ClientKey", SearchOption.AllDirectories).Single();
        internal string JobDirectory(string id) => Directory.GetDirectories(Exports, id, SearchOption.AllDirectories).Single();
        internal byte[][] History() => Files.Files.HistoryFiles.Order().Select(File.ReadAllBytes).ToArray();

        internal async Task PrepareAsync()
        {
            Directory.CreateDirectory(Media);
            Directory.CreateDirectory(Exports);
            var config = JsonNode.Parse(File.ReadAllText(Files.Base.ConfigPath))!;
            config["projects_root"] = Files.Files.ProjectDirectory;
            config["mounts"] = new JsonObject { ["videos"] = Media };
            config["exports_root"] = Exports;
            config["python_executable"] = Environment.GetEnvironmentVariable("STUDIO_TEST_PYTHON");
            config["ffmpeg_executable"] = Ffmpeg;
            config["ffprobe_executable"] = Ffprobe;
            File.WriteAllText(Files.Base.ConfigPath, config.ToJsonString());
            await SourceAsync("red", 440, "Красный.mp4");
            await SourceAsync("blue", 660, "Синий.mp4");
            var project = JsonNode.Parse(File.ReadAllText(ProjectPath))!;
            project["output_size"] = longRender ? new JsonArray(1600, 900) : new JsonArray(160, 120);
            project["assets"]!["video-a"]!["path"] = "@videos/Красный.mp4";
            project["assets"]!["video-b"]!["path"] = "@videos/Синий.mp4";
            var timeline = project["timeline"]!;
            timeline["clip_audio"] = true;
            var windows = timeline["segments"]!.AsArray();
            windows[0]!["duration_frames"] = 36;
            windows[1]!["start_frame"] = 36;
            windows[1]!["duration_frames"] = longRender ? 3600 : 36;
            var clips = timeline["clips"]!.AsArray();
            clips[1]!["start_frame"] = 40;
            File.WriteAllText(ProjectPath, project.ToJsonString());
            var store = Files.Files.Store();
            await store.MigrateAsync(await store.OpenAsync(), 11, "export-fixture");
        }

        private Task<byte[]> SourceAsync(string color, int frequency, string name) => BackendTrimFixture.RunAsync(Ffmpeg,
            ["-nostdin", "-v", "error", "-f", "lavfi", "-i", $"color=c={color}:size=160x120:rate=24:duration=2", "-f", "lavfi", "-i",
             $"sine=frequency={frequency}:sample_rate=48000:duration=2", "-map", "0:v:0", "-map", "1:a:0", "-c:v", "libx264",
             "-c:a", "alac", "-ac", "2", "-threads", "1", "-n", Path.Combine(Media, name)], 4096);

        internal async Task<ExportSession> OpenAsync(bool workspace = false)
        {
            var options = BackendOptions.Create(Files.Files.ProjectDirectory, Files.Base.ConfigPath) with
            { WriteEnabled = true, Action = "serve", ExecutablePath = BackendWriteFixture.Executable() };
            var app = TrueMoon.App.Builder(_ => { }).Setup(context =>
            {
                context.UseProjectBackend(options, false);
                if (workspace) context.UseBackendScene();
            }).Build();
            var session = new ExportSession(app);
            try
            {
                using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(25));
                await app.StartAsync(deadline.Token);
                await session.Http.StartAsync(deadline.Token);
                return session;
            }
            catch { await session.DisposeAsync(); throw; }
        }

        internal void AssertProject(byte[] bytes, byte[][] history)
        {
            Assert.Equal(bytes, File.ReadAllBytes(ProjectPath));
            var current = History();
            Assert.Equal(history.Length, current.Length);
            for (var index = 0; index < current.Length; index++) Assert.Equal(history[index], current[index]);
            Assert.False(File.Exists(Files.Files.JournalPath));
        }

        internal async Task AssertMediaAsync(string output)
        {
            var metadata = JsonNode.Parse(await BackendTrimFixture.RunAsync(Ffprobe,
                ["-v", "error", "-show_streams", "-of", "json", output], 32 * 1024))!["streams"]!.AsArray();
            Assert.Equal(2, metadata.Count);
            Assert.Equal("h264", metadata[0]!["codec_name"]!.GetValue<string>());
            Assert.Equal("72", metadata[0]!["nb_frames"]!.GetValue<string>());
            Assert.Equal("24/1", metadata[0]!["avg_frame_rate"]!.GetValue<string>());
            Assert.Equal("aac", metadata[1]!["codec_name"]!.GetValue<string>());
            Assert.Equal("48000", metadata[1]!["sample_rate"]!.GetValue<string>());
            Assert.Equal(2, metadata[1]!["channels"]!.GetValue<int>());
            var frames = await BackendTrimFixture.RunAsync(Ffmpeg, ["-nostdin", "-v", "error", "-i", output, "-map", "0:v:0",
                "-an", "-vf", "select='eq(n,3)+eq(n,4)+eq(n,19)+eq(n,20)+eq(n,40)+eq(n,64)',crop=2:2:80:60",
                "-fps_mode", "passthrough", "-pix_fmt", "rgb24", "-f", "rawvideo", "pipe:1"], 72);
            Assert.Equal(72, frames.Length);
            foreach (var index in new[] { 0, 3, 5 }) Assert.All(frames.Skip(index * 12).Take(12), value => Assert.InRange(value, 0, 8));
            foreach (var index in new[] { 1, 2 })
            {
                Assert.InRange(frames[index * 12], 230, 255);
                Assert.InRange(frames[index * 12 + 2], 0, 10);
            }
            Assert.InRange(frames[48], 0, 10);
            Assert.InRange(frames[50], 230, 255);
            var pcm = await BackendTrimFixture.RunAsync(Ffmpeg, ["-nostdin", "-v", "error", "-i", output, "-map", "0:a:0",
                "-vn", "-c:a", "pcm_f32le", "-f", "f32le", "pipe:1"], 1200000);
            var samples = MemoryMarshal.Cast<byte, float>(pcm).ToArray();
            double Rms(int firstFrame, int length) => Math.Sqrt(samples.Skip(firstFrame * 4000).Take(length * 4000).Select(s => (double)s * s).Average());
            Assert.InRange(Rms(0, 2), 0, .003);
            Assert.InRange(Rms(6, 10), .03, .15);
            Assert.InRange(Rms(23, 12), 0, .003);
            Assert.InRange(Rms(42, 15), .03, .15);
        }

        public void Dispose() => Files.Dispose();
    }

    private sealed class ExportSession(IApp app) : IAsyncDisposable
    {
        internal IApp App => app;
        internal BackendConnection Connection => app.Services.Resolve<BackendConnection>()!;
        internal BackendHttpServer Http { get; } = new(app.Services.Resolve<BackendConnection>()!);

        internal async Task<HttpResponseMessage> RawAsync(string action, object body)
        {
            using var client = new HttpClient(new SocketsHttpHandler { UseProxy = false, AllowAutoRedirect = false })
                { BaseAddress = Http.Address, Timeout = TimeSpan.FromSeconds(15) };
            client.DefaultRequestHeaders.Add("X-Studio-Token", Http.Token);
            return await client.PostAsJsonAsync("/api/backend/v1/" + action, body);
        }

        internal async Task<JsonNode> PostAsync(string action, object body)
        {
            using var response = await RawAsync(action, body);
            var data = await response.Content.ReadAsStringAsync();
            Assert.True(response.IsSuccessStatusCode, data);
            return JsonNode.Parse(data)!;
        }

        internal async Task<JsonNode> CliAsync(params string[] args)
        {
            using var cli = new Support.BackendCliProcess(Http.Address, Http.Token, args);
            var result = await cli.WaitAsync();
            Assert.True(result.Code == 0, result.Error);
            return JsonNode.Parse(result.Output)!;
        }

        internal async Task<JsonNode> UntilAsync(string id, Func<JsonNode, bool> predicate, string jobDirectory)
        {
            using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(35));
            while (true)
            {
                var row = await PostAsync("export-status", new { id });
                if (predicate(row)) return row;
                if (deadline.IsCancellationRequested) Assert.Fail(FailureDetails(jobDirectory, row));
                await Task.Delay(50);
            }
        }

        public async ValueTask DisposeAsync()
        {
            try { await Http.DisposeAsync(); }
            finally { try { await app.StopAsync(); } finally { await app.DisposeAsync(); } }
        }
    }
}
