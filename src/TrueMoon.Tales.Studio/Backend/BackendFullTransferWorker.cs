using System.ComponentModel;
using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text.Json;
using System.Runtime.Versioning;
using TrueMoon.Tales.Studio.Backend.Export;
using TrueMoon.Tales.Studio.Configuration;

namespace TrueMoon.Tales.Studio.Backend;

internal sealed record BackendFullTransferWorker(string Python, string ComfyRoot)
{
    internal const string Command = "--full-transfer-worker";
    internal static string DirectoryPath => Path.Combine(AppContext.BaseDirectory, "FullTransferWorker");

    internal static BackendFullTransferWorker Read(BackendOptions options)
    {
        var config = WorkspaceProfile.Read(options.ConfigPath);
        if (!OperatingSystem.IsWindows() || config.PythonExecutable is not { } python)
            throw new ScenarioEditException("transfer_unavailable");
        python = WorkspaceProfile.ResolvePath(python, options.ConfigPath);
        if (!File.Exists(python)) throw new ScenarioEditException("transfer_unavailable");
        return new(python, ReadComfyRoot(options, config));
    }

    internal static string ReadComfyRoot(BackendOptions options, WorkspaceSettings? settings = null)
    {
        var config = settings ?? WorkspaceProfile.Read(options.ConfigPath);
        if (config.ComfyRoot is not { } root) throw new ScenarioEditException("transfer_unavailable");
        var path = WorkspaceProfile.ResolvePath(root, options.ConfigPath);
        if (!Directory.Exists(path)) throw new ScenarioEditException("transfer_unavailable");
        BackendMediaResolver.CheckNoLinks(path);
        return path;
    }

    internal static Task<int> StartAsync(BackendOptions options, string scratch, string job, string buildSha, CancellationToken token)
    {
        var executable = options.ExecutablePath ?? Environment.ProcessPath ?? throw new IOException("The Studio executable is unavailable.");
        var start = new ProcessStartInfo(executable)
        {
            UseShellExecute = false, CreateNoWindow = true,
            RedirectStandardInput = true, RedirectStandardOutput = true, RedirectStandardError = true
        };
        foreach (var argument in new[] { Command, options.ConfigPath, scratch, job, buildSha })
            start.ArgumentList.Add(argument);
        return BackendExportWorker.RunOwnedAsync(start, scratch, token);
    }

    internal static async Task<int> RunChildAsync(string[] arguments)
    {
        if (!OperatingSystem.IsWindows() || arguments is not [_, var config, var scratch, var job, var buildSha] ||
            !Path.IsPathFullyQualified(config) || !Path.IsPathFullyQualified(scratch) || !Path.IsPathFullyQualified(job) ||
            !BackendConnection.IsHash(buildSha) || !BackendFullTransfer.ValidJobId(Path.GetFileName(job)) ||
            !string.Equals(Path.GetDirectoryName(Path.GetFullPath(scratch)), StudioReviewSession.FullTransferRoot, StringComparison.OrdinalIgnoreCase) ||
            !StudioReviewSession.ValidName(Path.GetFileName(scratch)) || !Console.IsInputRedirected || !Console.IsOutputRedirected)
            return 2;

        using var stop = new CancellationTokenSource();
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(stop.Token);
        deadline.CancelAfter(TimeSpan.FromSeconds(720));
        try
        {
            using var lease = BackendExportWorker.Lock(scratch, "Worker.lock");
            using var input = Console.OpenStandardInput();
            using var output = Console.OpenStandardOutput();
            if (!await BackendExportWorker.WaitForOwnerAsync(input, output, stop).ConfigureAwait(false)) return 2;
            var options = BackendOptions.Create(job, config);
            var worker = Read(options);
            await worker.ExecuteAsync(options, scratch, job, buildSha, deadline.Token).ConfigureAwait(false);
            return 0;
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or InvalidDataException or
            ArgumentException or InvalidOperationException or JsonException or KeyNotFoundException or Win32Exception or
            HttpRequestException or TimeoutException or OperationCanceledException)
        {
            if (error is not OperationCanceledException) Console.Error.WriteLine(error.Message);
            return error is TimeoutException || deadline.IsCancellationRequested && !stop.IsCancellationRequested ? 3 : 1;
        }
    }

