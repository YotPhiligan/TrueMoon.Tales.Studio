using System.ComponentModel;
using System.Diagnostics;
using System.Text.Json;
using TrueMoon.Tales.Studio.Backend.Export;

namespace TrueMoon.Tales.Studio.Backend;

// All commands and disposal are serialized by StudioBackendService's project gate.
internal sealed class BackendFullTransferRuns : IDisposable
{
    private sealed record End(string BuildSha256, string State, string? Detail);
    private sealed record Active(BackendFullTransfer.RunJob Job, CancellationTokenSource Stop, Task<End> Completion);
    private Active? _active;
    internal bool Busy => _active is { Completion.IsCompleted: false };

    internal async Task<FullTransferRunState> LaunchAsync(BackendFullTransfer.RunJob job, BackendFullTransferWorker worker,
        Func<string, Task> stage, Func<CancellationToken, Task<string?>> register, CancellationToken token)
    {
        if (_active is { Completion.IsCompleted: false }) throw new ScenarioEditException("transfer_run_busy");
        Reap();
        var script = Path.Combine(Path.GetDirectoryName(worker.Script)!, "ManagedRun.py");
        BackendMediaResolver.CheckNoLinks(script);
        var scratch = new StudioReviewSession(StudioReviewSession.FullTransferRoot);
        try
        {
            scratch.Open();
            await stage(Path.Combine(scratch.DirectoryPath, "input")).ConfigureAwait(false);
            var buildSha = await BackendFullTransfer.FreezeRunAsync(job, worker, script, scratch.DirectoryPath, token).ConfigureAwait(false);
            // The durable claim survives the scratch directory.
            var stop = new CancellationTokenSource();
            _active = new(job, stop, RunAsync(job, worker, script, buildSha, scratch, register, stop.Token));
        }
        catch { scratch.Dispose(); throw; }
        return Read(job);
    }

    internal async Task<FullTransferRunState?> StopOwnedAsync(string id, string requestSha)
    {
        if (_active is not { Completion.IsCompleted: false } active || active.Job.Id != id ||
            !string.Equals(active.Job.Receipt.RequestSha256, requestSha, StringComparison.OrdinalIgnoreCase)) return null;
        active.Stop.Cancel();
        var identity = Identity(active.Job);
        try
        {
            var end = await active.Completion.WaitAsync(TimeSpan.FromSeconds(12)).ConfigureAwait(false);
            return identity with { State = end.State, Detail = end.Detail,
                OutputSha256 = end.State == "completed" ? BackendFullTransfer.RunOutput(active.Job).Sha : null };
        }
        catch (TimeoutException) { return identity with { State = "stopping" }; }
    }

    internal FullTransferRunState Read(BackendFullTransfer.RunJob job)
    {
        Reap();
        var identity = Identity(job);
        if (!Directory.Exists(job.Run)) return identity;
        var buildSha = BackendFullTransfer.RunBuildSha(job);
        if (_active is { } active && active.Job.Run == job.Run)
            return identity with { State = active.Stop.IsCancellationRequested ? "stopping" : "running" };
        if (ReadEnd(job.Run) is { } end)
        {
            if (end.BuildSha256 != buildSha || end.State is not ("failed" or "cancelled" or "timed_out" or "completed"))
                throw new ScenarioEditException("transfer_damaged");
            if (end.State != "completed")
                return BackendFullTransfer.HasPublishedOutput(job) ? Completed(null) : identity with { State = end.State, Detail = end.Detail };
            return Completed(end.Detail);
        }
        var completion = Path.Combine(job.Run, "Completed.json");
        BackendMediaResolver.CheckNoLinks(completion, allowMissing: true);
        return BackendFullTransfer.HasPublishedOutput(job) || File.Exists(completion) ? Completed(null) : identity with { State = "interrupted" };

        FullTransferRunState Completed(string? detail) => identity with { State = "completed", OutputSha256 = BackendFullTransfer.RunOutput(job).Sha, Detail = detail };
    }

