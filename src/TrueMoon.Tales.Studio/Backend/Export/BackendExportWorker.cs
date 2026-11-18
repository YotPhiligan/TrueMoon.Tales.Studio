using System.Diagnostics;
using System.Text.Json;

namespace TrueMoon.Tales.Studio.Backend.Export;

internal static class BackendExportWorker
{
    internal static async Task RunAsync(string configPath, string jobDirectory, string requestSha256, CancellationToken token)
    {
        using var config = JsonDocument.Parse(await File.ReadAllBytesAsync(configPath, token).ConfigureAwait(false));
        var directory = Path.GetDirectoryName(configPath)!;
        string? Tool(string key)
        {
            if (!config.RootElement.TryGetProperty(key, out var value)) return null;
            if (value.ValueKind != JsonValueKind.String || string.IsNullOrWhiteSpace(value.GetString()))
                throw new InvalidDataException("Invalid configured export executable.");
            return Path.GetFullPath(Environment.ExpandEnvironmentVariables(value.GetString()!), directory);
        }

        var python = Tool("python_executable") ?? throw new InvalidDataException("Configure python_executable for CPU export.");
        var script = Path.Combine(AppContext.BaseDirectory, "ExportWorker", "RunExport.py");
        if (!File.Exists(python) || !File.Exists(script))
            throw new FileNotFoundException("CPU export worker is unavailable.");
        var start = new ProcessStartInfo(python)
        {
            WorkingDirectory = Path.GetDirectoryName(script)!,
            UseShellExecute = false, CreateNoWindow = true,
            RedirectStandardInput = true, RedirectStandardOutput = true, RedirectStandardError = true
        };
        foreach (var argument in new[] { "-I", "-B", script, jobDirectory, requestSha256 })
            start.ArgumentList.Add(argument);
        start.Environment["OPENBLAS_NUM_THREADS"] = "1";
        start.Environment.Remove("IMAGEIO_FFMPEG_EXE");
        if (Tool("ffmpeg_executable") is { } ffmpeg)
            start.Environment["IMAGEIO_FFMPEG_EXE"] = ffmpeg;

        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(token);
        deadline.CancelAfter(TimeSpan.FromSeconds(310));
        deadline.Token.ThrowIfCancellationRequested();
        using var process = Process.Start(start) ?? throw new IOException("CPU export worker did not start.");
        var errors = CaptureDiagnosticsAsync(process.StandardError.BaseStream, jobDirectory);
        Task? output = null;
        try
        {
            using (var readyDeadline = CancellationTokenSource.CreateLinkedTokenSource(deadline.Token))
            {
                readyDeadline.CancelAfter(TimeSpan.FromSeconds(8));
                var ready = new byte[6];
                await process.StandardOutput.BaseStream.ReadExactlyAsync(ready, readyDeadline.Token).ConfigureAwait(false);
                if (!ready.AsSpan().SequenceEqual("READY\n"u8))
                    throw new IOException("Invalid export worker readiness message.");
                await process.StandardInput.BaseStream.WriteAsync("S"u8.ToArray(), readyDeadline.Token).ConfigureAwait(false);
                await process.StandardInput.BaseStream.FlushAsync(readyDeadline.Token).ConfigureAwait(false);
                output = process.StandardOutput.BaseStream.CopyToAsync(Stream.Null);
            }
            await process.WaitForExitAsync(deadline.Token).ConfigureAwait(false);
            if (process.ExitCode != 0) throw new IOException("CPU export worker failed. See its job log.");
        }
        finally
        {
            // EOF ends the worker and its process group, even when the backend is killed externally.
            try { process.StandardInput.Close(); }
            catch (IOException) { }
            try { await process.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(3)).ConfigureAwait(false); }
            catch (TimeoutException)
            {
                try { process.Kill(entireProcessTree: true); }
                catch (InvalidOperationException) when (process.HasExited) { }
                await process.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(3)).ConfigureAwait(false);
            }
            await errors.WaitAsync(TimeSpan.FromSeconds(3)).ConfigureAwait(false);
            if (output is not null) await output.WaitAsync(TimeSpan.FromSeconds(3)).ConfigureAwait(false);
        }
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
