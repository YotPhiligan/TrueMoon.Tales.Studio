using System.Text.Json;
using System.Text.Json.Nodes;
using TrueMoon.Tales.Studio.Contracts.Projects;
using TrueMoon.Tales.Studio.Persistence;

namespace TrueMoon.Tales.Studio.Backend;

internal sealed record FullTransferRunState(int Version, string Id, string DraftTakeId, string RequestSha256,
    string State, string? OutputSha256, string? Detail);

internal static partial class BackendFullTransfer
{
    internal sealed record RunJob(string Id, ProjectStoreOptions Project, string Directory, FullTransferRequest Request,
        FullTransferReceipt Receipt, bool Staged)
    {
        internal string Run => Path.Combine(Directory, "Run");
    }

    private sealed record Intent(int Version, string RequestSha256, string StagedSha256, string BuildSha256,
        string RunnerSha256, DateTimeOffset StartedAt);
    private sealed record Completion(string BuildSha256, string ResultSha256);

    internal static RunJob OpenRun(BackendOptions options, ProjectContract project, string id, string requestSha)
    {
        if (!ValidJobId(id) || !BackendConnection.IsHash(requestSha)) throw Invalid();
        var paths = BackendProjectAccess.Read(options);
        var path = Path.Combine(paths.ProjectDirectory, JobReference(id));
        var (request, receipt, staged) = ReadJob(path, id, project.Id, requestSha);
        BackendMediaResolver.CheckNoLinks(Path.Combine(path, "Run"), allowMissing: true);
        return new(id, paths, path, request, receipt, staged);
    }

    internal static async Task<string> FreezeRunAsync(RunJob job, BackendFullTransferWorker worker, string runner,
        CancellationToken token)
    {
        if (!job.Staged) throw new ScenarioEditException("transfer_not_staged");
        BackendProjectAccess.CheckProductionWrite(job.Project, job.Run);
        if (Path.Exists(job.Run)) throw new ScenarioEditException("transfer_already_launched");
        var preview = Path.Combine(job.Directory, "Preview");
        var build = ReadFile(preview, "Build.json");
        if (Hash(build) != job.Receipt.BuildSha256) throw Damaged();
        var manifest = JsonNode.Parse(build)!.AsObject();
        var artifacts = PreviewFiles.ToDictionary(name => name, name => ReadFile(preview, name));
        foreach (var (name, bytes) in artifacts)
            if (!string.Equals(Hash(bytes), (string?)manifest["file_hashes"]![name], StringComparison.OrdinalIgnoreCase)) throw Damaged();
        using var settings = JsonDocument.Parse(artifacts["Job.json"]);
        if (!string.Equals(settings.RootElement.GetProperty("runtime").GetProperty("comfy_root").GetString(),
                worker.ComfyRoot, PathComparison)) throw new ScenarioEditException("transfer_stage_path_changed");
        var staged = ReadFile(job.Directory, "Staged.json");
        if (JsonSerializer.Deserialize<FullTransferReceipt>(staged, FormJson) != (job.Receipt with { State = "staged", Staged = true }))
            throw Damaged();

        manifest["staged"] = true;
        var runBuild = JsonSerializer.SerializeToUtf8Bytes(manifest);
        var buildSha = Hash(runBuild);
        var intent = new Intent(1, job.Receipt.RequestSha256, Hash(staged), buildSha,
            Hash(ReadFile(Path.GetDirectoryName(runner)!, Path.GetFileName(runner))), DateTimeOffset.UtcNow);
        var pending = Path.Combine(job.Directory, $".Run-{Guid.NewGuid():N}.pending");
        BackendProjectAccess.CheckProductionWrite(job.Project, pending);
        Directory.CreateDirectory(pending);
        try
        {
            foreach (var (name, bytes) in artifacts)
                await WriteNewAsync(Path.Combine(pending, name), bytes, token).ConfigureAwait(false);
            await WriteNewAsync(Path.Combine(pending, "Build.json"), runBuild, token).ConfigureAwait(false);
            await WriteNewAsync(Path.Combine(pending, "Launch.json"),
                JsonSerializer.SerializeToUtf8Bytes(intent, BackendAdaptation.Json), token).ConfigureAwait(false);
            token.ThrowIfCancellationRequested();
            Directory.Move(pending, job.Run);
        }
        finally
        {
            if (Directory.Exists(pending))
            {
                foreach (var name in PreviewFiles.Concat(["Build.json", "Launch.json"]))
                {
                    var path = Path.Combine(pending, name);
                    BackendProjectAccess.CheckProductionWrite(job.Project, path);
                    File.Delete(path);
                }
                Directory.Delete(pending);
            }
        }
        return buildSha;
    }

