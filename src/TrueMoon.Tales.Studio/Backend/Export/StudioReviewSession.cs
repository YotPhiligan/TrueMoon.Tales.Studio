namespace TrueMoon.Tales.Studio.Backend.Export;

internal sealed class StudioReviewSession(string root) : IDisposable
{
    private const string Prefix = "review-";
    private const string LeaseExtension = ".lock";
    private FileStream? _owner;

    internal static string DefaultRoot => Path.Combine(Path.GetTempPath(), "TrueMoon.Tales", "Studio");
    internal static string FullTransferRoot => Path.Combine(DefaultRoot, "FullTransfer");
    internal string DirectoryPath { get; } = Path.Combine(Path.GetFullPath(root), $"{Prefix}{Environment.ProcessId:x8}-{Guid.NewGuid():N}");

    internal void Open()
    {
        if (_owner is not null) return;

        BackendMediaResolver.CheckNoLinks(DirectoryPath, allowMissing: true);
        Directory.CreateDirectory(Path.GetDirectoryName(DirectoryPath)!);
        _owner = OpenLease(DirectoryPath + LeaseExtension, FileMode.CreateNew);
        // The lease is acquired before the session can appear on disk, and kept until its renderer has stopped.
        Directory.CreateDirectory(DirectoryPath);
    }

    internal static async Task CleanAbandonedAsync(string root, bool preserveCompleted = false)
    {
        try
        {
            root = Path.GetFullPath(root);
            BackendMediaResolver.CheckNoLinks(root, allowMissing: true);
            if (!Directory.Exists(root)) return;
            var retryUntil = System.Diagnostics.Stopwatch.StartNew();
            foreach (var lease in Directory.EnumerateFiles(root, Prefix + "*" + LeaseExtension))
            {
                var name = Path.GetFileNameWithoutExtension(lease);
                if (!ValidName(name) || Path.GetExtension(lease) != LeaseExtension)
                    continue;

                try
                {
                    using (OpenLease(lease, FileMode.Open))
                    {
                        var directory = Path.Combine(root, name);
                        if (preserveCompleted && File.Exists(Path.Combine(directory, "Completed.json"))) continue;
                        // The renderer can still be releasing its job lock after the backend loses its stdin pipe.
                        while (true)
                        {
                            try { DeleteStoppedSession(directory); break; }
                            catch (IOException) when (retryUntil.Elapsed < TimeSpan.FromSeconds(1))
                            { await Task.Delay(100).ConfigureAwait(false); }
                        }
                    }
                    File.Delete(lease);
                }
                catch (Exception error) when (error is IOException or UnauthorizedAccessException)
                { /* An active or inaccessible session stays in place. */ }
            }
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException)
        { Console.Error.WriteLine("Studio preview: could not check temporary sessions."); }
    }

    internal static string? CompletedFullTransfer(string? name)
    {
        if (name is null || !ValidName(name)) return null;
        var path = Path.Combine(FullTransferRoot, name);
        var completion = Path.Combine(path, "Completed.json");
        BackendMediaResolver.CheckNoLinks(completion, allowMissing: true);
        return File.Exists(completion) && File.Exists(path + LeaseExtension) ? path : null;
    }

    internal static void ReleaseCompletedFullTransfer(string path)
    {
        var lease = path + LeaseExtension;
        try
        {
            using (OpenLease(lease, FileMode.Open)) DeleteStoppedSession(path);
            File.Delete(lease);
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException)
        { Console.Error.WriteLine("Studio Full Transfer: could not remove recovered temporary files."); }
    }

    internal static bool ValidName(string name) =>
        name.Length == Prefix.Length + 8 + 1 + 32 && name.StartsWith(Prefix, StringComparison.Ordinal) &&
        name[Prefix.Length + 8] == '-' && name.AsSpan(Prefix.Length, 8).IndexOfAnyExcept("0123456789abcdef") < 0 &&
        name.AsSpan(Prefix.Length + 9).IndexOfAnyExcept("0123456789abcdef") < 0;

    public void Dispose()
    {
        if (_owner is not { } owner) return;
        _owner = null;
        try
        {
            using (owner) DeleteStoppedSession(DirectoryPath);
            File.Delete(DirectoryPath + LeaseExtension);
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException)
        { Console.Error.WriteLine("Studio preview: could not remove the temporary session."); }
    }

    internal void LeaveForRecovery()
    {
        _owner?.Dispose();
        _owner = null;
    }

    private static FileStream OpenLease(string path, FileMode mode)
    {
        BackendMediaResolver.CheckNoLinks(path, allowMissing: mode == FileMode.CreateNew);
        return new FileStream(path, mode, FileAccess.ReadWrite, FileShare.None);
    }

    private static void DeleteStoppedSession(string directory)
    {
        BackendMediaResolver.CheckNoLinks(directory, allowMissing: true);
        if (!Directory.Exists(directory)) return;
        CheckWorker(directory);
        var jobs = Path.Combine(directory, "Jobs");
        BackendMediaResolver.CheckNoLinks(jobs, allowMissing: true);
        if (Directory.Exists(jobs))
            foreach (var job in Directory.EnumerateDirectories(jobs, "working-*"))
                CheckWorker(job);

        Directory.Delete(directory, recursive: true);
    }

    private static void CheckWorker(string directory)
    {
        var path = Path.Combine(directory, "Worker.lock");
        BackendMediaResolver.CheckNoLinks(path, allowMissing: true);
        if ((!OperatingSystem.IsWindows() && !OperatingSystem.IsLinux()) || !File.Exists(path)) return;
        // The worker can still be releasing its lock after the owner has crashed.
        using var worker = new FileStream(path, FileMode.Open, FileAccess.ReadWrite, FileShare.ReadWrite);
        worker.Lock(0, 1);
    }
}