    private static FullTransferRunState Identity(BackendFullTransfer.RunJob job) =>
        new(1, job.Id, job.Request.Draft.TakeId, job.Receipt.RequestSha256, job.Staged ? "staged" : "prepared", null, null);

    private static End? ReadEnd(string directory)
    {
        var path = Path.Combine(directory, "End.json");
        BackendMediaResolver.CheckNoLinks(path, allowMissing: true);
        if (!File.Exists(path)) return null;
        if (new FileInfo(path).Length > 4096) throw new ScenarioEditException("transfer_damaged");
        return JsonSerializer.Deserialize<End>(File.ReadAllBytes(path), BackendAdaptation.Json)
            ?? throw new ScenarioEditException("transfer_damaged");
    }

    private static async Task<End> RunAsync(BackendFullTransfer.RunJob job, BackendFullTransferWorker worker, string script,
        string buildSha, StudioReviewSession scratch, Func<CancellationToken, Task<string?>> register, CancellationToken stop)
    {
        using var workspace = scratch;
        var end = new End(buildSha, "failed", "runner_failed");
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(stop);
        deadline.CancelAfter(TimeSpan.FromSeconds(725));
        try
        {
            var start = new ProcessStartInfo(worker.Python)
            {
                WorkingDirectory = Path.GetDirectoryName(script)!, UseShellExecute = false, CreateNoWindow = true,
                RedirectStandardInput = true
            };
            foreach (var argument in new[] { "-I", "-B", script, scratch.DirectoryPath, worker.ComfyRoot, buildSha, job.Directory })
                start.ArgumentList.Add(argument);
            start.Environment["TMT_COMFY_ROOT"] = worker.ComfyRoot;
            using var process = Process.Start(start) ?? throw new IOException("The Full Transfer worker did not start.");
            try
            {
                await process.WaitForExitAsync(deadline.Token).ConfigureAwait(false);
                if (process.ExitCode == 0)
                {
                    await BackendFullTransfer.PublishOutputAsync(job, scratch.DirectoryPath, deadline.Token).ConfigureAwait(false);
                    // A confirmed result is recoverable if writer admission is interrupted.
                    end = new(buildSha, "completed", "registration_pending");
                    end = end with { Detail = await register(deadline.Token).ConfigureAwait(false) };
                }
                else if (process.ExitCode == 3) end = new(buildSha, "timed_out", null);
            }
            finally
            {
                if (!process.HasExited)
                {
                    process.StandardInput.Dispose();
                    try { await process.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(4)).ConfigureAwait(false); }
                    catch (TimeoutException)
                    {
                        try { process.Kill(entireProcessTree: true); }
                        catch (InvalidOperationException) when (process.HasExited) { }
                        await process.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(4)).ConfigureAwait(false);
                    }
                }
            }
        }
        catch (OperationCanceledException) when (stop.IsCancellationRequested) { if (end.State != "completed") end = new(buildSha, "cancelled", null); }
        catch (OperationCanceledException) when (deadline.IsCancellationRequested) { if (end.State != "completed") end = new(buildSha, "timed_out", null); }
        catch (Exception error) when (error is IOException or Win32Exception or UnauthorizedAccessException or TimeoutException or
                                      InvalidOperationException or JsonException or KeyNotFoundException or ScenarioEditException)
        { if (end.State != "completed") end = new(buildSha, "failed", "runner_failed"); }
        try { await BackendFullTransfer.WriteRunEndAsync(job, end).ConfigureAwait(false); }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException) { /* An orphaned intent remains non-replayable. */ }
        if (end.State != "completed" && File.Exists(Path.Combine(scratch.DirectoryPath, "Completed.json")))
            scratch.LeaveForRecovery();
        return end;
    }

    private void Reap()
    {
        if (_active is not { Completion.IsCompleted: true } finished) return;
        _ = finished.Completion.Exception;
        finished.Stop.Dispose();
        _active = null;
    }

    public void Dispose()
    {
        if (_active is not { } active) return;
        active.Stop.Cancel();
        try { active.Completion.GetAwaiter().GetResult(); }
        finally { Reap(); }
    }
}
