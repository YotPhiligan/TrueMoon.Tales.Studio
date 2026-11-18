using System.Text.Json;
using System.Text.Json.Nodes;
using TrueMoon.Tales.Studio.Backend;
using TrueMoon.Tales.Studio.Backend.Export;
using TrueMoon.Tales.Studio.Persistence;
using Xunit;

namespace TrueMoon.Tales.Studio.Tests.Backend;

public sealed class BackendExportJobsTests
{
    private const string Key = "export-job-client-token";
    private const int Frames = 24;
    private const int Fps = 24;

    [Fact]
    public async Task Start_FreezesRequest_ReusesSubmission_AndServesOnlyCompletedRanges()
    {
        using var files = new ExportFixture();
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(15));
        var baseline = await files.MigrateAsync(deadline.Token);
        var projectBytes = File.ReadAllBytes(files.ProjectPath);
        var history = files.History;
        var started = new TaskCompletionSource<string>(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var content = Enumerable.Range(0, BackendExportJobs.ChunkSize + 17).Select(i => (byte)(i % 251)).ToArray();
        var runs = 0;
        using var jobs = new BackendExportJobs(files.Options, Plan,
            async (directory, hash, token) =>
            {
                Interlocked.Increment(ref runs);
                started.SetResult(directory);
                await release.Task.WaitAsync(token);
                PutProof(directory, hash, content);
            });

        var running = Status(await StartAsync(jobs, baseline, deadline.Token));
        var jobDirectory = await started.Task.WaitAsync(deadline.Token);
        var request = File.ReadAllBytes(Path.Combine(jobDirectory, "Request.json"));
        Assert.Equal(BackendExportStorage.Hash(request), running.RequestSha256);
        Assert.Equal(1, running.Revision);
        Assert.Equal("running", running.State);
        Assert.Equal(baseline.Fingerprint, JsonNode.Parse(request)!["project_fingerprint"]!.GetValue<string>());
        Assert.Equal(running.Id, Status(await StartAsync(jobs, baseline, deadline.Token)).Id);
        await ExpectCode("export_conflict", () => jobs.StartAsync(Key, "final", baseline.Project.Revision, baseline.Fingerprint,
            _ => Task.FromException<ProjectFileDocument>(new InvalidOperationException("Duplicate must not reread project.")), deadline.Token));
        await ExpectCode("export_busy", () => jobs.StartAsync(Key + "-other", "draft", baseline.Project.Revision, baseline.Fingerprint,
            _ => Task.FromResult(baseline), deadline.Token));
        await ExpectCode("export_not_ready", () => jobs.ChunkAsync(running.Id, new string('A', 64), 0, deadline.Token));
        Assert.Equal(1, Volatile.Read(ref runs));

        release.SetResult();
        var completed = await UntilAsync(jobs, running.Id, "completed", deadline.Token);
        Assert.Equal(2, completed.Revision);
        Assert.Equal(content.Length, completed.ArtifactSizeBytes);
        Assert.Equal(BackendExportStorage.Hash(content), completed.ArtifactSha256);
        Assert.Equal(content[..BackendExportJobs.ChunkSize], (await jobs.ChunkAsync(running.Id, completed.ArtifactSha256!, 0, deadline.Token)).Data);
        Assert.Equal(content[BackendExportJobs.ChunkSize..], (await jobs.ChunkAsync(running.Id, completed.ArtifactSha256!, BackendExportJobs.ChunkSize, deadline.Token)).Data);
        await ExpectCode("invalid_export", () => jobs.ChunkAsync(running.Id, completed.ArtifactSha256!, content.Length, deadline.Token));
        await ExpectCode("export_conflict", () => jobs.ChunkAsync(running.Id, new string('0', 64), 0, deadline.Token));
        Assert.Equal(request, File.ReadAllBytes(Path.Combine(jobDirectory, "Request.json")));
        Assert.Equal(projectBytes, File.ReadAllBytes(files.ProjectPath));
        Assert.Equal(history, files.History);
    }

