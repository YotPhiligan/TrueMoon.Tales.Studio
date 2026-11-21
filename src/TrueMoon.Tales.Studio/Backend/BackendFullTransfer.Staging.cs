using System.Security.Cryptography;
using System.Text.Json;
using TrueMoon.Tales.Studio.Configuration;
using TrueMoon.Tales.Studio.Persistence;

namespace TrueMoon.Tales.Studio.Backend;

internal static partial class BackendFullTransfer
{
    private static readonly StringComparison PathComparison = OperatingSystem.IsWindows()
        ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;

    internal static bool ValidJobId(string? id) => id is { Length: 35 } && id.StartsWith("ft-", StringComparison.Ordinal) && ValidClientKey(id[3..]);

    internal static async Task<FullTransferReceipt> OpenOrStageAsync(BackendOptions options, ProjectFileDocument baseline,
        string id, string? expectedRequestSha, Func<CancellationToken, Task> confirmBaseline, CancellationToken token,
        bool forLaunch = false, string? scratchInput = null)
    {
        if (!ValidJobId(id) || expectedRequestSha is not null && !BackendConnection.IsHash(expectedRequestSha)) throw Invalid();
        var project = BackendProjectAccess.Read(options);
        var job = JobDirectory(project.ProjectDirectory, id);
        if (expectedRequestSha is not null) CheckJobWrite(project, job);
        var (request, receipt, alreadyStaged) = ReadJob(job, id, baseline.Project.Id, expectedRequestSha);
        if (expectedRequestSha is null)
        {
            await confirmBaseline(token).ConfigureAwait(false);
            return alreadyStaged ? receipt with { State = "staged", Staged = true } : receipt;
        }
        if (request.Revision != baseline.Project.Revision || request.Fingerprint != baseline.Fingerprint)
            throw new ScenarioEditException("revision_conflict");
        var take = baseline.Project.Sources.FirstOrDefault(source => source.Id == request.SourceId)?.Takes
            .FirstOrDefault(take => take.Id == request.Draft.TakeId);
        if (take?.VideoAssetId is not { } asset || asset != request.Draft.AssetId ||
            BackendScenarioEdit.TryReference(baseline.Project, asset) != request.Draft.Reference)
            throw new ScenarioEditException("transfer_damaged");
        if (forLaunch && !alreadyStaged) throw new ScenarioEditException("transfer_not_staged");
        var staged = receipt with { State = "staged", Staged = true };
        var stagePath = Path.Combine(job, "Staged.json");
        if (!forLaunch && Directory.Exists(Path.Combine(job, "Run")))
            throw new ScenarioEditException("transfer_already_launched");
        var inputDirectory = ResolveInputDirectory(options.ConfigPath, project.ProtectedRoots);
        var resolver = new BackendMediaResolver(options);
        var buildBytes = ReadFile(Path.Combine(job, "Preview"), "Build.json");
        if (Hash(buildBytes) != receipt.BuildSha256) throw new ScenarioEditException("transfer_damaged");
        using var build = JsonDocument.Parse(buildBytes);
        var rows = build.RootElement.GetProperty("source_input_hashes").EnumerateArray().ToArray();
        var motion = new FullTransferInput(request.Draft.Reference, "Video 1 / " + request.Form.MotionRole, request.Draft.Sha256);
        FullTransferInput[] inputs = [..request.Images, motion];
        if (rows.Length != inputs.Length) throw new ScenarioEditException("transfer_damaged");
        var locked = new List<StageInput>();
        try
        {
            // Inspect every source and any existing target before publishing the first copy.
            for (var index = 0; index < inputs.Length; index++)
                locked.Add(await OpenStageInputAsync(resolver, inputDirectory, id, index, inputs[index], rows[index], index == inputs.Length - 1, token)
                    .ConfigureAwait(false));
            await confirmBaseline(token).ConfigureAwait(false);
            if (scratchInput is not null)
            {
                BackendMediaResolver.CheckNoLinks(scratchInput, allowMissing: true);
                Directory.CreateDirectory(scratchInput);
                foreach (var input in locked)
                    await StageInputAsync(input with { Target = Path.Combine(scratchInput, Path.GetFileName(input.Target)) }, token).ConfigureAwait(false);
            }
            ReadReceipt(job, id, request, receipt.RequestSha256);
            await confirmBaseline(token).ConfigureAwait(false);
            if (!alreadyStaged)
            {
                CheckJobWrite(project, stagePath);
                var pending = Path.Combine(job, $".Staged-{Guid.NewGuid():N}.pending");
                await PublishNewAsync(stagePath, pending, stream => WriteAsync(stream,
                    JsonSerializer.SerializeToUtf8Bytes(staged, BackendAdaptation.Json), token), token).ConfigureAwait(false);
            }
            return staged;
        }
        finally { foreach (var input in locked) await input.Source.DisposeAsync().ConfigureAwait(false); }
    }

