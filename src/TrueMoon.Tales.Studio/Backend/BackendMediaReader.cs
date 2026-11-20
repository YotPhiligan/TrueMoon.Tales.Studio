using System.ComponentModel;
using System.Security.Cryptography;
using System.Text.Json;
using TrueMoon.Tales.Studio.Contracts.Projects;
using TrueMoon.Tales.Studio.Editing;
using TrueMoon.Tales.Studio.Media;
using TrueMoon.Tales.Studio.Models;

namespace TrueMoon.Tales.Studio.Backend;

// One media operation at a time, no retained frame cache or writes to project/cache roots.
internal sealed class BackendMediaReader(IFactory<BackendMediaResolver> resolvers, BackendMediaDecoder decoder)
{
    private const long MaxTakeBytes = 512L * 1024 * 1024;
    private readonly SemaphoreSlim _slot = new(1, 1);
    private readonly Lazy<BackendMediaResolver> _resolver = new(() => resolvers.Create() ?? throw new InvalidDataException("Missing media resolver."));

    internal async Task<BackendVideoInfo> ProbeReferenceAsync(string reference, CancellationToken token)
    {
        var resolver = _resolver.Value;
        var path = resolver.Resolve(reference);
        await using var lease = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
        var info = await decoder.ProbeAsync(path, token).ConfigureAwait(false);
        if (resolver.Resolve(reference) != path)
            throw new UnauthorizedAccessException("Media path changed during inspection.");
        return info;
    }

