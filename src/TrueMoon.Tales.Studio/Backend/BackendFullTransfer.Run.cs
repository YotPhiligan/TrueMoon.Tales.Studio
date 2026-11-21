using System.Text.Json;
using System.Text.Json.Nodes;
using TrueMoon.Tales.Studio.Backend.Export;
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
        string RunnerSha256, DateTimeOffset StartedAt, string? Scratch = null);
    private sealed record Completion(string BuildSha256, string ResultSha256);
    private sealed record Publication(string BuildSha256, string Sha256, long Bytes);

    internal static RunJob OpenRun(BackendOptions options, ProjectContract project, string id, string requestSha)
    {
        if (!ValidJobId(id) || !BackendConnection.IsHash(requestSha)) throw Invalid();
        var paths = BackendProjectAccess.Read(options);
        var path = JobDirectory(paths.ProjectDirectory, id);
        var (request, receipt, staged) = ReadJob(path, id, project.Id, requestSha);
        BackendMediaResolver.CheckNoLinks(Path.Combine(path, "Run"), allowMissing: true);
        return new(id, paths, path, request, receipt, staged);
    }

    internal static async Task<string> FreezeRunAsync(RunJob job, BackendFullTransferWorker worker, string runner,
        string scratch, CancellationToken token)
    {
        if (!job.Staged) throw new ScenarioEditException("transfer_not_staged");
        CheckJobWrite(job.Project, job.Run);
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
            Hash(ReadFile(Path.GetDirectoryName(runner)!, Path.GetFileName(runner))), DateTimeOffset.UtcNow, Path.GetFileName(scratch));
        var pending = Path.Combine(job.Directory, $".Run-{Guid.NewGuid():N}.pending");
        CheckJobWrite(job.Project, pending);
        Directory.CreateDirectory(pending);
        try
        {
            foreach (var (name, bytes) in artifacts)
                await WriteNewAsync(Path.Combine(scratch, name), bytes, token).ConfigureAwait(false);
            var launch = JsonSerializer.SerializeToUtf8Bytes(intent, BackendAdaptation.Json);
            await WriteNewAsync(Path.Combine(scratch, "Build.json"), runBuild, token).ConfigureAwait(false);
            await WriteNewAsync(Path.Combine(scratch, "Launch.json"), launch, token).ConfigureAwait(false);
            await WriteNewAsync(Path.Combine(pending, "Build.json"), runBuild, token).ConfigureAwait(false);
            await WriteNewAsync(Path.Combine(pending, "Launch.json"), launch, token).ConfigureAwait(false);
            token.ThrowIfCancellationRequested();
            Directory.Move(pending, job.Run);
        }
        finally
        {
            if (Directory.Exists(pending))
            {
                foreach (var name in new[] { "Build.json", "Launch.json" })
                {
                    var path = Path.Combine(pending, name);
                    CheckJobWrite(job.Project, path);
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

    internal static bool HasPublishedOutput(RunJob job) => File.Exists(Path.Combine(job.Run, "Publication.json")) && File.Exists(PublishedPath(job));

    private static string PublishedPath(RunJob job) => Path.Combine(job.Project.ProjectDirectory, "video", $"FullTransfer.{job.Id}.mp4");
    private static string PublishedReference(RunJob job) => $"@project/video/FullTransfer.{job.Id}.mp4";

    internal static (string Path, string Sha) RunOutput(RunJob job)
    {
        if (!HasPublishedOutput(job)) return ReadWorkerOutput(job, job.Run);
        var publication = JsonSerializer.Deserialize<Publication>(ReadFile(job.Run, "Publication.json"), FormJson);
        if (publication is null || publication.BuildSha256 != RunBuildSha(job) || !BackendConnection.IsHash(publication.Sha256))
            throw Damaged();
        var video = PublishedPath(job);
        BackendMediaResolver.CheckNoLinks(video);
        if (new FileInfo(video).Length != publication.Bytes) throw Damaged();
        return (video, publication.Sha256);
    }

    internal static async Task PublishOutputAsync(RunJob job, string scratch, CancellationToken token)
    {
        var (path, sha) = ReadWorkerOutput(job, scratch);
        await using var source = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, 64 * 1024, FileOptions.Asynchronous);
        if (source.Length is < 1 or > 512L * 1024 * 1024 || !await HasHashAsync(source, sha, token).ConfigureAwait(false))
            throw Damaged();
        var destination = PublishedPath(job);
        var pending = Path.Combine(Path.GetDirectoryName(destination)!, $".{job.Id}.pending");
        BackendProjectAccess.CheckProjectSubdirectoryWrite(job.Project, "video", destination);
        BackendProjectAccess.CheckProjectSubdirectoryWrite(job.Project, "video", pending);
        Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
        var publication = new Publication(RunBuildSha(job), sha, source.Length);
        // Keep the evidence before publishing the file so a lost reply cannot lose the completed render.
        var receipt = Path.Combine(job.Run, "Publication.json");
        var receiptBytes = JsonSerializer.SerializeToUtf8Bytes(publication, BackendAdaptation.Json);
        if (File.Exists(receipt))
        {
            if (!ReadFile(job.Run, "Publication.json").AsSpan().SequenceEqual(receiptBytes)) throw Damaged();
            File.Delete(pending);
        }
        else
        {
            if (Path.Exists(pending)) throw new ScenarioEditException("transfer_stage_conflict");
            await PublishNewAsync(receipt, receipt + $".{Guid.NewGuid():N}.pending",
                stream => WriteAsync(stream, receiptBytes, token), token).ConfigureAwait(false);
        }
        if (await CheckTargetAsync(destination, sha, source.Length, token).ConfigureAwait(false)) return;
        source.Position = 0;
        await PublishNewAsync(destination, pending, async stream =>
        {
            await source.CopyToAsync(stream, token).ConfigureAwait(false);
            stream.Flush(flushToDisk: true);
        }, token, async () =>
        {
            if (!await CheckTargetAsync(pending, sha, source.Length, token).ConfigureAwait(false)) throw Damaged();
        }).ConfigureAwait(false);
    }

    internal static IEnumerable<RunJob> RecoverableRuns(ProjectStoreOptions paths, ProjectContract project)
    {
        var root = Path.Combine(paths.ProjectDirectory, ".local", Store);
        BackendMediaResolver.CheckNoLinks(root, allowMissing: true);
        if (!Directory.Exists(root)) yield break;
        foreach (var directory in Directory.EnumerateDirectories(root, "ft-*"))
        {
            var id = Path.GetFileName(directory);
            if (!ValidJobId(id) || !File.Exists(Path.Combine(directory, "Run", "Launch.json"))) continue;
            RunJob job;
            try
            {
                var (request, receipt, staged) = ReadJob(directory, id, project.Id, null);
                job = new(id, paths, directory, request, receipt, staged);
            }
            catch (Exception error) when (error is IOException or UnauthorizedAccessException or InvalidDataException
                or JsonException or ArgumentException or InvalidOperationException or KeyNotFoundException)
            { continue; }
            yield return job;
        }
    }

    internal static string? CompletedScratch(RunJob job)
    {
        RunBuildSha(job);
        var intent = JsonSerializer.Deserialize<Intent>(ReadFile(job.Run, "Launch.json"), FormJson)!;
        return StudioReviewSession.CompletedFullTransfer(intent.Scratch);
    }

    private static (string Path, string Sha) ReadWorkerOutput(RunJob job, string directory)
    {
        var completion = JsonSerializer.Deserialize<Completion>(ReadFile(directory, "Completed.json"), FormJson);
        var bytes = ReadFile(directory, "Result.json");
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
        var relative = Path.GetRelativePath(Path.Combine(directory, "output"), path);
        if (Path.IsPathRooted(relative) || relative == ".." || relative.StartsWith(".." + Path.DirectorySeparatorChar, PathComparison))
            throw Damaged();
        BackendMediaResolver.CheckNoLinks(path);
        if (new FileInfo(path).Length != output.GetProperty("bytes").GetInt64()) throw Damaged();
        return (path, sha!);
    }

    internal static async Task<T> CollectAsync<T>(RunJob job, ProjectContract project, BackendMediaReader reader,
        BackendMediaResolver resolver, Func<ProjectContract, Task<T>> commit, CancellationToken token)
    {
        if (BackendTakeRemoval.Find(project, job.Request.SourceId, FinalTakeId(job.Id)) is not null)
            throw new ScenarioEditException("transfer_take_removed");
        var (path, sha) = RunOutput(job);
        var reference = HasPublishedOutput(job) ? PublishedReference(job) : path;
        if (!string.Equals(resolver.Resolve(reference), path, PathComparison)) throw Damaged();
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
                BackendScenarioEdit.TryReference(project, assetId) != reference) throw Damaged();
            return await commit(project).ConfigureAwait(false);
        }
        if (project.Sources.Any(row => row.Takes.Any(take => take.Id == takeId || take.VideoAssetId == assetId)) ||
            BackendScenarioEdit.TryReference(project, assetId) is not null) throw Damaged();
        var draftPath = resolver.Resolve(job.Request.Draft.Reference);
        await using var draftFile = new FileStream(draftPath, FileMode.Open, FileAccess.Read, FileShare.Read);
        var actual = await reader.SnapshotTakeAsync(project, input!, token).ConfigureAwait(false);
        if ((actual with { Name = job.Request.Draft.Name }) != job.Request.Draft) throw Changed();
        if (string.Equals(actual.Sha256, sha, StringComparison.OrdinalIgnoreCase)) throw Damaged();
        var info = await reader.ProbeReferenceAsync(reference, token).ConfigureAwait(false);
        if ((info.FrameCount, info.Fps, info.Width, info.Height) != (job.Receipt.Frames, Fps, Width, Height))
            throw new ScenarioEditException("transfer_timing");
        const string name = DefaultTakeName;
        var snapshot = new TakeVideoSnapshot(takeId, name, assetId, reference, sha.ToUpperInvariant(), Fps, info.FrameCount, Width, Height);
        var take = BackendTakePairs.WithPair(new TakeContract
        {
            Id = takeId, VideoAssetId = assetId,
            Properties = new Dictionary<string, JsonElement>
            {
                ["name"] = JsonSerializer.SerializeToElement(name),
                [TakeOriginProperty] = JsonSerializer.SerializeToElement(new
                {
                    job_id = job.Id, request_sha256 = job.Receipt.RequestSha256, build_sha256 = RunBuildSha(job)
                })
            }
        }, new DraftFinalPair(BackendTakePairs.FullTransfer, job.Request.Draft, snapshot));
        var edited = BackendScenarioEdit.SetReference(project with
        {
            Sources = project.Sources.Select(row => row.Id == source.Id ? row with { Takes = [..row.Takes, take] } : row).ToArray()
        }, assetId, reference);
        return await commit(edited).ConfigureAwait(false);
    }

    private static ScenarioEditException Damaged() => new("transfer_damaged");

    internal static string FinalTakeId(string jobId) => "take-" + jobId;

    internal static Task WriteRunEndAsync<T>(RunJob job, T end) => PublishNewAsync(Path.Combine(job.Run, "End.json"),
        Path.Combine(job.Run, $".End-{Guid.NewGuid():N}.pending"),
        file => WriteAsync(file, JsonSerializer.SerializeToUtf8Bytes(end, BackendAdaptation.Json), CancellationToken.None),
        CancellationToken.None);
}