    private static (FullTransferRequest Request, FullTransferReceipt Receipt, bool Staged) ReadJob(
        string job, string id, string projectId, string? expectedRequestSha)
    {
        BackendMediaResolver.CheckNoLinks(job);
        var bytes = ReadFile(job, "Request.json");
        var sha = Hash(bytes);
        if (expectedRequestSha is not null && !string.Equals(sha, expectedRequestSha, StringComparison.OrdinalIgnoreCase))
            throw new ScenarioEditException("transfer_key_conflict");
        var request = JsonSerializer.Deserialize<FullTransferRequest>(bytes, FormJson);
        if (request is not { Version: 1, Draft: not null, Form: not null, Images: { Length: >= 1 and <= 8 } } ||
            request.ProjectId != projectId || JobId(request.Form.ClientKey) != id)
            throw new ScenarioEditException("transfer_damaged");
        var receipt = ReadReceipt(job, id, request, sha);
        var staged = receipt with { State = "staged", Staged = true };
        var stagePath = Path.Combine(job, "Staged.json");
        BackendMediaResolver.CheckNoLinks(stagePath, allowMissing: true);
        var alreadyStaged = File.Exists(stagePath);
        if (alreadyStaged && JsonSerializer.Deserialize<FullTransferReceipt>(ReadFile(job, "Staged.json"), FormJson) != staged)
            throw new ScenarioEditException("transfer_damaged");
        return (request, receipt, alreadyStaged);
    }

    private static string ResolveInputDirectory(string configPath, IEnumerable<string> protectedRoots)
    {
        if (WorkspaceProfile.Read(configPath).ComfyRoot is not { } root) throw new ScenarioEditException("transfer_unavailable");
        var directory = Path.Combine(WorkspaceProfile.ResolvePath(root, configPath), "input");
        foreach (var rootPath in protectedRoots)
        {
            var relative = Path.GetRelativePath(rootPath, directory);
            if (!Path.IsPathRooted(relative) && relative != ".." && !relative.StartsWith(".." + Path.DirectorySeparatorChar, PathComparison))
                throw new UnauthorizedAccessException("The configured ComfyUI input is protected.");
        }
        BackendMediaResolver.CheckNoLinks(directory);
        if (!Directory.Exists(directory)) throw new ScenarioEditException("transfer_unavailable");
        return directory;
    }