    internal async Task<TakeVideoSnapshot> SnapshotTakeAsync(ProjectContract project, TakeContract take, CancellationToken token)
    {
        var asset = take.VideoAssetId!;
        var reference = BackendScenarioEdit.TryReference(project, asset) ?? throw new ScenarioEditException("media_not_found");
        await _slot.WaitAsync(token).ConfigureAwait(false);
        try
        {
            var resolver = _resolver.Value;
            var path = resolver.Resolve(reference);
            await using var lease = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, 64 * 1024, FileOptions.Asynchronous);
            var hash = await HashTakeAsync(lease, token).ConfigureAwait(false);
            var info = await decoder.ProbeAsync(path, token).ConfigureAwait(false);
            if (resolver.Resolve(reference) != path) throw new UnauthorizedAccessException("Media path changed during inspection.");
            return new(take.Id, BackendScenarioEdit.TakeName(take), asset, reference, hash, info.Fps, info.FrameCount, info.Width, info.Height);
        }
        catch (InvalidDataException) { throw new ScenarioEditException("invalid_media"); }
        finally { _slot.Release(); }
    }

    private static async Task<string> HashTakeAsync(FileStream lease, CancellationToken token)
    {
        if (lease.Length is < 1 or > MaxTakeBytes) throw new InvalidDataException("Video exceeds the take fingerprint limit.");
        return Convert.ToHexString(await SHA256.HashDataAsync(lease, token).ConfigureAwait(false));
    }

    internal async Task<double> ProbeAudioReferenceAsync(string reference, int stream, CancellationToken token)
    {
        var resolver = _resolver.Value;
        var path = resolver.Resolve(reference);
        await using var lease = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
        var seconds = await decoder.ProbeAudioDurationAsync(path, stream, token).ConfigureAwait(false);
        if (resolver.Resolve(reference) != path)
            throw new UnauthorizedAccessException("Media path changed during inspection.");
        return seconds;
    }

    internal async Task<BackendPlaybackSession> OpenReviewedAsync(BackendRenderedMontage review, FileStream file, string kind,
        int localFrame, CancellationToken token, CancellationToken stopping)
    {
        if (kind is not ("audio" or "video" or BackendPlaybackSession.ReviewAudio) ||
            localFrame < 0 || localFrame >= review.Frames || kind == "audio" && localFrame != 0)
            throw new ArgumentException("Invalid review starting frame or stream kind.");
        var info = await ReviewInfoAsync(review, file.Name, token).ConfigureAwait(false);
        var scene = review.Scene(review.FileStart);
        var samples = AudioTimeline.Sample(review.Start + review.Frames, review.Fps) - AudioTimeline.Sample(review.Start, review.Fps);
        var slice = new AudioSlice(review.Key, 0, review.FileStart / review.Fps, review.Frames / review.Fps, 0,
            checked((int)samples), 1, 1, Optional: true);
        long? audioStart = kind == BackendPlaybackSession.ReviewAudio
            ? review.Bounded ? BackendPlaybackSession.SampleOffset(scene, localFrame) : AudioTimeline.Sample(review.FileStart + localFrame, review.Fps)
            : null;
        return new(decoder, scene, info, file.Name, [(slice, file.Name)], [file], kind, localFrame, review.Fingerprint,
            stopping, () => BackendMediaResolver.CheckNoLinks(file.Name), review.FileStart, audioStart);
    }

    internal async Task<BackendMediaReply> ReadReviewedAsync(BackendRenderedMontage review, string path, int frame, CancellationToken token)
    {
        if (frame < -1 || frame >= review.Frames) return new() { ErrorCode = "invalid_frame" };
        var info = await ReviewInfoAsync(review, path, token).ConfigureAwait(false);
        var pixels = frame < 0 ? [] : await decoder.ReadAsync(path, review.FileStart + frame, info, token).ConfigureAwait(false);
        return new()
        {
            AssetId = review.Key, Revision = review.Revision, Fps = review.Fps, FrameCount = review.Frames,
            SourceWidth = info.Width, SourceHeight = info.Height, Width = FrameDecoder.Width, Height = FrameDecoder.Height,
            FrameIndex = frame, Pixels = pixels, PixelSha256 = frame < 0 ? "" : Convert.ToHexString(SHA256.HashData(pixels))
        };
    }

    private async Task<BackendVideoInfo> ReviewInfoAsync(BackendRenderedMontage review, string path, CancellationToken token)
    {
        BackendMediaResolver.CheckNoLinks(path);
        var info = await decoder.ProbeAsync(path, token).ConfigureAwait(false);
        if (info.Fps != review.Fps || (long)review.FileStart + review.Frames > info.FrameCount ||
            review.Bounded && review.Frames != info.FrameCount)
            throw new InvalidDataException("Rendered montage differs from its verified range.");
        return info;
    }
    internal async Task<BackendPlaybackSession> OpenPlaybackAsync(ProjectContract project, string clipId, string kind,
        int localFrame, string fingerprint, CancellationToken operation, CancellationToken stopping)
    {
        var scene = BackendPlaybackProject.Scene(project, clipId);
        if (kind is not ("audio" or "video") || localFrame < 0 || localFrame >= scene.Clip.DurationFrames || kind == "audio" && localFrame != 0)
            throw new ArgumentException("Invalid playback kind or starting frame.");
        var resolver = _resolver.Value;
        var leases = new List<FileStream>();
        var paths = new Dictionary<string, string>(StringComparer.Ordinal);
        var references = new Dictionary<string, string>(StringComparer.Ordinal);
        string Resolve(string assetId)
        {
            if (paths.TryGetValue(assetId, out var previous)) return previous;
            if (!project.Properties.TryGetValue("assets", out var assets) || assets.ValueKind != JsonValueKind.Object ||
                !assets.TryGetProperty(assetId, out var asset) || asset.ValueKind != JsonValueKind.Object ||
                !asset.TryGetProperty("path", out var value) || value.ValueKind != JsonValueKind.String)
                throw new FileNotFoundException("Registered playback media is missing.");
            var path = resolver.Resolve(value.GetString()!);
            leases.Add(new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, 4096, FileOptions.Asynchronous));
            paths.Add(assetId, path);
            references.Add(assetId, value.GetString()!);
            return path;
        }
        void ValidatePaths()
        {
            foreach (var (assetId, reference) in references)
                if (resolver.Resolve(reference) != paths[assetId]) throw new UnauthorizedAccessException("Playback media moved outside its observed path.");
        }
        try
        {
            var videoPath = Resolve(scene.Takes.Single(row => row.Id == scene.Clip.TakeId).VideoAssetId);
            var info = await decoder.ProbeAsync(videoPath, operation).ConfigureAwait(false);
            var audio = kind == "audio" ? scene.Audio!.Select(slice => (slice, Resolve(slice.AssetId))).ToArray() : [];
            ValidatePaths();
            operation.ThrowIfCancellationRequested();
            return new(decoder, scene, info, videoPath, audio, leases, kind, localFrame, fingerprint, stopping, ValidatePaths);
        }
        catch { foreach (var lease in leases) await lease.DisposeAsync().ConfigureAwait(false); throw; }
    }
    internal async Task<BackendMediaReply> ReadAsync(ProjectContract project, string assetId, int frame, CancellationToken token,
        TakeVideoSnapshot? expected = null)
    {
        if (!await _slot.WaitAsync(0, token).ConfigureAwait(false)) return new() { ErrorCode = "media_busy" };
        try
        {
            if (string.IsNullOrWhiteSpace(assetId) || assetId.Length > 160 ||
                !project.Sources.Any(source => source.Takes.Any(take => take.VideoAssetId == assetId)))
                return new() { ErrorCode = "media_not_found" };
            if (!project.Properties.TryGetValue("assets", out var assets) || assets.ValueKind != JsonValueKind.Object ||
                !assets.TryGetProperty(assetId, out var asset) || asset.ValueKind != JsonValueKind.Object ||
                !asset.TryGetProperty("path", out var reference) || reference.ValueKind != JsonValueKind.String)
                return new() { ErrorCode = "media_not_found" };
            var resolver = _resolver.Value;
            var path = resolver.Resolve(reference.GetString()!);
            // Deny replacement/write while a decoder opens the validated local file.
            await using var lease = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, 4096, FileOptions.Asynchronous);
            var sourceSha = expected is null ? null : await HashTakeAsync(lease, token).ConfigureAwait(false);
            if (expected is not null && !string.Equals(sourceSha, expected.Sha256, StringComparison.OrdinalIgnoreCase))
                return new() { ErrorCode = "take_pair_changed" };
            var info = await decoder.ProbeAsync(path, token).ConfigureAwait(false);
            if (expected is not null && (expected.Fps, expected.Frames, expected.Width, expected.Height) !=
                (info.Fps, info.FrameCount, info.Width, info.Height)) return new() { ErrorCode = "take_pair_changed" };
            if (frame < -1 || frame >= info.FrameCount) return new() { ErrorCode = "invalid_frame" };
            var pixels = frame < 0 ? [] : await decoder.ReadAsync(path, frame, info, token).ConfigureAwait(false);
            if (resolver.Resolve(reference.GetString()!) != path) return new() { ErrorCode = "access_denied" };
            return new()
            {
                AssetId = assetId, Revision = project.Revision, Fps = info.Fps, FrameCount = info.FrameCount,
                SourceWidth = info.Width, SourceHeight = info.Height, Width = FrameDecoder.Width, Height = FrameDecoder.Height,
                FrameIndex = frame, Pixels = pixels, PixelSha256 = frame < 0 ? "" : Convert.ToHexString(SHA256.HashData(pixels)),
                SourceSha256 = sourceSha
            };
        }
        catch (UnauthorizedAccessException) { return new() { ErrorCode = "access_denied" }; }
        catch (FileNotFoundException) { return new() { ErrorCode = "media_not_found" }; }
        catch (DirectoryNotFoundException) { return new() { ErrorCode = "media_not_found" }; }
        catch (Win32Exception) { return new() { ErrorCode = "decoder_unavailable" }; }
        catch (OperationCanceledException) when (!token.IsCancellationRequested) { return new() { ErrorCode = "media_timeout" }; }
        catch (Exception e) when (e is InvalidDataException or ArgumentException or JsonException or NotSupportedException) { return new() { ErrorCode = "invalid_media" }; }
        catch (IOException) { return new() { ErrorCode = "media_io_error" }; }
        finally { _slot.Release(); }
    }

    internal static BackendMediaDecoder Decoder(BackendOptions options)
    {
        using var document = JsonDocument.Parse(File.ReadAllText(options.ConfigPath));
        string Tool(string property, string fallback)
        {
            if (!document.RootElement.TryGetProperty(property, out var value)) return fallback;
            if (value.ValueKind != JsonValueKind.String || string.IsNullOrWhiteSpace(value.GetString()))
                throw new InvalidDataException("Invalid trusted media tool path.");
            return Path.GetFullPath(Environment.ExpandEnvironmentVariables(value.GetString()!), Path.GetDirectoryName(options.ConfigPath)!);
        }
        return new(Tool("ffmpeg_executable", "ffmpeg"), Tool("ffprobe_executable", "ffprobe"));
    }
}