    [Fact]
    public async Task Cancel_UsesJobRevision_AndSuppressesEvenACompleteLateWorkerResult()
    {
        using var files = new ExportFixture();
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(15));
        var baseline = await files.MigrateAsync(deadline.Token);
        var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var runs = 0;
        using var jobs = new BackendExportJobs(files.Options, Plan,
            async (directory, hash, token) =>
            {
                Interlocked.Increment(ref runs);
                started.SetResult();
                try { await Task.Delay(Timeout.InfiniteTimeSpan, token); }
                catch (OperationCanceledException) { PutProof(directory, hash, [1, 2, 3]); }
            });
        var running = Status(await StartAsync(jobs, baseline, deadline.Token));
        await started.Task.WaitAsync(deadline.Token);
        await ExpectCode("export_conflict", () => jobs.CancelAsync(running.Id, 2, deadline.Token));
        Assert.Equal(1, Status(await jobs.StatusAsync(running.Id, deadline.Token)).Revision);
        var stopping = Status(await jobs.CancelAsync(running.Id, running.Revision, deadline.Token));
        Assert.Equal("stopping", stopping.State);
        Assert.Equal(2, stopping.Revision);
        var cancelled = await UntilAsync(jobs, running.Id, "cancelled", deadline.Token);
        Assert.Equal(3, cancelled.Revision);
        Assert.Null(cancelled.ArtifactSha256);
        Assert.Null(cancelled.ArtifactSizeBytes);
        await ExpectCode("export_not_ready", () => jobs.ChunkAsync(running.Id, new string('A', 64), 0, deadline.Token));
        Assert.Equal(cancelled, Status(await jobs.RecoverAsync(running.Id, deadline.Token)));
        Assert.Equal(cancelled, Status(await StartAsync(jobs, baseline, deadline.Token)));
        Assert.Equal(1, Volatile.Read(ref runs));
    }

    [Theory]
    [InlineData("complete", "completed")]
    [InlineData("no-proof", "interrupted")]
    [InlineData("wrong-request", "interrupted")]
    [InlineData("bad-bytes", "interrupted")]
    public async Task Recovery_ReconcilesOnlyMatchingProof_WithoutStartingAWorker(string evidence, string expectedState)
    {
        using var files = new ExportFixture();
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(15));
        var baseline = await files.MigrateAsync(deadline.Token);
        var storage = new BackendExportStorage(files.Options);
        var id = "export-" + BackendExportStorage.Hash(System.Text.Encoding.ASCII.GetBytes(Key))[..32].ToLowerInvariant();
        var directory = storage.JobDirectory(id);
        var plan = await Plan(baseline, "draft", directory, deadline.Token);
        var receipt = new BackendExportStatus(1, id, baseline.Project.Id, baseline.Project.Revision, baseline.Fingerprint,
            "draft", 1, "running", BackendExportStorage.Hash(plan.Request), Frames, Fps);
        storage.Create(directory, plan.Request, receipt);
        if (evidence != "no-proof")
            PutProof(directory, evidence == "wrong-request" ? new string('A', 64) : receipt.RequestSha256, [7, 11, 13]);
        if (evidence == "bad-bytes") File.WriteAllBytes(Path.Combine(directory, "Draft.mp4"), [13, 11, 7]);

        var runs = 0;
        using var jobs = new BackendExportJobs(files.Options, Plan, (_, _, _) =>
        {
            Interlocked.Increment(ref runs);
            return Task.CompletedTask;
        });
        Assert.Equal("running", Status(await jobs.StatusAsync(id, deadline.Token)).State);
        var recovered = Status(await jobs.RecoverAsync(id, deadline.Token));
        Assert.Equal(expectedState, recovered.State);
        Assert.Equal(2, recovered.Revision);
        Assert.Equal(receipt.RequestSha256, recovered.RequestSha256);
        Assert.Equal(recovered, Status(await StartAsync(jobs, baseline, deadline.Token)));
        Assert.Equal(0, Volatile.Read(ref runs));
        Assert.Equal(plan.Request, File.ReadAllBytes(Path.Combine(directory, "Request.json")));
        if (expectedState == "completed")
        {
            Assert.Equal(BackendExportStorage.Hash([7, 11, 13]), recovered.ArtifactSha256);
            File.WriteAllBytes(Path.Combine(directory, "Draft.mp4"), [7, 11, 17]);
            var failed = Status(await jobs.RecoverAsync(id, deadline.Token));
            Assert.Equal(("failed", "invalid_export_artifact", 3L), (failed.State, failed.ErrorCode, failed.Revision));
            Assert.Null(failed.ArtifactSha256);
        }
        else Assert.Null(recovered.ArtifactSha256);
    }

    [Fact]
    public async Task Shutdown_LeavesUnattestedWorkerProofForRecovery()
    {
        using var files = new ExportFixture();
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(15));
        var baseline = await files.MigrateAsync(deadline.Token);
        var durable = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var runs = 0;
        var bytes = new byte[] { 3, 7, 11 };
        string id;
        using (var jobs = new BackendExportJobs(files.Options, Plan, async (directory, hash, token) =>
        {
            Interlocked.Increment(ref runs);
            PutProof(directory, hash, bytes);
            durable.SetResult();
            await Task.Delay(Timeout.InfiniteTimeSpan, token);
        }))
        {
            id = Status(await StartAsync(jobs, baseline, deadline.Token)).Id;
            await durable.Task.WaitAsync(deadline.Token);
        }

        using var reopened = new BackendExportJobs(files.Options, Plan, (_, _, _) =>
        {
            Interlocked.Increment(ref runs);
            return Task.CompletedTask;
        });
        Assert.Equal("running", Status(await StartAsync(reopened, baseline, deadline.Token)).State);
        var completed = Status(await reopened.RecoverAsync(id, deadline.Token));
        Assert.Equal("completed", completed.State);
        Assert.Equal(BackendExportStorage.Hash(bytes), completed.ArtifactSha256);
        Assert.Equal(1, Volatile.Read(ref runs));
    }

    [Fact]
    public async Task Start_ChangedBaselineDuringPlanning_DoesNotAcceptOrRun()
    {
        using var files = new ExportFixture();
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(15));
        var baseline = await files.MigrateAsync(deadline.Token);
        var calls = 0;
        var runs = 0;
        using var jobs = new BackendExportJobs(files.Options, Plan, (_, _, _) =>
        {
            Interlocked.Increment(ref runs);
            return Task.CompletedTask;
        });
        await Assert.ThrowsAsync<ProjectRevisionConflictException>(() => jobs.StartAsync(Key, "draft", baseline.Project.Revision,
            baseline.Fingerprint, _ => ++calls == 1 ? Task.FromResult(baseline) :
                Task.FromException<ProjectFileDocument>(new ProjectRevisionConflictException(baseline.Project.Revision, baseline.Project.Revision + 1)), deadline.Token));
        Assert.Equal(2, calls);
        Assert.Empty(Directory.GetFiles(files.ExportRoot, "Request.json", SearchOption.AllDirectories));
        Assert.Equal(0, Volatile.Read(ref runs));
    }

    private static Task<BackendExportReply> StartAsync(BackendExportJobs jobs, ProjectFileDocument document, CancellationToken token) =>
        jobs.StartAsync(Key, "draft", document.Project.Revision, document.Fingerprint, _ => Task.FromResult(document), token);

    private static Task<BackendExportPlan> Plan(ProjectFileDocument document, string profile, string directory, CancellationToken token) =>
        Task.FromResult(new BackendExportPlan(JsonSerializer.SerializeToUtf8Bytes(new
        {
            version = 1, project_revision = document.Project.Revision, project_fingerprint = document.Fingerprint, profile
        }), Frames, Fps));

    private static void PutProof(string directory, string requestSha256, byte[] content)
    {
        File.WriteAllBytes(Path.Combine(directory, "Draft.mp4"), content);
        File.WriteAllText(Path.Combine(directory, "Result.json"), JsonSerializer.Serialize(new
        {
            state = "completed", output = "Draft.mp4", request_sha256 = requestSha256, sha256 = BackendExportStorage.Hash(content),
            video_info = new { frames = Frames, fps = Fps }
        }));
    }

    private static BackendExportStatus Status(BackendExportReply reply) =>
        JsonSerializer.Deserialize<BackendExportStatus>(reply.JobJson, BackendExportStorage.Json)!;

    private static async Task<BackendExportStatus> UntilAsync(BackendExportJobs jobs, string id, string state, CancellationToken token)
    {
        while (true)
        {
            var status = Status(await jobs.StatusAsync(id, token));
            if (status.State == state) return status;
            Assert.Contains(status.State, new[] { "running", "stopping" });
            await Task.Delay(10, token);
        }
    }

    private static async Task ExpectCode(string code, Func<Task<BackendExportReply>> call) =>
        Assert.Equal(code, (await Assert.ThrowsAsync<BackendExportException>(call)).Code);

    private sealed class ExportFixture : IDisposable
    {
        private readonly BackendWriteFixture _files = new();
        internal string ProjectPath => _files.Files.ProjectPath;
        internal string[] History => _files.Files.HistoryFiles;
        internal string ExportRoot => Path.Combine(_files.Files.Root, "exports");
        internal BackendOptions Options => _files.Options;

        internal ExportFixture()
        {
            var config = JsonNode.Parse(File.ReadAllText(Options.ConfigPath))!;
            config["projects_root"] = _files.Files.ProjectDirectory;
            config["exports_root"] = ExportRoot;
            File.WriteAllText(Options.ConfigPath, config.ToJsonString());
        }

        internal async Task<ProjectFileDocument> MigrateAsync(CancellationToken token)
        {
            var store = _files.Files.Store();
            var legacy = await store.OpenAsync(token);
            return await store.MigrateAsync(legacy, legacy.Project.Revision, "test", token);
        }

        public void Dispose() => _files.Dispose();
    }
}
