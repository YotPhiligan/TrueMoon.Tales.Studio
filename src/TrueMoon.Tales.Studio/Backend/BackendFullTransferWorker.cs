using System.ComponentModel;
using System.Diagnostics;
using System.Text;
using TrueMoon.Tales.Studio.Configuration;

namespace TrueMoon.Tales.Studio.Backend;

internal sealed record BackendFullTransferWorker(string Python, string ComfyRoot, string Script)
{
    internal static BackendFullTransferWorker Read(BackendOptions options)
    {
        var config = WorkspaceProfile.Read(options.ConfigPath);
        var python = PathFor(config.PythonExecutable);
        var comfy = PathFor(config.ComfyRoot);
        var script = Path.Combine(AppContext.BaseDirectory, "FullTransferWorker", "prepare.py");
        if (!File.Exists(python) || !Directory.Exists(comfy) || !File.Exists(script))
            throw new ScenarioEditException("transfer_unavailable");
        BackendMediaResolver.CheckNoLinks(script);
        return new(python, comfy, script);

        string PathFor(string? path) => path is not null
            ? WorkspaceProfile.ResolvePath(path, options.ConfigPath)
            : throw new ScenarioEditException("transfer_unavailable");
    }

    internal async Task RunAsync(string config, string preview, CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        var start = new ProcessStartInfo(Python)
        {
            WorkingDirectory = Path.GetDirectoryName(Script)!, UseShellExecute = false, CreateNoWindow = true,
            RedirectStandardOutput = true, RedirectStandardError = true
        };
        // The packaged entry point only builds graphs. Never pass --stage or run a ComfyUI launcher.
        foreach (var arg in new[] { "-I", "-B", Script, config, preview }) start.ArgumentList.Add(arg);
        start.Environment["CUDA_VISIBLE_DEVICES"] = "";
        using var process = Start(start);
        var output = process.StandardOutput.BaseStream.CopyToAsync(Stream.Null);
        var errors = ReadErrorAsync(process.StandardError.BaseStream);
        try
        {
            await process.WaitForExitAsync(token).ConfigureAwait(false);
            if (process.ExitCode != 0)
            {
                var error = await errors.WaitAsync(token).ConfigureAwait(false);
                throw new ScenarioEditException(ErrorCode(error));
            }
        }
        finally
        {
            if (!process.HasExited)
            {
                try { process.Kill(entireProcessTree: true); }
                catch (InvalidOperationException) when (process.HasExited) { }
                await process.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(4)).ConfigureAwait(false);
            }
            await Task.WhenAll(output, errors).WaitAsync(TimeSpan.FromSeconds(4)).ConfigureAwait(false);
        }

        static Process Start(ProcessStartInfo start)
        {
            try { return Process.Start(start) ?? throw new ScenarioEditException("transfer_unavailable"); }
            catch (Win32Exception) { throw new ScenarioEditException("transfer_unavailable"); }
        }
    }

    private static string ErrorCode(string error)
    {
        if (error.Contains("Both sound fields must be N/A", StringComparison.Ordinal)) return "transfer_prompt_sound";
        if (error.Contains("Motion reference", StringComparison.Ordinal)) return "transfer_timing";
        return error.Contains("ValueError:", StringComparison.Ordinal) ? "transfer_invalid_profile" : "transfer_worker_failed";
    }

    private static async Task<string> ReadErrorAsync(Stream stream)
    {
        const int limit = 8192;
        var buffer = new byte[1024];
        using var log = new MemoryStream(limit);
        int read;
        while ((read = await stream.ReadAsync(buffer).ConfigureAwait(false)) != 0)
            log.Write(buffer, 0, (int)Math.Min(read, limit - log.Length));
        return Encoding.UTF8.GetString(log.ToArray());
    }
}
