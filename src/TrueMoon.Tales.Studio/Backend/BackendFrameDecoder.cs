using System.Globalization;
using TrueMoon.Tales.Studio.Media;

namespace TrueMoon.Tales.Studio.Backend;

internal sealed class BackendFrameDecoder(BackendConnection backend) : IFrameDecoder
{
    internal static Uri Reference(string asset, long revision, string fingerprint) =>
        new("studio-media://frame/" + Uri.EscapeDataString(asset) + "?revision=" + revision.ToString(CultureInfo.InvariantCulture) + "&sha=" + fingerprint);
    public async Task<byte[]> ReadAsync(Uri media, int frame, double fps, CancellationToken cancellationToken)
    {
        if (media.Scheme != "studio-media" || media.Host != "frame" || media.UserInfo.Length != 0 ||
            media.Fragment.Length != 0 || frame < 0) throw new InvalidDataException("Invalid backend media reference.");
        var parts = media.Query.TrimStart('?').Split('&');
        if (parts.Length != 2 || !parts[0].StartsWith("revision=", StringComparison.Ordinal) ||
            !parts[1].StartsWith("sha=", StringComparison.Ordinal) ||
            !long.TryParse(parts[0][9..], NumberStyles.None, CultureInfo.InvariantCulture, out var revision) || revision < 0 ||
            parts[1][4..] is not { Length: 64 } hash || !hash.All(Uri.IsHexDigit))
            throw new InvalidDataException("Invalid backend media baseline.");
        var reply = await backend.FrameAsync(Uri.UnescapeDataString(media.AbsolutePath[1..]), frame, revision, hash, cancellationToken).ConfigureAwait(false);
        if (Math.Abs(reply.Fps - fps) > 1e-6) throw new InvalidDataException("Media time base changed.");
        return reply.Pixels;
    }
}
