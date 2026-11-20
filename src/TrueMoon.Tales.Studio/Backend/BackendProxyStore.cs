using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace TrueMoon.Tales.Studio.Backend;

internal sealed record ProxyReceipt(string AssetId, string OriginalSha256, long OriginalBytes, string ProxySha256,
    string FileId, BackendVideoInfo Original, BackendVideoInfo Proxy, bool Enabled)
{
    internal bool Valid(string asset) => AssetId == asset && BackendConnection.IsHash(OriginalSha256) &&
        BackendConnection.IsHash(ProxySha256) && Guid.TryParseExact(FileId, "N", out _) &&
        OriginalBytes is > 0 and <= BackendProxyStore.MaxSourceBytes && Original is not null && Proxy is not null &&
        Proxy.Width == 960 && Proxy.Height is > 0 and <= 8192 && Original.Width > Proxy.Width &&
        Proxy.FrameCount == Original.FrameCount && Proxy.FrameCount > 0 && Proxy.Fps == Original.Fps &&
        double.IsFinite(Proxy.Fps) && Proxy.Fps is > 0 and <= 240;
}

internal sealed record ProxyStatus(string AssetId, string State, string? Error = null, ProxyReceipt? Receipt = null);
internal sealed record ProxyInput(FileStream File, ProxyReceipt Receipt) : IAsyncDisposable
{
    public ValueTask DisposeAsync() => File.DisposeAsync();
}

internal sealed class BackendProxyStore(BackendOptions options, BackendMediaDecoder decoder) : IDisposable
{
    internal const long MaxSourceBytes = 1024L * 1024 * 1024;
    private readonly Lazy<string?> _root = new(() => new BackendProjectAccess(options.ConfigPath).ProxyCache());
    private readonly object _sync = new();
    private Job? _job;
    private bool _disposed;

    private sealed class Job(string asset)
    {
        internal readonly CancellationTokenSource Stop = new(TimeSpan.FromMinutes(4));
        internal Task Completion = Task.CompletedTask;
        internal ProxyStatus Status = new(asset, "running");
    }