    [SupportedOSPlatform("windows")]
    private async Task ExecuteAsync(BackendOptions options, string scratch, string job, string buildSha, CancellationToken token)
    {
        var build = BackendFullTransfer.ConfirmWorkerBuild(scratch, job, buildSha, ComfyRoot);
        var main = Path.Combine(ComfyRoot, "main.py");
        BackendMediaResolver.CheckNoLinks(main);
        if (!File.Exists(main)) throw new InvalidDataException("ComfyUI is not installed in the configured directory.");
        await File.WriteAllTextAsync(Path.Combine(scratch, "worker.pid"), Environment.ProcessId.ToString(), token).ConfigureAwait(false);
        var owner = Convert.ToHexString(RandomNumberGenerator.GetBytes(32));
        var port = PickPort();
        using (var validation = StartPython("validate.py", scratch, owner, port, cpu: true))
        {
            try
            {
                await validation.WaitForExitAsync(token).WaitAsync(TimeSpan.FromSeconds(90), token).ConfigureAwait(false);
                if (validation.ExitCode != 0) throw new InvalidDataException("ComfyUI CPU validation failed.");
            }
            finally { await StopAsync(validation).ConfigureAwait(false); }
        }

        BackendFullTransfer.ConfirmWorkerBuild(scratch, job, buildSha, ComfyRoot);
        using var server = StartPython("server.py", scratch, owner, port, cpu: false);
        using var http = new HttpClient(new SocketsHttpHandler { UseProxy = false, AllowAutoRedirect = false })
        {
            BaseAddress = new Uri($"http://127.0.0.1:{port}"),
            Timeout = Timeout.InfiniteTimeSpan
        };
        http.DefaultRequestHeaders.Add("X-Studio-Owner", owner);
        JsonElement history;
        try
        {
            await File.WriteAllTextAsync(Path.Combine(scratch, "server.pid"), server.Id.ToString(), token).ConfigureAwait(false);
            await WaitForServerAsync(http, server, port, token).ConfigureAwait(false);
            history = await GenerateAsync(http, server, port, scratch, build, token).ConfigureAwait(false);
        }
        finally { await StopAsync(server).ConfigureAwait(false); }

        using var collection = CancellationTokenSource.CreateLinkedTokenSource(token);
        collection.CancelAfter(TimeSpan.FromSeconds(90));
        try
        {
            await BackendFullTransfer.FinishWorkerAsync(scratch, buildSha, build, history,
                BackendMediaReader.Decoder(options), collection.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (!token.IsCancellationRequested)
        { throw new TimeoutException("Full Transfer result collection exceeded 90 seconds."); }
    }

    private Process StartPython(string name, string scratch, string owner, int port, bool cpu)
    {
        var start = new ProcessStartInfo(Python)
        {
            WorkingDirectory = ComfyRoot, UseShellExecute = false, CreateNoWindow = true, RedirectStandardInput = true
        };
        foreach (var argument in new[] { "-I", "-B", Path.Combine(DirectoryPath, name) })
            start.ArgumentList.Add(argument);
        start.Environment["TMT_COMFY_ROOT"] = ComfyRoot;
        start.Environment["TMT_OMNI2_JOB"] = scratch;
        start.Environment["TMT_OMNI2_MODE"] = "api";
        start.Environment["TMT_OMNI2_OWNER"] = owner;
        start.Environment["TMT_OMNI2_PORT"] = port.ToString();
        start.Environment["TMT_OMNI2_INPUT"] = Path.Combine(scratch, "input");
        if (cpu) start.Environment["CUDA_VISIBLE_DEVICES"] = "";
        var process = Process.Start(start) ?? throw new IOException("ComfyUI did not start.");
        process.StandardInput.Close();
        return process;
    }

    private static int PickPort()
    {
        using var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        return ((IPEndPoint)listener.LocalEndpoint).Port;
    }

    private static async Task StopAsync(Process process)
    {
        try { if (!process.HasExited) process.Kill(entireProcessTree: true); }
        catch (InvalidOperationException) when (process.HasExited) { }
        await process.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(4)).ConfigureAwait(false);
    }

    [SupportedOSPlatform("windows")]
    private static async Task WaitForServerAsync(HttpClient http, Process server, int port, CancellationToken token)
    {
        var startup = Stopwatch.StartNew();
        while (startup.Elapsed < TimeSpan.FromSeconds(90))
        {
            if (server.HasExited) throw new IOException("The owned ComfyUI server exited during startup.");
            if (WindowsTcpListener.IsOwned(port, server.Id))
            {
                try
                {
                    await SendAsync(http, server, port, "/system_stats", body: null, token).ConfigureAwait(false);
                    return;
                }
                catch (HttpRequestException) { }
                catch (TimeoutException) { }
            }
            await Task.Delay(250, token).ConfigureAwait(false);
        }
        throw new TimeoutException("The owned ComfyUI server did not become ready in 90 seconds.");
    }

    [SupportedOSPlatform("windows")]
    private static async Task<JsonElement> GenerateAsync(HttpClient http, Process server, int port, string scratch,
        JsonElement build, CancellationToken token)
    {
        var queue = await SendAsync(http, server, port, "/queue", body: null, token).ConfigureAwait(false);
        if (queue.GetProperty("queue_running").GetArrayLength() != 0 || queue.GetProperty("queue_pending").GetArrayLength() != 0)
            throw new InvalidDataException("The owned ComfyUI queue is not empty.");
        var payload = await BackendFullTransfer.RecordDispatchAsync(scratch, build.GetProperty("id").GetString()!, token)
            .ConfigureAwait(false);
        var response = await SendAsync(http, server, port, "/prompt", payload, token).ConfigureAwait(false);
        if (!Guid.TryParse(response.GetProperty("prompt_id").GetString(), out var promptId))
            throw new InvalidDataException("ComfyUI returned an invalid prompt ID.");

        var generation = Stopwatch.StartNew();
        var heartbeat = Path.Combine(scratch, "Runtime.json");
        var observed = File.GetLastWriteTimeUtc(heartbeat);
        var lastProgress = TimeSpan.Zero;
        while (generation.Elapsed < TimeSpan.FromSeconds(480) && generation.Elapsed - lastProgress < TimeSpan.FromSeconds(180))
        {
            var history = await SendAsync(http, server, port, $"/history/{promptId:D}", body: null, token).ConfigureAwait(false);
            if (history.TryGetProperty(promptId.ToString("D"), out var entry)) return entry;
            var updated = File.GetLastWriteTimeUtc(heartbeat);
            if (updated != observed)
            {
                observed = updated;
                lastProgress = generation.Elapsed;
            }
            await Task.Delay(1000, token).ConfigureAwait(false);
        }
        throw new TimeoutException("Full Transfer exceeded its generation or progress deadline.");
    }

    [SupportedOSPlatform("windows")]
    private static async Task<JsonElement> SendAsync(HttpClient http, Process server, int port, string path,
        byte[]? body, CancellationToken token)
    {
        if (server.HasExited || !WindowsTcpListener.IsOwned(port, server.Id))
            throw new IOException("The owned ComfyUI server is no longer listening.");
        using var request = new HttpRequestMessage(body is null ? HttpMethod.Get : HttpMethod.Post, path);
        if (body is not null)
        {
            request.Headers.ConnectionClose = true;
            request.Content = new ByteArrayContent(body);
            request.Content.Headers.ContentType = new("application/json");
        }
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(token);
        deadline.CancelAfter(TimeSpan.FromSeconds(5));
        try
        {
            using var response = await http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, deadline.Token).ConfigureAwait(false);
            response.EnsureSuccessStatusCode();
            await response.Content.LoadIntoBufferAsync(1024 * 1024, deadline.Token).ConfigureAwait(false);
            var bytes = await response.Content.ReadAsByteArrayAsync(deadline.Token).ConfigureAwait(false);
            using var document = JsonDocument.Parse(bytes);
            return document.RootElement.Clone();
        }
        catch (OperationCanceledException) when (!token.IsCancellationRequested)
        { throw new TimeoutException("The owned ComfyUI server did not answer in time."); }
    }
}
