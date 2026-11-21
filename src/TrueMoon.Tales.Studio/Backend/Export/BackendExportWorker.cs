using System.Diagnostics;
using System.Text.Json;
using Microsoft.Win32.SafeHandles;
using TrueMoon.Tales.Studio.Configuration;

namespace TrueMoon.Tales.Studio.Backend.Export;

internal static class BackendExportWorker
{
    internal const string Command = "--export-worker";

    internal static async Task RunAsync(BackendOptions options, string jobDirectory, string requestSha256, string workerScope, CancellationToken token)
    {
        var executable = options.ExecutablePath ?? Environment.ProcessPath ?? throw new IOException("The Studio executable is unavailable.");
        var start = new ProcessStartInfo(executable)
        {
            UseShellExecute = false, CreateNoWindow = true,
            RedirectStandardInput = true, RedirectStandardOutput = true, RedirectStandardError = true
        };
        foreach (var argument in new[] { Command, options.ConfigPath, jobDirectory, requestSha256, workerScope })
            start.ArgumentList.Add(argument);

        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(token);
        deadline.CancelAfter(TimeSpan.FromSeconds(310));
        if (await RunOwnedAsync(start, jobDirectory, deadline.Token).ConfigureAwait(false) != 0)
            throw new IOException("CPU export worker failed. See its job log.");
    }

    internal static async Task<int> RunOwnedAsync(ProcessStartInfo start, string jobDirectory, CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        using var process = Process.Start(start) ?? throw new IOException("The Studio worker did not start.");
        var errors = CaptureDiagnosticsAsync(process.StandardError.BaseStream, jobDirectory);
        Task? output = null;
        SafeFileHandle? processJob = null;
        try
        {
            if (OperatingSystem.IsWindows()) processJob = WindowsExportJob.Attach(process);
            using (var readyDeadline = CancellationTokenSource.CreateLinkedTokenSource(token))
            {
                readyDeadline.CancelAfter(TimeSpan.FromSeconds(8));
                var ready = new byte[6];
                await process.StandardOutput.BaseStream.ReadExactlyAsync(ready, readyDeadline.Token).ConfigureAwait(false);
                if (!ready.AsSpan().SequenceEqual("READY\n"u8))
                    throw new IOException("Invalid Studio worker readiness message.");
                await process.StandardInput.BaseStream.WriteAsync("S"u8.ToArray(), readyDeadline.Token).ConfigureAwait(false);
                await process.StandardInput.BaseStream.FlushAsync(readyDeadline.Token).ConfigureAwait(false);
                output = process.StandardOutput.BaseStream.CopyToAsync(Stream.Null);
            }
            await process.WaitForExitAsync(token).ConfigureAwait(false);
            return process.ExitCode;
        }
        finally
        {
            try
            {
                try { process.StandardInput.Close(); }
                catch (IOException) { }
                try { await process.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(3)).ConfigureAwait(false); }
                catch (TimeoutException)
                {
                    try { process.Kill(entireProcessTree: true); }
                    catch (InvalidOperationException) when (process.HasExited) { }
                    await process.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(3)).ConfigureAwait(false);
                }
            }
            finally { processJob?.Dispose(); }
            await errors.WaitAsync(TimeSpan.FromSeconds(3)).ConfigureAwait(false);
            if (output is not null) await output.WaitAsync(TimeSpan.FromSeconds(3)).ConfigureAwait(false);
        }
    }

    internal static async Task<int> RunChildAsync(string[] arguments)
    {
        if (arguments is not [_, var configPath, var directory, var sha, var scope] ||
            !Path.IsPathFullyQualified(configPath) || !Path.IsPathFullyQualified(directory) || !Path.IsPathFullyQualified(scope) ||
            !BackendExportStorage.ValidHash(sha) || !Console.IsInputRedirected || !Console.IsOutputRedirected)
            return 2;
        using var stop = new CancellationTokenSource(TimeSpan.FromSeconds(300));
        try
        {
            BackendMediaResolver.CheckNoLinks(directory);
            BackendMediaResolver.CheckNoLinks(scope);
            using var active = Lock(scope, "WorkerActive.lock");
            using var worker = Lock(directory, "Worker.lock");
            using var input = Console.OpenStandardInput();
            using var output = Console.OpenStandardOutput();
            if (!await WaitForOwnerAsync(input, output, stop).ConfigureAwait(false)) return 2;
            var settings = await WorkspaceProfile.ReadAsync(configPath, stop.Token).ConfigureAwait(false);
            string Tool(string? path, string fallback) => path is null ? fallback : WorkspaceProfile.ResolvePath(path, configPath);
            await BackendCpuRenderer.RenderAsync(directory, sha, Tool(settings.FfmpegExecutable, "ffmpeg"),
                Tool(settings.FfprobeExecutable, "ffprobe"), stop.Token).ConfigureAwait(false);
            return 0;
        }
        catch (OperationCanceledException) { return 1; }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or InvalidDataException or
            JsonException or ArgumentException or InvalidOperationException or System.ComponentModel.Win32Exception)
        {
            Console.Error.WriteLine(error.Message);
            return 1;
        }

    }

    internal static FileStream Lock(string directory, string name)
    {
        var path = Path.Combine(directory, name);
        BackendMediaResolver.CheckNoLinks(path, allowMissing: true);
        var file = new FileStream(path, FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.ReadWrite);
        try
        {
            if (OperatingSystem.IsMacOS()) throw new PlatformNotSupportedException();
            file.Lock(0, 1);
            return file;
        }
        catch { file.Dispose(); throw; }
    }

    internal static async Task<bool> WaitForOwnerAsync(Stream input, Stream output, CancellationTokenSource stop)
    {
        await output.WriteAsync("READY\n"u8.ToArray(), stop.Token).ConfigureAwait(false);
        await output.FlushAsync(stop.Token).ConfigureAwait(false);
        if (input.ReadByte() != 'S') return false;
        // The backend owns the pipe. EOF also stops work after a hard backend exit.
        _ = Task.Run(() =>
        {
            try { input.ReadByte(); }
            catch (IOException) { }
            catch (ObjectDisposedException) { return; }
            if (!stop.IsCancellationRequested)
                try { stop.Cancel(); }
                catch (ObjectDisposedException) { }
        });
        return true;
    }

    private static async Task CaptureDiagnosticsAsync(Stream input, string jobDirectory)
    {
        var bytes = new byte[4096];
        using var retained = new MemoryStream(64 * 1024);
        while (true)
        {
            var count = await input.ReadAsync(bytes).ConfigureAwait(false);
            if (count == 0) break;
            retained.Write(bytes, 0, (int)Math.Min(count, 64 * 1024 - retained.Length));
        }
        if (retained.Length == 0) return;
        var path = Path.Combine(jobDirectory, "Worker.log");
        await using var output = new FileStream(path, FileMode.CreateNew, FileAccess.Write, FileShare.Read);
        retained.Position = 0;
        await retained.CopyToAsync(output).ConfigureAwait(false);
    }
}