    internal ProxyStatus Start(string project, string asset, string path, string reference, bool manual,
        Func<Action, CancellationToken, Task> publish)
    {
        var directory = DirectoryFor(project, asset) ?? throw new ScenarioEditException("proxy_cache_required");
        lock (_sync)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            if (_job?.Status.State == "running")
            {
                if (_job.Status.AssetId != asset) throw new ScenarioEditException("proxy_busy");
                return _job.Status;
            }
            var job = new Job(asset);
            var running = job.Status;
            _job = job;
            job.Completion = Task.Run(() => PrepareAsync(job, directory, project, asset, path, reference, manual, publish));
            return running;
        }
    }

    internal async Task<ProxyStatus> StatusAsync(string project, string asset, string path, CancellationToken token)
    {
        lock (_sync)
            if (_job?.Status is { } status && status.AssetId == asset && status.State is "running" or "failed" or "cancelled")
                return status;
        await using var source = Open(path);
        await using var ready = await TryOpenAsync(project, asset, source, token, requireEnabled: false).ConfigureAwait(false);
        return ready is null ? new(asset, "original") : new(asset, ready.Receipt.Enabled ? "ready" : "original", Receipt: ready.Receipt);
    }

    internal void SelectOriginal(string project, string asset)
    {
        lock (_sync)
        {
            if (_job?.Status.State == "running" && _job.Status.AssetId == asset) throw new ScenarioEditException("proxy_busy");
            var directory = DirectoryFor(project, asset);
            if (directory is not null && Read(directory, asset) is { Enabled: true } proof)
                Save(directory, proof with { Enabled = false });
            if (_job?.Status.AssetId == asset) _job = null;
        }
    }

    internal ProxyStatus Cancel(string asset)
    {
        lock (_sync)
        {
            if (_job?.Status is not { State: "running" } status || status.AssetId != asset)
                throw new ScenarioEditException("proxy_not_running");
            _job.Stop.Cancel();
            return status;
        }
    }

    internal async Task<ProxyInput?> TryOpenAsync(string project, string asset, FileStream source, CancellationToken token,
        bool requireEnabled = true)
    {
        try
        {
            var directory = DirectoryFor(project, asset);
            if (directory is null || Read(directory, asset) is not { } proof || requireEnabled && !proof.Enabled ||
                proof.OriginalBytes != source.Length)
                return null;
            var output = ProxyPath(directory, proof.FileId);
            BackendMediaResolver.CheckNoLinks(output);
            var file = Open(output);
            try
            {
                if (file.Length is > 0 and <= MaxSourceBytes &&
                    string.Equals(proof.OriginalSha256, await HashAsync(source, token).ConfigureAwait(false), StringComparison.Ordinal) &&
                    string.Equals(proof.ProxySha256, await HashAsync(file, token).ConfigureAwait(false), StringComparison.Ordinal))
                    return new(file, proof);
            }
            catch { await file.DisposeAsync().ConfigureAwait(false); throw; }
            await file.DisposeAsync().ConfigureAwait(false);
            return null;
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or JsonException or InvalidDataException or
            ArgumentException or InvalidOperationException)
        { return null; }
    }

    private async Task PrepareAsync(Job job, string directory, string project, string asset, string sourcePath, string reference,
        bool manual, Func<Action, CancellationToken, Task> publish)
    {
        var token = job.Stop.Token;
        var fileId = Guid.NewGuid().ToString("N");
        var staged = Path.Combine(directory, $"pending-{fileId}.mp4");
        var output = ProxyPath(directory, fileId);
        var moved = false;
        var finished = false;
        var result = new ProxyStatus(asset, "failed", "proxy_failed");
        try
        {
            if (!string.Equals(new BackendMediaResolver(options).Resolve(reference), sourcePath, PathComparison))
                throw new ScenarioEditException("proxy_source_changed");
            await using var source = Open(sourcePath);
            if (source.Length is < 1 or > MaxSourceBytes) throw new ScenarioEditException("proxy_unsupported");
            var original = await decoder.ProbeProxyVideoAsync(sourcePath, token).ConfigureAwait(false);
            if (manual ? original.Codec != "h264" || original.Info.Width < 1920 :
                original.Info.Width < 3840 || original.Info.Height < 2160 || original.LongestGop < 2 * original.Info.Fps)
                throw new ScenarioEditException(manual ? "proxy_wide_required" : "proxy_4k_required");
            await using (var cached = await TryOpenAsync(project, asset, source, token, requireEnabled: false).ConfigureAwait(false))
            {
                if (cached is not null)
                {
                    var enabled = cached.Receipt with { Enabled = true };
                    if (!cached.Receipt.Enabled) await publish(() => Save(directory, enabled), token).ConfigureAwait(false);
                    result = new(asset, "ready", Receipt: enabled);
                    return;
                }
            }
            var sha = await HashAsync(source, token).ConfigureAwait(false);
            var audio = await decoder.ProxyAudioFingerprintAsync(sourcePath, token).ConfigureAwait(false);
            BackendMediaResolver.CheckNoLinks(directory, allowMissing: true);
            Directory.CreateDirectory(directory);
            BackendMediaResolver.CheckNoLinks(staged, allowMissing: true);
            await decoder.EncodeProxyAsync(sourcePath, staged, original.Timescale, token).ConfigureAwait(false);
            BackendMediaResolver.CheckNoLinks(staged);
            ProxyReceipt proof;
            await using (var proxy = Open(staged))
            {
                var converted = await decoder.ProbeProxyVideoAsync(staged, token).ConfigureAwait(false);
                if (converted.Codec != "h264" || converted.Info.Width != 960 || converted.LongestGop > 12 ||
                    converted.Info.Fps != original.Info.Fps || converted.Timescale != original.Timescale ||
                    !converted.Frames.AsSpan().SequenceEqual(original.Frames) ||
                    await decoder.ProxyAudioFingerprintAsync(staged, token).ConfigureAwait(false) != audio)
                    throw new ScenarioEditException("proxy_timing");
                proof = new(asset, sha, source.Length, await HashAsync(proxy, token).ConfigureAwait(false), fileId,
                    original.Info, converted.Info, Enabled: true);
            }
            await publish(() =>
            {
                token.ThrowIfCancellationRequested();
                if (!string.Equals(new BackendMediaResolver(options).Resolve(reference), sourcePath, PathComparison))
                    throw new ScenarioEditException("proxy_source_changed");
                BackendMediaResolver.CheckNoLinks(output, allowMissing: true);
                File.Move(staged, output);
                moved = true;
                Save(directory, proof);
                finished = true;
            }, token).ConfigureAwait(false);
            result = new(asset, "ready", Receipt: proof);
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested) { result = new(asset, "cancelled"); }
        catch (Exception error)
        {
            result = new(asset, "failed", error switch
            {
                ScenarioEditException edit => edit.Code,
                Persistence.ProjectRevisionConflictException => "revision_conflict",
                UnauthorizedAccessException => "access_denied",
                InvalidDataException or JsonException => "proxy_unsupported",
                _ => "proxy_failed"
            });
        }
        finally
        {
            DeletePrivateFile(staged);
            if (moved && !finished) DeletePrivateFile(output);
            Complete(job, result);
        }
    }

    private void Complete(Job job, ProxyStatus status)
    {
        lock (_sync)
        {
            job.Status = status;
            job.Stop.Dispose();
        }
    }

    private static ProxyReceipt? Read(string directory, string asset)
    {
        var file = Path.Combine(directory, "Selected.json");
        try
        {
            BackendMediaResolver.CheckNoLinks(file, allowMissing: true);
            using var stream = new FileStream(file, FileMode.Open, FileAccess.Read, FileShare.Read | FileShare.Delete);
            return stream.Length is > 0 and <= 4096 &&
                   JsonSerializer.Deserialize<ProxyReceipt>(stream, BackendAdaptation.Json) is { } receipt && receipt.Valid(asset)
                ? receipt : null;
        }
        catch (Exception error) when (error is FileNotFoundException or DirectoryNotFoundException or JsonException) { return null; }
    }

    private static void Save(string directory, ProxyReceipt receipt)
    {
        var path = Path.Combine(directory, "Selected.json");
        var pending = Path.Combine(directory, $"selected-{Guid.NewGuid():N}.pending");
        BackendMediaResolver.CheckNoLinks(path, allowMissing: true);
        BackendMediaResolver.CheckNoLinks(pending, allowMissing: true);
        try
        {
            using (var stream = new FileStream(pending, FileMode.CreateNew, FileAccess.Write, FileShare.None))
            {
                JsonSerializer.Serialize(stream, receipt, BackendAdaptation.Json);
                stream.Flush(flushToDisk: true);
            }
            File.Move(pending, path, overwrite: true);
        }
        finally { DeletePrivateFile(pending); }
    }

    private string? DirectoryFor(string project, string asset)
    {
        if (_root.Value is not { } root) return null;
        var path = Path.TrimEndingDirectorySeparator(options.ProjectDirectory);
        var scope = Key((OperatingSystem.IsWindows() ? path.ToUpperInvariant() : path) + "\n" + project);
        return Path.Combine(root, scope, Key(asset));
    }

    private static string Key(string value) => Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(value)))[..32];
    private static string ProxyPath(string directory, string fileId) => Path.Combine(directory, fileId + ".mp4");
    private static FileStream Open(string path) => new(path, FileMode.Open, FileAccess.Read, FileShare.Read, 64 * 1024,
        FileOptions.Asynchronous | FileOptions.SequentialScan);

    private static async Task<string> HashAsync(FileStream source, CancellationToken token)
    {
        source.Position = 0;
        return Convert.ToHexString(await SHA256.HashDataAsync(source, token).ConfigureAwait(false));
    }

    private static void DeletePrivateFile(string path)
    {
        try { BackendMediaResolver.CheckNoLinks(path, allowMissing: true); File.Delete(path); }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException) { }
    }

    private static readonly StringComparison PathComparison = OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;

    public void Dispose()
    {
        Task? pending;
        lock (_sync)
        {
            if (_disposed) return;
            _disposed = true;
            if (_job?.Status.State == "running") _job.Stop.Cancel();
            pending = _job?.Completion;
        }
        pending?.GetAwaiter().GetResult();
    }
}
