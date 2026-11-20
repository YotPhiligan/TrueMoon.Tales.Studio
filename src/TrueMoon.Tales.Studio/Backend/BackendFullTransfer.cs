using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Serialization;
using SkiaSharp;
using TrueMoon.Tales.Studio.Contracts.Projects;
using TrueMoon.Tales.Studio.Persistence;

namespace TrueMoon.Tales.Studio.Backend;

internal sealed record FullTransferImage(string Reference, string Role);
internal sealed record FullTransferPredecessor(string Id, string RequestSha256);
internal sealed record FullTransferForm(string ClientKey, string SourceId, string DraftTakeId, ulong Seed, string Prompt,
    FullTransferImage[] Images, int GuidePicture, string MotionRole, string MotionNotes,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] FullTransferPredecessor? Predecessor = null);
internal sealed record FullTransferInput(string Reference, string Role, string Sha256);
internal sealed record FullTransferRequest(int Version, string ProjectId, long Revision, string Fingerprint, string SourceId,
    TakeVideoSnapshot Draft, FullTransferForm Form, FullTransferInput[] Images);
internal sealed record FullTransferReceipt(int Version, string Id, string State, long Revision, string Fingerprint, string DraftTakeId,
    string DraftSha256, string RequestSha256, string ConfigSha256, string BuildSha256, int Frames, int Fps, int Width, int Height,
    bool Staged, bool Enqueued);

internal static partial class BackendFullTransfer
{
    private const string Store = "FullTransfer.v1";
    private const int Width = 1280, Height = 736, Fps = 24;
    private static readonly string[] PreviewFiles = ["Job.json", "Prepared.api.json", "Workflow.ui.json", "Inputs.prepared.json", "Prompt.txt"];
    private static readonly string[] JobFiles = ["Request.json", "Prepare.config.json", "Prompt.txt", "Prepared.json.pending"];
    private static readonly JsonSerializerOptions FormJson = new(BackendAdaptation.Json) { RespectRequiredConstructorParameters = true };

    internal static FullTransferForm Decode(string json)
    {
        if (json is not { Length: > 0 and <= 64 * 1024 } ||
            JsonSerializer.Deserialize<FullTransferForm>(json, FormJson) is not { Images: { Length: >= 1 and <= 8 } } form ||
            !ValidClientKey(form.ClientKey) || form.GuidePicture < 1 || form.GuidePicture > form.Images.Length ||
            form.Predecessor is { } previous && (!ValidJobId(previous.Id) || !BackendConnection.IsHash(previous.RequestSha256) ||
                previous.Id == JobId(form.ClientKey)) ||
            form.Images.Any(image => image is null)) throw Invalid();
        return form with
        {
            SourceId = Required(form.SourceId, 160), DraftTakeId = Required(form.DraftTakeId, 160),
            Prompt = Required(form.Prompt, 12_000, multiline: true), MotionRole = Required(form.MotionRole, 200),
            MotionNotes = Required(form.MotionNotes, 1600, multiline: true),
            Images = form.Images.Select(image => new FullTransferImage(Required(image.Reference, 512), Required(image.Role, 200))).ToArray(),
            Predecessor = form.Predecessor is { } predecessor
                ? predecessor with { RequestSha256 = predecessor.RequestSha256.ToUpperInvariant() }
                : null
        };
    }

    internal static string JobId(string key) => "ft-" + key;
    internal static string JobReference(string id) => $"Production/{Store}/{id}";
    internal static bool ValidClientKey(string? key) => key is { Length: 32 } &&
        key.All(ch => ch is >= '0' and <= '9' or >= 'a' and <= 'f');

