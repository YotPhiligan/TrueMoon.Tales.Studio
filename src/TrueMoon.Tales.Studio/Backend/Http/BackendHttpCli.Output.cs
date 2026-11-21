namespace TrueMoon.Tales.Studio.Backend.Http;

internal static partial class BackendHttpCli
{
    private static bool TryNewOutputPath(string value, string extension, out string? path)
    {
        path = null;
        if (string.IsNullOrWhiteSpace(value)) return false;
        try
        {
            if (value.StartsWith("~/", StringComparison.Ordinal) || OperatingSystem.IsWindows() && value.StartsWith("~\\", StringComparison.Ordinal))
                value = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), value[2..]);
            if (OperatingSystem.IsWindows())
            {
                value = value.Replace('/', '\\');
                if (value.StartsWith(@"\\.\", StringComparison.Ordinal) || value.StartsWith(@"\\?\", StringComparison.Ordinal) ||
                    value.EndsWith(' ') || value.EndsWith('.'))
                    return false;
            }
            var candidate = Path.GetFullPath(value);
            if (!Path.GetExtension(candidate).Equals(extension, StringComparison.OrdinalIgnoreCase) ||
                !Directory.Exists(Path.GetDirectoryName(candidate)) || Path.Exists(candidate))
                return false;
            if (OperatingSystem.IsWindows())
            {
                var relative = candidate.AsSpan(Path.GetPathRoot(candidate)!.Length);
                var file = Path.GetFileName(candidate);
                var stem = file[..file.IndexOf('.')].TrimEnd(' ').ToUpperInvariant();
                if (relative.Contains(':') || file.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0 ||
                    stem is "CON" or "PRN" or "AUX" or "NUL" ||
                    stem.Length == 4 && (stem.StartsWith("COM", StringComparison.Ordinal) || stem.StartsWith("LPT", StringComparison.Ordinal)) &&
                    "0123456789¹²³".Contains(stem[3]))
                    return false;
            }
            path = candidate;
            return true;
        }
        catch (Exception error) when (error is IOException or ArgumentException or NotSupportedException or UnauthorizedAccessException)
        { return false; }
    }

    private static async Task<int> PublishOutputAsync(string output, string prefix, Func<FileStream, Task<int>> write, CancellationToken token)
    {
        var stage = Path.Combine(Path.GetDirectoryName(output)!, $"{prefix}{Guid.NewGuid():N}.tmp");
        var created = false;
        try
        {
            await using (var file = new FileStream(stage, FileMode.CreateNew, FileAccess.Write, FileShare.None, 64 * 1024, FileOptions.Asynchronous))
            {
                created = true;
                var code = await write(file).ConfigureAwait(false);
                if (code != 0) return code;
                file.Flush(flushToDisk: true);
            }
            token.ThrowIfCancellationRequested();
            File.Move(stage, output);
            created = false;
            return 0;
        }
        finally { if (created) File.Delete(stage); }
    }
}
