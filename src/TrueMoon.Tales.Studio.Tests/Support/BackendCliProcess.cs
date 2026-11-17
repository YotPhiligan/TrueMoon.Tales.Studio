using System.Diagnostics;
using System.Text;
using TrueMoon.Tales.Studio.Tests.Backend;

namespace TrueMoon.Tales.Studio.Tests.Support;

// Owns only its own CLI process; callers may pump a UI owner until Completed instead of awaiting on it.
internal sealed class BackendCliProcess : IDisposable
{
    private readonly Process _process;
    private readonly Task<string> _output, _error;
    internal bool Completed => _process.HasExited && _output.IsCompleted && _error.IsCompleted;
    internal BackendCliProcess(Uri address, string token, params string[] args)
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (!File.Exists(Path.Combine(directory.FullName, "TrueMoon.Tales.Studio.slnx"))) directory = directory.Parent!;
        var info = new ProcessStartInfo(Environment.GetEnvironmentVariable("STUDIO_TEST_PYTHON") ?? throw new InvalidOperationException("Select STUDIO_TEST_PYTHON."))
        {
            UseShellExecute = false, CreateNoWindow = true, WindowStyle = ProcessWindowStyle.Hidden,
            RedirectStandardOutput = true, RedirectStandardError = true,
            StandardOutputEncoding = Encoding.UTF8, StandardErrorEncoding = Encoding.UTF8
        };
        foreach (var arg in new[] { "-s", "-B", Path.Combine(directory.Parent!.FullName, "studio_agent.py"), "--url", address.AbsoluteUri, "--token", token }.Concat(args))
            info.ArgumentList.Add(arg);
        _process = new() { StartInfo = info };
        try { _process.Start(); _output = _process.StandardOutput.ReadToEndAsync(); _error = _process.StandardError.ReadToEndAsync(); }
        catch { _process.Dispose(); throw; }
    }
    internal (int Code, string Output, string Error) Result()
    {
        if (!Completed) throw new InvalidOperationException("Pump or await CLI completion before reading results.");
        return (_process.ExitCode, _output.GetAwaiter().GetResult(), _error.GetAwaiter().GetResult());
    }
    internal async Task<(int Code, string Output, string Error)> WaitAsync()
    {
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(20));
        await _process.WaitForExitAsync(deadline.Token);
        await Task.WhenAll(_output, _error).WaitAsync(deadline.Token);
        return Result();
    }
    public void Dispose()
    {
        try { if (!_process.HasExited) { _process.Kill(entireProcessTree: true); _process.WaitForExit(5000); } }
        finally { _process.Dispose(); }
    }
}
