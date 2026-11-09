using System.Diagnostics.CodeAnalysis;

namespace TrueMoon.Tales.Studio.Configuration;

internal sealed record StudioOptions(Uri Endpoint, string ProjectId, string FfmpegPath, string? SegmentId = null)
{
    public const string Usage = "Использование: TrueMoon.Tales.Studio http://127.0.0.1:<порт> <проект> <путь к ffmpeg.exe> [сегмент]";

    public static bool TryParse(string[] args, [NotNullWhen(true)] out StudioOptions? options)
    {
        options = null;
        if (args.Length is < 3 or > 4 || !Uri.TryCreate(args[0], UriKind.Absolute, out var endpoint) ||
            endpoint.Scheme != "http" || endpoint.Host != "127.0.0.1" || endpoint.AbsolutePath != "/" ||
            endpoint.UserInfo.Length != 0 || endpoint.Query.Length != 0 || endpoint.Fragment.Length != 0 ||
            !File.Exists(args[2])) return false;

        options = new(endpoint, args[1], Path.GetFullPath(args[2]), args.ElementAtOrDefault(3));
        return true;
    }
}
