using System.Security.Cryptography;
using TrueMoon.Tales.Studio.Media;

namespace TrueMoon.Tales.Studio.Backend;

internal sealed partial class BackendConnection
{
    internal Task<BackendMediaReply> MediaInfoAsync(string assetId, long revision, string fingerprint, CancellationToken token = default) =>
        MediaAsync(assetId, -1, revision, fingerprint, token);
    internal Task<BackendMediaReply> FrameAsync(string assetId, int frame, long revision, string fingerprint, CancellationToken token = default)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(frame);
        return MediaAsync(assetId, frame, revision, fingerprint, token);
    }
    private async Task<BackendMediaReply> MediaAsync(string assetId, int frame, long revision, string fingerprint, CancellationToken token)
    {
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(token);
        deadline.CancelAfter(TimeSpan.FromSeconds(12));
        var identity = await TransportAsync(() => service.HelloAsync(options.SessionKey, deadline.Token)).ConfigureAwait(false);
        ValidateIdentity(identity, processes.Owned?.IsServing(identity.ProcessId) == true);
        var reply = await TransportAsync(() => frame < 0
            ? service.MediaInfoAsync(options.SessionKey, assetId, revision, fingerprint, deadline.Token)
            : service.FrameAsync(options.SessionKey, assetId, frame, revision, fingerprint, deadline.Token)).ConfigureAwait(false);
        ValidateMedia(reply, assetId, frame, revision, fingerprint);
        return reply;
    }
    internal static void ValidateMedia(BackendMediaReply reply, string assetId, int frame, long revision, string fingerprint)
    {
        if (reply is null || reply.ErrorCode is null) throw new InvalidDataException("Null media reply.");
        if (reply.ErrorCode.Length > 0) throw new BackendCommandException(reply.ErrorCode);
        if (reply.AssetId != assetId || reply.Revision != revision ||
            !string.Equals(reply.Fingerprint, fingerprint, StringComparison.OrdinalIgnoreCase) ||
            !double.IsFinite(reply.Fps) || reply.Fps is <= 0 or > 240 || reply.FrameCount is <= 0 or > 10_000_000 ||
            reply.SourceWidth is <= 0 or > 8192 || reply.SourceHeight is <= 0 or > 8192 ||
            (long)reply.SourceWidth * reply.SourceHeight > 16_777_216 ||
            reply.Width != FrameDecoder.Width || reply.Height != FrameDecoder.Height || reply.FrameIndex != frame ||
            frame >= reply.FrameCount || reply.Pixels is null || reply.PixelSha256 is null)
            throw new InvalidDataException("Invalid media envelope.");
        if (frame < 0)
        {
            if (reply.Pixels.Length != 0 || reply.PixelSha256.Length != 0) throw new InvalidDataException("Pixels in metadata reply.");
        }
        else if (reply.Pixels.Length != FrameDecoder.Width * FrameDecoder.Height * 4 ||
            !string.Equals(Convert.ToHexString(SHA256.HashData(reply.Pixels)), reply.PixelSha256, StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException("Invalid frame bytes or fingerprint.");
    }
}
