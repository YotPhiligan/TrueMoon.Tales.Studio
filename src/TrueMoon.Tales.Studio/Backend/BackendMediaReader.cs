using System.ComponentModel;
using System.Security.Cryptography;
using System.Text.Json;
using TrueMoon.Tales.Studio.Contracts.Projects;
using TrueMoon.Tales.Studio.Media;

namespace TrueMoon.Tales.Studio.Backend;

// One media operation at a time, no retained frame cache or writes to project/cache roots.
internal sealed class BackendMediaReader(IFactory<BackendMediaResolver> resolvers, BackendMediaDecoder decoder)
{
    private readonly SemaphoreSlim _slot = new(1, 1);
    private readonly Lazy<BackendMediaResolver> _resolver = new(() => resolvers.Create() ?? throw new InvalidDataException("Missing media resolver."));
    internal async Task<BackendMediaReply> ReadAsync(ProjectContract project, string assetId, int frame, CancellationToken token)
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
            var info = await decoder.ProbeAsync(path, token).ConfigureAwait(false);
            if (frame < -1 || frame >= info.FrameCount) return new() { ErrorCode = "invalid_frame" };
            var pixels = frame < 0 ? [] : await decoder.ReadAsync(path, frame, info, token).ConfigureAwait(false);
            if (resolver.Resolve(reference.GetString()!) != path) return new() { ErrorCode = "access_denied" };
            return new()
            {
                AssetId = assetId, Revision = project.Revision, Fps = info.Fps, FrameCount = info.FrameCount,
                SourceWidth = info.Width, SourceHeight = info.Height, Width = FrameDecoder.Width, Height = FrameDecoder.Height,
                FrameIndex = frame, Pixels = pixels, PixelSha256 = frame < 0 ? "" : Convert.ToHexString(SHA256.HashData(pixels))
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
