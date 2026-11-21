using System.Security.Cryptography;
using System.Text.Json;
using SkiaSharp;
using TrueMoon.Alloy.Rendering.Skia;

namespace TrueMoon.Tales.Studio.Backend;

internal static partial class BackendFullTransfer
{
    private sealed record WorkerOutput(string Path, string Sha256, long Bytes);

    internal static async Task<byte[]> RecordDispatchAsync(string scratch, string id, CancellationToken token)
    {
        using var graph = JsonDocument.Parse(ReadFile(scratch, "Prepared.api.json"));
        using var workflow = JsonDocument.Parse(ReadFile(scratch, "Workflow.ui.json"));
        var payload = JsonSerializer.SerializeToUtf8Bytes(new
        {
            prompt = graph.RootElement,
            client_id = "studio-" + id,
            extra_data = new { extra_pnginfo = new { workflow = workflow.RootElement } }
        });
        // Record the attempt before crossing the HTTP boundary. A lost reply is never retried.
        await WriteNewAsync(Path.Combine(scratch, "Dispatch.json"), payload, token).ConfigureAwait(false);
        return payload;
    }

    internal static async Task FinishWorkerAsync(string scratch, string buildSha, JsonElement build, JsonElement history,
        BackendMediaDecoder decoder, CancellationToken token)
    {
        await WriteNewAsync(Path.Combine(scratch, "History.json"), JsonSerializer.SerializeToUtf8Bytes(history), token).ConfigureAwait(false);
        if (history.GetProperty("status").GetProperty("status_str").GetString() != "success")
            throw new InvalidDataException("ComfyUI did not report a successful generation.");
        using var submitted = JsonDocument.Parse(ReadFile(scratch, "Submission.json"));
        if (submitted.RootElement.GetProperty("submissions").GetInt32() != 1)
            throw new InvalidDataException("Expected exactly one Full Transfer submission.");
        using var runtime = JsonDocument.Parse(ReadFile(scratch, "Runtime.json"));
        var diagnostics = runtime.RootElement;
        var forwards = diagnostics.GetProperty("h3_forwards");
        var sage = diagnostics.GetProperty("sage_success").GetInt32();
        var sdpa = diagnostics.GetProperty("sdpa_calls").GetInt32();
        if (sage <= 0 || sdpa != 0 || forwards.GetArrayLength() == 0 || forwards.EnumerateArray().Any(row =>
            row.GetProperty("output_finite").GetArrayLength() == 0 ||
            row.GetProperty("output_finite").EnumerateArray().Any(value => !value.GetBoolean())))
            throw new InvalidDataException("Invalid Full Transfer attention or finite output diagnostics.");

        var outputs = history.GetProperty("outputs");
        var video = OutputPath(scratch, outputs, "92", ".mp4");
        var still = OutputPath(scratch, outputs, "202", ".png");
        await using var videoFile = OpenResult(video, 512L * 1024 * 1024);
        await using var stillFile = OpenResult(still, 32 * 1024 * 1024);
        var frames = build.GetProperty("frames").GetInt32();
        if (await decoder.CheckTransferVideoAsync(video, frames, Width, Height, token).ConfigureAwait(false))
            throw new InvalidDataException("The generated MP4 must be silent.");
        SkiaNativeLibrary.Initialize();
        using (var codec = SKCodec.Create(still))
        {
            if (codec is null || !BackendAdaptation.ValidStill(codec) || codec.EncodedFormat != SKEncodedImageFormat.Png ||
                codec.Info.Width != Width || codec.Info.Height != Height)
                throw new InvalidDataException("Invalid Full Transfer final PNG.");
        }

        var files = new Dictionary<string, WorkerOutput>
        {
            ["92.0"] = await SnapshotAsync(video, videoFile, token).ConfigureAwait(false),
            ["202.0"] = await SnapshotAsync(still, stillFile, token).ConfigureAwait(false)
        };
        var result = JsonSerializer.SerializeToUtf8Bytes(new
        {
            id = build.GetProperty("id"), profile = "full-transfer", outputs = files, frames, fps = Fps,
            size = new[] { Width, Height }, silent = true, sage_success = sage, sdpa_calls = sdpa, all_h3_finite = true,
            motion_video = build.GetProperty("motion_video"), generation_executed = true, production_accepted = false
        }, BackendAdaptation.Json);
        await WriteNewAsync(Path.Combine(scratch, "Result.json"), result, token).ConfigureAwait(false);
        var completion = JsonSerializer.SerializeToUtf8Bytes(new Completion(buildSha, Hash(result)), BackendAdaptation.Json);
        await PublishNewAsync(Path.Combine(scratch, "Completed.json"), Path.Combine(scratch, "Completed.json.pending"),
            stream => WriteAsync(stream, completion, token), token).ConfigureAwait(false);
    }

    private static string OutputPath(string scratch, JsonElement outputs, string node, string extension)
    {
        var images = outputs.GetProperty(node).GetProperty("images");
        if (images.GetArrayLength() != 1) throw new InvalidDataException("Expected one output per Full Transfer node.");
        var row = images[0];
        var name = row.GetProperty("filename").GetString();
        var folder = row.GetProperty("subfolder").GetString();
        if (string.IsNullOrEmpty(name) || name != Path.GetFileName(name) || name.Contains(':') ||
            !string.Equals(Path.GetExtension(name), extension, StringComparison.OrdinalIgnoreCase) ||
            folder is null || Path.IsPathRooted(folder) || folder.Contains(':'))
            throw new InvalidDataException("Invalid Full Transfer output path.");
        var root = Path.Combine(scratch, "output");
        var path = Path.GetFullPath(Path.Combine(root, folder, name));
        var relative = Path.GetRelativePath(root, path);
        if (Path.IsPathRooted(relative) || relative == ".." || relative.StartsWith(".." + Path.DirectorySeparatorChar, PathComparison))
            throw new InvalidDataException("Full Transfer output is outside the job directory.");
        BackendMediaResolver.CheckNoLinks(path);
        return path;
    }

    private static FileStream OpenResult(string path, long limit)
    {
        var file = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, 64 * 1024, FileOptions.Asynchronous);
        if (file.Length > 0 && file.Length <= limit) return file;
        file.Dispose();
        throw new InvalidDataException("Invalid Full Transfer output size.");
    }

    private static async Task<WorkerOutput> SnapshotAsync(string path, FileStream file, CancellationToken token) =>
        new(path, Convert.ToHexString(await SHA256.HashDataAsync(file, token).ConfigureAwait(false)), file.Length);
}
