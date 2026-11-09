using System.Diagnostics;

namespace TrueMoon.Tales.Studio.Media;

// The exact Process handle is retained; cancellation never stops a process by name/PID.
internal sealed class FfmpegChild : IAsyncDisposable
{
    private readonly Process _process;
    private readonly Task<string> _errors;
    private readonly CancellationTokenRegistration _cancel;
    public Stream Output => _process.StandardOutput.BaseStream;

    public FfmpegChild(string executable, IEnumerable<string> arguments, CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        var start = new ProcessStartInfo(executable)
        {
            UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true, RedirectStandardError = true
        };
        foreach (var argument in new[] { "-nostdin", "-v", "error", "-threads", "1" }.Concat(arguments)) start.ArgumentList.Add(argument);
        _process = Process.Start(start) ?? throw new InvalidOperationException("FFmpeg не запустился");
        _errors = _process.StandardError.ReadToEndAsync();
        _cancel = token.Register(Kill);
    }

    private void Kill()
    {
        try { if (!_process.HasExited) _process.Kill(entireProcessTree: true); }
        catch (InvalidOperationException) { }
        catch (System.ComponentModel.Win32Exception) when (_process.HasExited) { }
    }

    public async Task CheckAsync(CancellationToken token)
    {
        await _process.WaitForExitAsync(token).ConfigureAwait(false);
        var errors = await _errors.ConfigureAwait(false);
        if (_process.ExitCode != 0) throw new InvalidDataException($"FFmpeg: {errors[..Math.Min(500, errors.Length)].Trim()}");
    }

    public async ValueTask DisposeAsync()
    {
        _cancel.Dispose();
        try { Kill(); await _process.WaitForExitAsync().ConfigureAwait(false); await _errors.ConfigureAwait(false); }
        finally { _process.Dispose(); }
    }
}
