using System.Diagnostics;
using System.Globalization;
using System.Security.Cryptography;
using System.Text.Json;

namespace TrueMoon.Tales.Studio.Backend.Export;

internal sealed partial class BackendCpuRenderer
{
    private static readonly StringComparer Paths = OperatingSystem.IsWindows() ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal;
    private readonly Stopwatch _clock = Stopwatch.StartNew();
    private readonly BackendMediaDecoder _media;
    private readonly string directory;
    private readonly RenderRequest request;
    private readonly string ffmpeg;
    private readonly CancellationToken token;
    private long _lastProgress = -500;
    private int Width => request.Size[0];
    private int Height => request.Size[1];
    private int Samples => Sample(request.StartFrame + request.Frames) - Sample(request.StartFrame);

    private BackendCpuRenderer(string directory, RenderRequest request, string ffmpeg, string ffprobe, CancellationToken token)
    {
        this.directory = directory;
        this.request = request;
        this.ffmpeg = ffmpeg;
        this.token = token;
        _media = new(ffmpeg, ffprobe);
    }

    internal static async Task RenderAsync(string directory, string sha, string ffmpeg, string ffprobe, CancellationToken token)
    {
        var path = Path.Combine(directory, "Request.json");
        BackendMediaResolver.CheckNoLinks(path);
        byte[] data;
        await using (var source = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, 16384, FileOptions.Asynchronous))
        {
            if (source.Length is < 1 or > BackendExportStorage.MaxRequestBytes) throw Invalid();
            data = new byte[(int)source.Length];
            await source.ReadExactlyAsync(data, token).ConfigureAwait(false);
        }
        if (!BackendExportStorage.EqualHash(sha, BackendExportStorage.Hash(data))) throw Invalid();
        var plan = JsonSerializer.Deserialize<RenderRequest>(data, BackendExportStorage.Json) ?? throw Invalid();
        plan.Validate(directory);
        await new BackendCpuRenderer(directory, plan, ffmpeg, ffprobe, token).RunAsync(sha).ConfigureAwait(false);
    }

    private async Task RunAsync(string sha)
    {
        Progress("Проверка материалов", 0, force: true);
        var hashes = await HashInputsAsync().ConfigureAwait(false);
        WriteJson("InputHashes.json", hashes);
        var parts = Parts();
        await RenderVideoAsync(parts).ConfigureAwait(false);
        if (request.ClipAudio) await RenderNativeAudioAsync(parts).ConfigureAwait(false);
        Progress("Сборка MP4", 91, force: true);
        await MuxAsync().ConfigureAwait(false);
        Progress("Проверка результата", 97, force: true);
        await HashInputsAsync().ConfigureAwait(false);
        var artifact = JobFile("Draft.mp4");
        var info = await _media.ProbeAsync(artifact, token).ConfigureAwait(false);
        if (info.Width != Width || info.Height != Height || info.FrameCount != request.Frames || info.Fps != request.Fps ||
            !await _media.HasAudioAsync(artifact, 0, token).ConfigureAwait(false))
            throw new InvalidDataException("Exported streams do not match the timeline.");
        await using var stream = new FileStream(artifact, FileMode.Open, FileAccess.Read, FileShare.Read, 65536, FileOptions.Asynchronous);
        if (stream.Length is < 1 or > BackendExportJobs.MaxArtifactSize) throw Invalid();
        await RunFfmpegAsync(["-threads", "2", .. BackendMediaDecoder.Input(artifact),
            "-map", "0:v:0", "-map", "0:a:0", "-f", "null", "-"]).ConfigureAwait(false);
        var digest = Convert.ToHexStringLower(await SHA256.HashDataAsync(stream, token).ConfigureAwait(false));
        Progress("Готово", 100, force: true);
        WriteJson("Result.json", new
        {
            state = "completed", output = "Draft.mp4", sha256 = digest, request_sha256 = sha,
            video_info = new { frames = request.Frames, fps = request.Fps, seconds = request.Frames / (double)request.Fps, width = Width, height = Height, audio = true },
            elapsed_seconds = _clock.Elapsed.TotalSeconds
        });
    }

    private async Task<Dictionary<string, string>> HashInputsAsync()
    {
        var hashes = new Dictionary<string, string>(Paths);
        var result = new Dictionary<string, string>(StringComparer.Ordinal);
        long total = 0;
        foreach (var (id, asset) in request.Assets)
        {
            token.ThrowIfCancellationRequested();
            BackendMediaResolver.CheckNoLinks(asset.Path);
            if (!hashes.TryGetValue(asset.Path, out var hash))
            {
                await using var input = new FileStream(asset.Path, FileMode.Open, FileAccess.Read, FileShare.Read, 65536, FileOptions.Asynchronous);
                total = checked(total + input.Length);
                if (total > 2L * 1024 * 1024 * 1024) throw Invalid();
                hash = Convert.ToHexStringLower(await SHA256.HashDataAsync(input, token).ConfigureAwait(false));
                hashes.Add(asset.Path, hash);
            }
            if (!BackendExportStorage.EqualHash(hash, asset.ExpectedSha256))
                throw new InvalidDataException("An export input changed after the request was saved.");
            result.Add(id, hash);
        }
        return result;
    }

    private List<Part> Parts()
    {
        var end = request.StartFrame + request.Frames;
        var cursor = request.StartFrame;
        var parts = new List<Part>();
        foreach (var clip in request.Clips)
        {
            var left = Math.Max(request.StartFrame, clip.StartFrame);
            var right = Math.Min(end, clip.StartFrame + clip.DurationFrames);
            if (left >= right) continue;
            if (left > cursor) parts.Add(new(cursor, left, null));
            parts.Add(new(left, right, clip));
            cursor = right;
        }
        if (cursor < end) parts.Add(new(cursor, end, null));
        return parts;
    }

    private string JobFile(string name, bool allowMissing = false)
    {
        var path = Path.Combine(directory, name);
        BackendMediaResolver.CheckNoLinks(path, allowMissing);
        return path;
    }

    private void Progress(string stage, double percent, bool force = false)
    {
        token.ThrowIfCancellationRequested();
        if (!force && _clock.ElapsedMilliseconds - _lastProgress < 500) return;
        _lastProgress = _clock.ElapsedMilliseconds;
        try { WriteJson("Progress.json", new { stage, percent, elapsed_seconds = _clock.Elapsed.TotalSeconds }, replace: true); }
        catch (IOException) { } // A transient sharing failure on telemetry must not change a job's outcome.
    }

    private void WriteJson(string name, object value, bool replace = false)
    {
        var staged = JobFile("worker-" + Guid.NewGuid().ToString("N") + ".tmp", allowMissing: true);
        try
        {
            using (var stream = new FileStream(staged, FileMode.CreateNew, FileAccess.Write, FileShare.None))
            {
                JsonSerializer.Serialize(stream, value, BackendExportStorage.Json);
                stream.Flush(flushToDisk: true);
            }
            token.ThrowIfCancellationRequested();
            File.Move(staged, JobFile(name, allowMissing: true), replace);
        }
        finally { File.Delete(staged); }
    }

    private string AssetPath(string id)
    {
        var asset = request.Assets[id].Path;
        BackendMediaResolver.CheckNoLinks(asset);
        return asset;
    }

    private int Sample(int frame) => (int)Math.Round(frame / (double)request.Fps * 48000);
    private static string Number(double value) => value.ToString("0.############", CultureInfo.InvariantCulture);
    private static InvalidDataException Invalid() => new("Invalid frozen CPU export request.");

    internal sealed record RenderRequest(int Version, bool BackendMode, string Book, RenderPolicy PathPolicy, int Fps, int Frames,
        int StartFrame, int[] Size, RenderEncoding Encoding, RenderClip[] Clips, BlackTransition[] TransitionWindows,
        BackendExportPlan.MusicPlacement? Music, bool ClipAudio, BackendExportPlan.SoundPlacement[] Sounds,
        RenderTitle[] Titles, int[] TitleCanvas, Dictionary<string, RenderAsset> Assets, RenderStill[] AnimaticStills)
    {
        internal void Validate(string directory)
        {
            if (Version != 1 || !BackendMode || !Paths.Equals(Book, directory) || PathPolicy is null ||
                PathPolicy.WriteRoots is not [var root] || !Paths.Equals(root, directory) ||
                PathPolicy.ReadRoots is null || PathPolicy.ProtectedRoots is not { Length: 0 } ||
                Fps is < 1 or > 120 || StartFrame < 0 || Frames < 1 || (long)StartFrame + Frames > Fps * 600 ||
                !Dimensions(Size, even: true) || !Dimensions(TitleCanvas, even: false) ||
                Encoding is not { VideoCodec: "libx264", PixelFormat: "yuv420p", Preset: "veryfast" or "medium", Crf: 18 or 25,
                    AudioCodec: "aac", AudioBitrateKbps: 192 or 320 } ||
                Assets is not { Count: <= 128 } || Clips is not { Length: <= 2000 } || TransitionWindows is not { Length: <= 2000 } ||
                Sounds is not { Length: <= 64 } || Titles is not { Length: <= 2048 } || AnimaticStills is not { Length: <= 2000 })
                throw Invalid();

            var roots = new HashSet<string>(PathPolicy.ReadRoots, Paths);
            foreach (var asset in Assets.Values)
            {
                if (asset is null || !Path.IsPathFullyQualified(asset.Path) || !Paths.Equals(Path.GetFullPath(asset.Path), asset.Path) ||
                    !roots.Contains(asset.Path) || !BackendExportStorage.ValidHash(asset.ExpectedSha256))
                    throw Invalid();
                if (asset.Format is not ("font" or "image") && BackendMediaDecoder.LocalInputFormat(asset.Path, audio: true) != asset.Format)
                    throw Invalid();
                if (asset.Format is "font" && !Path.GetExtension(asset.Path).Equals(".ttf", StringComparison.OrdinalIgnoreCase) ||
                    asset.Format is "image" && Path.GetExtension(asset.Path).ToLowerInvariant() is not (".png" or ".jpg" or ".jpeg"))
                    throw Invalid();
            }
            var cursor = 0;
            foreach (var clip in Clips)
            {
                if (clip is null || clip.StartFrame < cursor || !Timing(clip.StartFrame, clip.DurationFrames) ||
                    !IsSource(clip.AssetId, "mov", "matroska", "avi") || !double.IsFinite(clip.Speed) || clip.Speed is < .1 or > 10 ||
                    !double.IsFinite(clip.SourceInFrame) || clip.SourceInFrame < 0 || !double.IsFinite(clip.SourceOutFrame) ||
                    clip.SourceOutFrame <= clip.SourceInFrame)
                    throw Invalid();
                cursor = clip.StartFrame + clip.DurationFrames;
            }
            foreach (var image in AnimaticStills)
                if (image is null || !Timing(image.StartFrame, image.DurationFrames) || !IsSource(image.AssetId, "image"))
                    throw Invalid();
            foreach (var title in Titles)
                if (title is null || !Timing(title.StartFrame, title.DurationFrames) || !IsSource(title.FontAssetId, "font") ||
                    title.Text is not { Length: <= 4096 } || title.FontSize is < 1 or > 256 || title.FadeInFrames < 0 ||
                    title.FadeOutFrames < 0 || (long)title.FadeInFrames + title.FadeOutFrames > title.DurationFrames)
                    throw Invalid();
            if (Music is { } music && !Audio(music.AssetId, music.StartFrame, music.DurationFrames, music.SourceInFrame, music.Gain))
                throw Invalid();
            foreach (var sound in Sounds)
                if (sound is null || sound.Stream is < 0 or > 63 ||
                    !Audio(sound.AssetId, sound.StartFrame, sound.DurationFrames, sound.SourceInFrame, sound.Gain))
                    throw Invalid();

            bool Timing(int start, int length) => start >= 0 && length > 0 && (long)start + length <= 600 * Fps;
            bool IsSource(string id, params string[] formats) => id is not null && Assets.TryGetValue(id, out var asset) && formats.Contains(asset.Format);
            bool Audio(string id, int start, int length, double source, double gain) =>
                Timing(start, length) && id is not null && Assets.TryGetValue(id, out var asset) && asset.Format is not ("font" or "image") &&
                double.IsFinite(source) && source >= 0 && double.IsFinite(gain) && gain is >= 0 and <= 4;
            static bool Dimensions(int[] size, bool even) => size is [>= 2 and <= 4096, >= 2 and <= 4096] &&
                (!even || size[0] % 2 == 0 && size[1] % 2 == 0);
        }
    }

    internal sealed record RenderPolicy(string[] ReadRoots, string[] WriteRoots, string[] ProtectedRoots);
    internal sealed record RenderEncoding(string VideoCodec, string PixelFormat, string Preset, int Crf, string AudioCodec, int AudioBitrateKbps);
    internal sealed record RenderAsset(string Path, string Format, string ExpectedSha256);
    internal sealed record RenderClip(string AssetId, int StartFrame, int DurationFrames, double SourceInFrame, double SourceOutFrame,
        double Speed, bool MuteNativeAudio);
    internal sealed record RenderStill(string AssetId, int StartFrame, int DurationFrames);
    internal sealed record RenderTitle(string Text, int StartFrame, int DurationFrames, int FadeInFrames, int FadeOutFrames,
        string FontAssetId, int FontSize, string Color, string Align, string Position, string Background, bool Outline);
    private sealed record Part(int Start, int End, RenderClip? Clip);
}