    internal static string RunBuildSha(RunJob job)
    {
        var intent = JsonSerializer.Deserialize<Intent>(ReadFile(job.Run, "Launch.json"), FormJson);
        if (!job.Staged || intent is not { Version: 1 } ||
            intent.RequestSha256 != job.Receipt.RequestSha256 ||
            intent.StagedSha256 != Hash(ReadFile(job.Directory, "Staged.json")) ||
            intent.BuildSha256 != Hash(ReadFile(job.Run, "Build.json")) || !BackendConnection.IsHash(intent.RunnerSha256))
            throw Damaged();
        return intent.BuildSha256;
    }

    internal static (string Path, string Sha) RunOutput(RunJob job)
    {
        var completion = JsonSerializer.Deserialize<Completion>(ReadFile(job.Run, "Completed.json"), FormJson);
        var bytes = ReadFile(job.Run, "Result.json");
        if (completion is null || !string.Equals(completion.BuildSha256, RunBuildSha(job), StringComparison.OrdinalIgnoreCase) ||
            !string.Equals(completion.ResultSha256, Hash(bytes), StringComparison.OrdinalIgnoreCase)) throw Damaged();
        using var json = JsonDocument.Parse(bytes);
        var result = json.RootElement;
        if (result.GetProperty("id").GetString() != job.Id || result.GetProperty("profile").GetString() != "full-transfer" ||
            result.GetProperty("frames").GetInt32() != job.Receipt.Frames || result.GetProperty("fps").GetInt32() != Fps ||
            !result.GetProperty("size").EnumerateArray().Select(row => row.GetInt32()).SequenceEqual([Width, Height]) ||
            !result.GetProperty("silent").GetBoolean() || !result.GetProperty("generation_executed").GetBoolean() ||
            result.GetProperty("production_accepted").GetBoolean()) throw Damaged();
        var output = result.GetProperty("outputs").GetProperty("92.0");
        var path = output.GetProperty("path").GetString();
        var sha = output.GetProperty("sha256").GetString();
        if (string.IsNullOrEmpty(path) || !Path.IsPathFullyQualified(path) || !BackendConnection.IsHash(sha) ||
            !string.Equals(Path.GetExtension(path), ".mp4", StringComparison.OrdinalIgnoreCase)) throw Damaged();
        path = Path.GetFullPath(path);
        var relative = Path.GetRelativePath(Path.Combine(job.Run, "output"), path);
        if (Path.IsPathRooted(relative) || relative == ".." || relative.StartsWith(".." + Path.DirectorySeparatorChar, PathComparison))
            throw Damaged();
        BackendMediaResolver.CheckNoLinks(path);
        if (new FileInfo(path).Length != output.GetProperty("bytes").GetInt64()) throw Damaged();
        return (path, sha!);
    }

