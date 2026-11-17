using System.Diagnostics;
using System.Text.Json.Nodes;
using TrueMoon.Mithril;
using TrueMoon.Tales.Studio.Backend;
using TrueMoon.Tales.Studio.Backend.Http;
using TrueMoon.Tales.Studio.Persistence;
using TrueMoon.Tales.Studio.Tests.Support;
using Xunit;

namespace TrueMoon.Tales.Studio.Tests.Backend;

public sealed class BackendHttpProcessTests
{
    [PythonWriterFact]
    public async Task ServeEntrypoint_AdvertisesScopedApi_CliMovesThroughOwnedChild_AndOwnerExitReapsIt()
    {
        using var fixture = new BackendWriteFixture();
        var store = fixture.Files.Store(); await store.MigrateAsync(await store.OpenAsync(), 11, "fixture");
        var info = new ProcessStartInfo(BackendWriteFixture.Executable())
        {
            UseShellExecute = false, CreateNoWindow = true, WindowStyle = ProcessWindowStyle.Hidden,
            RedirectStandardOutput = true, RedirectStandardError = true
        };
        foreach (var arg in new[] { "--serve-project", fixture.Files.ProjectDirectory, "--config", fixture.Base.ConfigPath }) info.ArgumentList.Add(arg);
        foreach (var key in new[] { "-u", "-p", UnitParentProcessEventsHandler.ControlPipeKey, "TRUEMOON_MITHRIL__DISABLED" }) info.Environment.Remove(key);
        using var parent = new Process { StartInfo = info };
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        parent.Start(); var stderr = parent.StandardError.ReadToEndAsync(deadline.Token);
        Process? child = null;
        try
        {
            var line = await parent.StandardOutput.ReadLineAsync(deadline.Token);
            Assert.True(line is not null, parent.HasExited ? await stderr : "No ready advertisement");
            var ready = JsonNode.Parse(line!)!;
            Assert.Equal(1, ready["api_version"]!.GetValue<int>());
            Assert.Equal(parent.Id, ready["owner_pid"]!.GetValue<int>());
            Assert.Equal("project-synthetic", ready["project_id"]!.GetValue<string>());
            Assert.Equal(StudioBackendService.ProductVersion, ready["product_version"]!.GetValue<string>());
            var address = new Uri(ready["url"]!.GetValue<string>());
            Assert.Equal("127.0.0.1", address.Host);
            var token = ready["token"]!.GetValue<string>(); Assert.Equal(64, token.Length);
            child = Process.GetProcessById(ready["backend_pid"]!.GetValue<int>());
            Assert.NotEqual(parent.Id, child.Id);
            Assert.Equal(Path.GetFullPath(BackendWriteFixture.Executable()), child.MainModule!.FileName, ignoreCase: true);
            using var get = new BackendCliProcess(address, token, "backend-project");
            var read = await get.WaitAsync(); Assert.True(read.Code == 0, read.Error);
            var snapshot = JsonNode.Parse(read.Output)!;
            using var move = new BackendCliProcess(address, token, "backend-move", "--clip", "clip-main", "--start-frame", "14", "--revision", "12", "--sha256", snapshot["fingerprint"]!.GetValue<string>());
            var result = await move.WaitAsync(); Assert.True(result.Code == 0, result.Error);
            Assert.Equal(13, JsonNode.Parse(result.Output)!["project"]!["revision"]!.GetValue<int>());
            Assert.Equal(2, fixture.Files.HistoryFiles.Length);
            parent.Kill(); await parent.WaitForExitAsync(deadline.Token);
            await child.WaitForExitAsync(deadline.Token); Assert.True(child.HasExited);
            using (ProjectWriterLease.Acquire(BackendProjectAccess.Read(fixture.Options))) { }
            using var client = new HttpClient(new SocketsHttpHandler { UseProxy = false });
            await Assert.ThrowsAsync<HttpRequestException>(() => client.GetAsync(new Uri(address, BackendHttpServer.ProjectRoute)));
        }
        finally
        {
            if (!parent.HasExited) { parent.Kill(entireProcessTree: true); await parent.WaitForExitAsync(); }
            if (child is not null) { await child.WaitForExitAsync(deadline.Token); child.Dispose(); }
        }
    }
}