    private static async Task<StageInput> OpenStageInputAsync(BackendMediaResolver resolver, string directory, string id,
        int index, FullTransferInput input, JsonElement row, bool motion, CancellationToken token)
    {
        if (!BackendConnection.IsHash(input.Sha256) || row.GetProperty("role").GetString() != input.Role ||
            !string.Equals(row.GetProperty("SHA256").GetString(), input.Sha256, StringComparison.OrdinalIgnoreCase))
            throw new ScenarioEditException("transfer_damaged");
        var source = motion ? resolver.Resolve(input.Reference) : BackendAdaptation.ResolveStill(resolver, input.Reference);
        var suffix = Path.GetExtension(source).ToLowerInvariant();
        if (motion && suffix != ".mp4") throw Changed();
        var fileName = FormattableString.Invariant($"TMT.{id}.{index + 1}.{input.Sha256[..10].ToLowerInvariant()}{suffix}");
        var target = Path.Combine(directory, fileName);
        if (!string.Equals(target, row.GetProperty("Target").GetString(), PathComparison) ||
            !string.Equals(source, row.GetProperty("Source").GetString(), PathComparison))
            throw new ScenarioEditException("transfer_stage_path_changed");
        var stream = new FileStream(source, FileMode.Open, FileAccess.Read, FileShare.Read, 64 * 1024, FileOptions.Asynchronous);
        try
        {
            if (stream.Length is < 1 || stream.Length > (motion ? 512L : 32L) * 1024 * 1024 ||
                !await HasHashAsync(stream, input.Sha256, token).ConfigureAwait(false)) throw Changed();
            stream.Position = 0;
            return new(stream, target, input.Sha256);
        }
        catch { await stream.DisposeAsync().ConfigureAwait(false); throw; }
    }

    private static async Task StageInputAsync(StageInput input, CancellationToken token)
    {
        if (await CheckTargetAsync(input.Target, input.Sha, input.Source.Length, token).ConfigureAwait(false)) return;
        var pending = Path.Combine(Path.GetDirectoryName(input.Target)!, $".studio-ft-{Guid.NewGuid():N}.pending");
        try
        {
            await PublishNewAsync(input.Target, pending, async stream =>
            {
                await input.Source.CopyToAsync(stream, token).ConfigureAwait(false);
                stream.Flush(flushToDisk: true);
            }, token, async () =>
            {
                if (!await CheckTargetAsync(pending, input.Sha, input.Source.Length, token).ConfigureAwait(false)) throw Changed();
            }).ConfigureAwait(false);
        }
        catch (IOException) when (File.Exists(input.Target))
        {
            if (!await CheckTargetAsync(input.Target, input.Sha, input.Source.Length, token).ConfigureAwait(false)) throw;
        }
    }

    private static async Task<bool> CheckTargetAsync(string path, string sha, long length, CancellationToken token)
    {
        BackendMediaResolver.CheckNoLinks(path, allowMissing: true);
        if (Directory.Exists(path)) throw new ScenarioEditException("transfer_stage_conflict");
        if (!File.Exists(path)) return false;
        await using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, 64 * 1024, FileOptions.Asynchronous);
        if (stream.Length != length || !await HasHashAsync(stream, sha, token).ConfigureAwait(false))
            throw new ScenarioEditException("transfer_stage_conflict");
        return true;
    }

    private static async Task PublishNewAsync(string destination, string pending, Func<FileStream, Task> write, CancellationToken token,
        Func<Task>? verify = null)
    {
        var created = false;
        try
        {
            await using (var file = OpenNew(pending))
            {
                created = true;
                await write(file).ConfigureAwait(false);
            }
            if (verify is not null) await verify().ConfigureAwait(false);
            BackendMediaResolver.CheckNoLinks(destination, allowMissing: true);
            token.ThrowIfCancellationRequested();
            File.Move(pending, destination);
            created = false;
        }
        finally
        {
            if (created)
            {
                BackendMediaResolver.CheckNoLinks(pending, allowMissing: true);
                File.Delete(pending);
            }
        }
    }

    private static async Task<bool> HasHashAsync(Stream source, string expected, CancellationToken token) =>
        string.Equals(Convert.ToHexString(await SHA256.HashDataAsync(source, token).ConfigureAwait(false)), expected, StringComparison.OrdinalIgnoreCase);

    private sealed record StageInput(FileStream Source, string Target, string Sha);
}
