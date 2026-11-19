using System.Runtime.InteropServices;
using System.Security.Cryptography;
using SkiaSharp;
using TrueMoon.Tales.Studio.Media;
using TrueMoon.Tales.Studio.Persistence;

namespace TrueMoon.Tales.Studio.Backend;

internal sealed partial class StudioBackendService
{
    public async Task<BackendMediaReply> StoryboardStillAsync(string sessionKey, string segmentId, string actionId,
        long expectedRevision, string fingerprint, CancellationToken cancellationToken = default)
    {
        Authorize(sessionKey);
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, _mediaStopping.Token);
        deadline.CancelAfter(TimeSpan.FromSeconds(10));
        var token = deadline.Token;
        if (!await _mediaGate.WaitAsync(0, token).ConfigureAwait(false)) return new() { ErrorCode = "media_busy" };
        try
        {
            var document = await ObserveAsync(expectedRevision, fingerprint, token).ConfigureAwait(false);
            if (document.SchemaVersion != 2) return new() { ErrorCode = "migration_required" };
            var reference = BackendAdaptation.Read(document.Project).Stills.SingleOrDefault(row =>
                row.SegmentId == segmentId && row.ActionId == actionId)?.Reference;
            if (reference is null) return new() { ErrorCode = "media_not_found" };
            var resolver = new BackendMediaResolver(options);
            var path = BackendAdaptation.ResolveStill(resolver, reference);
            await using var source = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, 16 * 1024, FileOptions.Asynchronous);
            if (source.Length is < 1 or > 32 * 1024 * 1024) return new() { ErrorCode = "invalid_animatic_still" };
            var version = Convert.ToHexString(await SHA256.HashDataAsync(source, token).ConfigureAwait(false));
            source.Position = 0;
            using var codec = SKCodec.Create(source);
            if (codec is null || !BackendAdaptation.ValidStill(codec))
                return new() { ErrorCode = "invalid_animatic_still" };
            using var bitmap = SKBitmap.Decode(codec);
            if (bitmap is null) return new() { ErrorCode = "invalid_animatic_still" };
            const int width = FrameDecoder.Width, height = FrameDecoder.Height;
            using var scaled = new SKBitmap(width, height, SKColorType.Bgra8888, SKAlphaType.Opaque);
            using (var canvas = new SKCanvas(scaled))
            {
                var scale = Math.Min((float)width / bitmap.Width, (float)height / bitmap.Height);
                var imageWidth = bitmap.Width * scale;
                var imageHeight = bitmap.Height * scale;
                var bounds = SKRect.Create((width - imageWidth) / 2, (height - imageHeight) / 2, imageWidth, imageHeight);
                canvas.Clear(SKColors.Black);
                canvas.DrawBitmap(bitmap, bounds, new SKSamplingOptions(SKFilterMode.Linear));
            }
            var pixels = new byte[width * height * 4];
            Marshal.Copy(scaled.GetPixels(), pixels, 0, pixels.Length);
            if (!string.Equals(BackendAdaptation.ResolveStill(resolver, reference), path, StringComparison.OrdinalIgnoreCase))
                return new() { ErrorCode = "access_denied" };
            await ObserveAsync(expectedRevision, fingerprint, token).ConfigureAwait(false);
            return new()
            {
                AssetId = actionId, Revision = document.Project.Revision, Fingerprint = document.Fingerprint,
                FrameCount = 1, FrameIndex = 0, Fps = document.Project.Fps, SourceWidth = bitmap.Width, SourceHeight = bitmap.Height,
                Width = width, Height = height, Pixels = pixels, PixelSha256 = Convert.ToHexString(SHA256.HashData(pixels)), SourceSha256 = version
            };
        }
        catch (ProjectRevisionConflictException) { return new() { ErrorCode = "revision_conflict" }; }
        catch (ProjectRecoveryRequiredException) { return new() { ErrorCode = "recovery_required" }; }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested) { return new() { ErrorCode = "media_timeout" }; }
        catch (ScenarioEditException error) { return new() { ErrorCode = error.Code }; }
        catch (UnauthorizedAccessException) { return new() { ErrorCode = "access_denied" }; }
        catch (FileNotFoundException) { return new() { ErrorCode = "media_not_found" }; }
        catch (Exception error) when (error is IOException or InvalidDataException or System.Text.Json.JsonException)
        { return new() { ErrorCode = "invalid_animatic_still" }; }
        finally { _mediaGate.Release(); }
    }
}
