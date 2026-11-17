using System.Diagnostics;

namespace TrueMoon.Tales.Studio.Backend;

// Retains the exact process handle. Only this decoder and its descendants may be killed.
internal sealed class BackendPlaybackChild : IAsyncDisposable
{
    private readonly Process _process;
    private readonly CancellationTokenRegistration _cancel;
    private readonly Task _errors;
    internal Stream Output => _process.StandardOutput.BaseStream;

    internal BackendPlaybackChild(string executable, IEnumerable<string> arguments, Func<ProcessStartInfo, Process> startProcess, CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        var start = new ProcessStartInfo(executable)
        { UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true, RedirectStandardError = true };
        foreach (var argument in new[] { "-nostdin", "-v", "error", "-threads", "1", "-max_alloc", "67108864" }.Concat(arguments))
            start.ArgumentList.Add(argument);
        _process = startProcess(start);
        _cancel = token.Register(Kill);
        _errors = DrainErrorsAsync(token);
    }

    private async Task DrainErrorsAsync(CancellationToken token)
    {
        var buffer = new byte[4096]; var total = 0;
        try
        {
            while (true)
            {
                var count = await _process.StandardError.BaseStream.ReadAsync(buffer, token).ConfigureAwait(false);
                if (count == 0) return;
                total += count;
                if (total > BackendMediaDecoder.MaxErrorBytes) throw new InvalidDataException("Decoder diagnostic output exceeded its limit.");
            }
        }
        catch { Kill(); throw; }
    }

    internal async Task ReadExactlyAsync(Memory<byte> bytes, CancellationToken token)
    {
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(token);
        deadline.CancelAfter(TimeSpan.FromSeconds(8));
        try
        {
            await Output.ReadExactlyAsync(bytes, deadline.Token).ConfigureAwait(false);
            if (_errors.IsFaulted) await _errors.ConfigureAwait(false);
        }
        catch { Kill(); throw; }
    }

    internal async Task CheckAsync(CancellationToken token)
    {
        await _process.WaitForExitAsync(token).WaitAsync(TimeSpan.FromSeconds(8), token).ConfigureAwait(false);
        await _errors.ConfigureAwait(false);
        if (_process.ExitCode != 0) throw new InvalidDataException($"Playback decoder exited with code {_process.ExitCode}.");
    }

    private void Kill()
    {
        try { if (!_process.HasExited) _process.Kill(entireProcessTree: true); }
        catch (InvalidOperationException) { }
        catch (System.ComponentModel.Win32Exception) { }
    }

    public async ValueTask DisposeAsync()
    {
        _cancel.Dispose();
        try
        {
            Kill();
            await _process.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(3)).ConfigureAwait(false);
            try { await _errors.WaitAsync(TimeSpan.FromSeconds(3)).ConfigureAwait(false); }
            catch (Exception error) when (error is IOException or OperationCanceledException) { }
        }
        finally { _process.Dispose(); }
    }
}
