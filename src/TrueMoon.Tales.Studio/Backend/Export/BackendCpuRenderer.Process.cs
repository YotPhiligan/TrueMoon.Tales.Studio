using System.Diagnostics;
using System.Text;

namespace TrueMoon.Tales.Studio.Backend.Export;

internal sealed partial class BackendCpuRenderer
{
    private async Task RunFfmpegAsync(IEnumerable<string> args)
    {
        await using var process = new RenderProcess(ffmpeg, args, token);
        await process.CompleteAsync(token).ConfigureAwait(false);
    }

    private sealed class RenderProcess : IAsyncDisposable
    {
        private readonly Process _process;
        private readonly CancellationTokenRegistration _cancel;
        private readonly Task<string>? _errors;
        private readonly Task? _stdout;
        internal Stream Input => _process.StandardInput.BaseStream;
        internal Stream Output => _process.StandardOutput.BaseStream;
        internal StreamReader Diagnostics => _process.StandardError;

        internal RenderProcess(string ffmpeg, IEnumerable<string> args, CancellationToken token, bool encode = false, bool decode = false)
        {
            token.ThrowIfCancellationRequested();
            var start = new ProcessStartInfo(ffmpeg)
            {
                UseShellExecute = false, CreateNoWindow = true, RedirectStandardInput = encode,
                RedirectStandardOutput = true, RedirectStandardError = true
            };
            foreach (var arg in new[] { "-hide_banner", "-nostdin", "-nostats", "-v", decode ? "info" : "error",
                "-xerror", "-max_alloc", "134217728", "-filter_threads", "1" }.Concat(args))
                start.ArgumentList.Add(arg);
            _process = Process.Start(start) ?? throw new IOException("FFmpeg did not start.");
            _cancel = token.Register(Kill);
            if (!decode)
            {
                _errors = ReadErrorsAsync(Diagnostics);
                _stdout = Output.CopyToAsync(Stream.Null);
            }
        }

        private static async Task<string> ReadErrorsAsync(StreamReader reader)
        {
            var text = new StringBuilder();
            var buffer = new char[1024];
            int count;
            while ((count = await reader.ReadAsync(buffer).ConfigureAwait(false)) > 0)
                text.Append(buffer, 0, Math.Min(count, 8192 - text.Length));
            return text.ToString();
        }

        private void Kill()
        {
            try { if (!_process.HasExited) _process.Kill(entireProcessTree: true); }
            catch (InvalidOperationException) { }
            catch (System.ComponentModel.Win32Exception) { }
        }

        internal async Task CompleteAsync(CancellationToken token)
        {
            await _process.WaitForExitAsync(token).ConfigureAwait(false);
            var errors = _errors is null ? "" : await _errors.ConfigureAwait(false);
            if (_stdout is not null) await _stdout.ConfigureAwait(false);
            if (_process.ExitCode != 0) throw new InvalidDataException($"FFmpeg exited with code {_process.ExitCode}. {errors}");
        }

        public async ValueTask DisposeAsync()
        {
            _cancel.Dispose();
            try
            {
                Kill();
                await _process.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(3)).ConfigureAwait(false);
                if (_errors is not null) await _errors.ConfigureAwait(false);
                if (_stdout is not null) await _stdout.ConfigureAwait(false);
            }
            finally { _process.Dispose(); }
        }
    }
}