    internal static async Task<T> CollectAsync<T>(RunJob job, ProjectContract project, BackendMediaReader reader,
        BackendMediaResolver resolver, Func<ProjectContract, Task<T>> commit, CancellationToken token)
    {
        var (path, sha) = RunOutput(job);
        if (!string.Equals(resolver.Resolve(path), path, PathComparison)) throw Damaged();
        var assetId = "video-" + job.Id;
        var takeId = FinalTakeId(job.Id);
        var source = project.Sources.FirstOrDefault(row => row.Id == job.Request.SourceId) ??
            throw new ScenarioEditException("transfer_input_changed");
        var input = source.Takes.FirstOrDefault(row => row.Id == job.Request.Draft.TakeId);
        if (input?.VideoAssetId != job.Request.Draft.AssetId) throw Changed();
        await using var finalFile = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, 64 * 1024, FileOptions.Asynchronous);
        if (finalFile.Length is < 1 or > 512L * 1024 * 1024 ||
            !await HasHashAsync(finalFile, sha, token).ConfigureAwait(false)) throw Damaged();
        var final = source.Takes.FirstOrDefault(row => row.Id == takeId);
        if (final is not null)
        {
            var pair = BackendTakePairs.Read(final);
            if (final.VideoAssetId != assetId || pair?.Draft != job.Request.Draft ||
                !string.Equals(pair.Final.Sha256, sha, StringComparison.OrdinalIgnoreCase) ||
                BackendScenarioEdit.TryReference(project, assetId) != path) throw Damaged();
            return await commit(project).ConfigureAwait(false);
        }
        if (project.Sources.Any(row => row.Takes.Any(take => take.Id == takeId || take.VideoAssetId == assetId)) ||
            BackendScenarioEdit.TryReference(project, assetId) is not null) throw Damaged();
        var draftPath = resolver.Resolve(job.Request.Draft.Reference);
        await using var draftFile = new FileStream(draftPath, FileMode.Open, FileAccess.Read, FileShare.Read);
        var actual = await reader.SnapshotTakeAsync(project, input!, token).ConfigureAwait(false);
        if ((actual with { Name = job.Request.Draft.Name }) != job.Request.Draft) throw Changed();
        if (string.Equals(actual.Sha256, sha, StringComparison.OrdinalIgnoreCase)) throw Damaged();
        var info = await reader.ProbeReferenceAsync(path, token).ConfigureAwait(false);
        if ((info.FrameCount, info.Fps, info.Width, info.Height) != (job.Receipt.Frames, Fps, Width, Height))
            throw new ScenarioEditException("transfer_timing");
        const string name = "Full Transfer";
        var snapshot = new TakeVideoSnapshot(takeId, name, assetId, path, sha.ToUpperInvariant(), Fps, info.FrameCount, Width, Height);
        var take = BackendTakePairs.WithPair(new TakeContract
        {
            Id = takeId, VideoAssetId = assetId,
            Properties = new Dictionary<string, JsonElement>
            {
                ["name"] = JsonSerializer.SerializeToElement(name),
                ["studio_full_transfer_v1"] = JsonSerializer.SerializeToElement(new
                {
                    job_id = job.Id, request_sha256 = job.Receipt.RequestSha256, build_sha256 = RunBuildSha(job)
                })
            }
        }, new DraftFinalPair(BackendTakePairs.FullTransfer, job.Request.Draft, snapshot));
        var edited = BackendScenarioEdit.SetReference(project with
        {
            Sources = project.Sources.Select(row => row.Id == source.Id ? row with { Takes = [..row.Takes, take] } : row).ToArray()
        }, assetId, path);
        return await commit(edited).ConfigureAwait(false);
    }

    private static ScenarioEditException Damaged() => new("transfer_damaged");

    internal static string FinalTakeId(string jobId) => "take-" + jobId;

    internal static Task WriteRunEndAsync<T>(RunJob job, T end) => PublishNewAsync(Path.Combine(job.Run, "End.json"),
        Path.Combine(job.Run, $".End-{Guid.NewGuid():N}.pending"),
        file => WriteAsync(file, JsonSerializer.SerializeToUtf8Bytes(end, BackendAdaptation.Json), CancellationToken.None),
        CancellationToken.None);
}