    internal static async Task<FullTransferReceipt> PrepareAsync(BackendOptions options, ProjectFileDocument baseline, FullTransferForm form,
        BackendMediaReader media, Func<CancellationToken, Task> confirmBaseline, CancellationToken token)
    {
        var source = baseline.Project.Sources.FirstOrDefault(row => row.Id == form.SourceId);
        var take = source?.Takes.FirstOrDefault(row => row.Id == form.DraftTakeId);
        if (take?.VideoAssetId is null || BackendTakePairs.Read(take) is not null) throw Invalid();
        var id = JobId(form.ClientKey);
        var project = BackendProjectAccess.Read(options);
        if (form.Predecessor is { } previous)
        {
            var previousJob = Path.Combine(project.ProjectDirectory, "Production", Store, previous.Id);
            var (previousRequest, _, _) = ReadJob(previousJob, previous.Id, baseline.Project.Id, previous.RequestSha256);
            if (previousRequest.SourceId != form.SourceId || previousRequest.Revision > baseline.Project.Revision)
                throw Invalid();
        }
        var job = Path.Combine(project.ProjectDirectory, "Production", Store, id);
        BackendProjectAccess.CheckProductionWrite(project, job);
        var draft = await media.SnapshotTakeAsync(baseline.Project, take, token).ConfigureAwait(false);
        if (draft.Fps != Fps || draft.Frames is < 56 or > 192 || (draft.Frames - 5) % 17 != 0 ||
            !string.Equals(Path.GetExtension(draft.Reference), ".mp4", StringComparison.OrdinalIgnoreCase))
            throw new ScenarioEditException("transfer_timing");
        if (source!.Takes.Select(BackendTakePairs.Read).Any(pair => pair?.Draft.TakeId == take.Id && pair.Draft.Sha256 != draft.Sha256))
            throw new ScenarioEditException("take_pair_changed");
        var resolver = new BackendMediaResolver(options);
        var pins = new List<Pin>();
        try
        {
            var motion = await PinAsync(draft.Reference, "Video 1 / " + form.MotionRole, image: false).ConfigureAwait(false);
            if (motion.Input.Sha256 != draft.Sha256) throw Changed();
            var images = new List<Pin>(form.Images.Length);
            var takeInputs = BackendReferenceLibrary.Inputs(take);
            foreach (var item in form.Images)
            {
                var image = await PinAsync(item.Reference, item.Role, image: true).ConfigureAwait(false);
                if (takeInputs.Any(input => input.Version.ImageReference == item.Reference && input.Version.Sha256 != image.Input.Sha256))
                    throw Changed();
                images.Add(image);
            }
            var request = new FullTransferRequest(1, baseline.Project.Id, baseline.Project.Revision, baseline.Fingerprint,
                source.Id, draft, form, images.Select(row => row.Input).ToArray());
            var bytes = JsonSerializer.SerializeToUtf8Bytes(request, BackendAdaptation.Json);
            var requestSha = Hash(bytes);
            if (Directory.Exists(job))
            {
                var existing = ReadReceipt(job, id, request, requestSha);
                await confirmBaseline(token).ConfigureAwait(false);
                return existing;
            }
            var worker = BackendFullTransferWorker.Read(options);
            await confirmBaseline(token).ConfigureAwait(false);
            Directory.CreateDirectory(job);
            var owned = false;
            var published = false;
            try
            {
                BackendProjectAccess.CheckProductionWrite(project, job);
                await using (var requestFile = OpenNew(Path.Combine(job, "Request.json")))
                {
                    owned = true;
                    await WriteAsync(requestFile, bytes, token).ConfigureAwait(false);
                }
                await WriteNewAsync(Path.Combine(job, "Prompt.txt"), System.Text.Encoding.UTF8.GetBytes(form.Prompt), token).ConfigureAwait(false);
                var config = Path.Combine(job, "Prepare.config.json");
                var configBytes = JsonSerializer.SerializeToUtf8Bytes(new
                {
                    version = 2, id, profile = "full-transfer", size = new[] { Width, Height }, fps = Fps,
                    seconds = draft.Frames / (double)Fps, seed = form.Seed, silent = true, prompt = "Prompt.txt",
                    references = images.Select(row => new { role = row.Input.Role, path = row.Path }),
                    guides = new[] { new { picture = form.GuidePicture, frame = 0 } },
                    motion_video = new { path = motion.Path, role = form.MotionRole, review_notes = form.MotionNotes },
                    runtime = new { comfy_root = worker.ComfyRoot }
                });
                await WriteNewAsync(config, configBytes, token).ConfigureAwait(false);
                var preview = Path.Combine(job, "Preview");
                await worker.RunAsync(config, preview, token).ConfigureAwait(false);
                foreach (var pin in pins)
                {
                    if (resolver.Resolve(pin.Input.Reference) != pin.Path) throw Changed();
                    pin.Stream.Position = 0;
                    if (Convert.ToHexString(await SHA256.HashDataAsync(pin.Stream, token).ConfigureAwait(false)) != pin.Input.Sha256) throw Changed();
                }
                var build = CheckBuild(preview, id, draft.Frames, [.. images, motion]);
                await confirmBaseline(token).ConfigureAwait(false);
                var receipt = new FullTransferReceipt(1, id, "prepared", baseline.Project.Revision, baseline.Fingerprint,
                    take.Id, draft.Sha256, requestSha, Hash(configBytes), build, draft.Frames, Fps, Width, Height, Staged: false, Enqueued: false);
                var pending = Path.Combine(job, "Prepared.json.pending");
                await WriteNewAsync(pending, JsonSerializer.SerializeToUtf8Bytes(receipt, BackendAdaptation.Json), token).ConfigureAwait(false);
                token.ThrowIfCancellationRequested();
                var ready = Path.Combine(job, "Prepared.json");
                BackendProjectAccess.CheckProductionWrite(project, ready);
                File.Move(pending, ready);
                published = true;
                return receipt;
            }
            finally { if (owned && !published) RemoveIncomplete(project, job); }
        }
        finally { foreach (var pin in pins) await pin.Stream.DisposeAsync().ConfigureAwait(false); }

        async Task<Pin> PinAsync(string reference, string role, bool image)
        {
            var path = image ? BackendAdaptation.ResolveStill(resolver, reference) : resolver.Resolve(reference);
            var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, 64 * 1024, FileOptions.Asynchronous);
            try
            {
                if (image)
                {
                    if (stream.Length is < 1 or > 32 * 1024 * 1024) throw Invalid();
                    using var codec = SKCodec.Create(path);
                    if (codec is null || !BackendAdaptation.ValidStill(codec)) throw Invalid();
                }
                if (stream.Length is < 1 or > 512L * 1024 * 1024) throw Invalid();
                var sha = Convert.ToHexString(await SHA256.HashDataAsync(stream, token).ConfigureAwait(false));
                var pin = new Pin(path, stream, new(reference, role, sha));
                pins.Add(pin);
                return pin;
            }
            catch { await stream.DisposeAsync().ConfigureAwait(false); throw; }
        }
    }

    private sealed record Pin(string Path, FileStream Stream, FullTransferInput Input);

    private static FullTransferReceipt ReadReceipt(string job, string id, FullTransferRequest request, string sha)
    {
        if (!File.Exists(Path.Combine(job, "Prepared.json"))) throw new ScenarioEditException("transfer_interrupted");
        if (Hash(ReadFile(job, "Request.json")) != sha) throw new ScenarioEditException("transfer_key_conflict");
        var receipt = JsonSerializer.Deserialize<FullTransferReceipt>(ReadFile(job, "Prepared.json"), BackendAdaptation.Json);
        if (receipt is not { Version: 1, State: "prepared", Staged: false, Enqueued: false } || receipt.Id != id ||
            receipt.Revision != request.Revision || receipt.Fingerprint != request.Fingerprint || receipt.DraftTakeId != request.Draft.TakeId ||
            receipt.DraftSha256 != request.Draft.Sha256 || receipt.RequestSha256 != sha || receipt.Frames != request.Draft.Frames ||
            (receipt.Fps, receipt.Width, receipt.Height) != (Fps, Width, Height) ||
            receipt.ConfigSha256 != Hash(ReadFile(job, "Prepare.config.json")) ||
            !ReadFile(job, "Prompt.txt").AsSpan().SequenceEqual(System.Text.Encoding.UTF8.GetBytes(request.Form.Prompt)) ||
            receipt.BuildSha256 != CheckBuild(Path.Combine(job, "Preview"), id, request.Draft.Frames, null))
            throw new ScenarioEditException("transfer_damaged");
        return receipt;
    }

    private static string CheckBuild(string preview, string id, int frames, Pin[]? inputs)
    {
        var bytes = ReadFile(preview, "Build.json");
        using var json = JsonDocument.Parse(bytes);
        var manifest = json.RootElement;
        if (manifest.GetProperty("version").GetInt32() != 2 || manifest.GetProperty("id").GetString() != id ||
            manifest.GetProperty("profile").GetString() != "full-transfer" || manifest.GetProperty("frames").GetInt32() != frames ||
            manifest.GetProperty("fps").GetInt32() != Fps || !manifest.GetProperty("silent").GetBoolean() ||
            manifest.GetProperty("staged").GetBoolean() || manifest.GetProperty("generation_executed").GetBoolean() ||
            !manifest.GetProperty("size").EnumerateArray().Select(row => row.GetInt32()).SequenceEqual([Width, Height]))
            throw new ScenarioEditException("transfer_damaged");
        var hashes = manifest.GetProperty("file_hashes");
        foreach (var name in PreviewFiles)
            if (!string.Equals(hashes.GetProperty(name).GetString(), Hash(ReadFile(preview, name)), StringComparison.OrdinalIgnoreCase))
                throw new ScenarioEditException("transfer_damaged");
        if (inputs is not null)
        {
            var rows = manifest.GetProperty("source_input_hashes").EnumerateArray().ToArray();
            if (rows.Length != inputs.Length || rows.Where((row, index) => row.GetProperty("role").GetString() != inputs[index].Input.Role ||
                !string.Equals(row.GetProperty("SHA256").GetString(), inputs[index].Input.Sha256, StringComparison.OrdinalIgnoreCase)).Any())
                throw Changed();
        }
        return Hash(bytes);
    }

    private static byte[] ReadFile(string directory, string name)
    {
        var path = Path.Combine(directory, name);
        BackendMediaResolver.CheckNoLinks(path);
        using var file = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
        if (file.Length is < 1 or > 1024 * 1024) throw new ScenarioEditException("transfer_damaged");
        var bytes = new byte[(int)file.Length];
        file.ReadExactly(bytes);
        return bytes;
    }

    private static async Task WriteNewAsync(string path, byte[] bytes, CancellationToken token)
    {
        await using var file = OpenNew(path);
        await WriteAsync(file, bytes, token).ConfigureAwait(false);
    }

    private static FileStream OpenNew(string path)
    {
        BackendMediaResolver.CheckNoLinks(path, allowMissing: true);
        return new(path, FileMode.CreateNew, FileAccess.Write, FileShare.None, 4096, FileOptions.Asynchronous);
    }

    private static async Task WriteAsync(FileStream file, byte[] bytes, CancellationToken token)
    {
        await file.WriteAsync(bytes, token).ConfigureAwait(false);
        file.Flush(flushToDisk: true);
    }

    private static void RemoveIncomplete(ProjectStoreOptions project, string job)
    {
        try
        {
            var preview = Path.Combine(job, "Preview");
            if (Directory.Exists(preview))
            {
                foreach (var name in PreviewFiles.Append("Build.json")) Delete(Path.Combine(preview, name));
                Directory.Delete(preview);
            }
            foreach (var name in JobFiles) Delete(Path.Combine(job, name));
            Directory.Delete(job);
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException) { /* No receipt means no prepared job. */ }

        void Delete(string path)
        {
            BackendProjectAccess.CheckProductionWrite(project, path);
            File.Delete(path);
        }
    }

    private static string Hash(byte[] bytes) => Convert.ToHexString(SHA256.HashData(bytes));
    private static string Required(string? value, int limit, bool multiline = false) => value?.Trim() is { Length: > 0 } text &&
        text.Length <= limit && !text.Any(ch => char.IsControl(ch) && (!multiline || ch is not ('\r' or '\n' or '\t')))
        ? (multiline ? text.ReplaceLineEndings("\n") : text) : throw Invalid();
    private static ScenarioEditException Invalid() => new("invalid_transfer");
    private static ScenarioEditException Changed() => new("transfer_input_changed");
}
