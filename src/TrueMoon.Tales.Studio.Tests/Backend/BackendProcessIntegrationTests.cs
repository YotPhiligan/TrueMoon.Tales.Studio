using System.Diagnostics;
using System.Text.Json;
using System.Text.RegularExpressions;
using TrueMoon;
using TrueMoon.Dependencies;
using TrueMoon.Diagnostics;
using TrueMoon.Enerit;
using TrueMoon.Enerit.IO.Pipes;
using TrueMoon.Mithril;
using TrueMoon.Tales.Studio.Backend;
using Xunit;

namespace TrueMoon.Tales.Studio.Tests.Backend;

public sealed class BackendProcessIntegrationTests
{
    [Fact]
    public async Task SameExe_MithrilLaunchesBackend_EneritOpensProject_AndShutdownReapsChild()
    {
        using var fixture = new BackendFixture();
        var result = await InspectAsync(fixture);
        Assert.True(result.ExitCode == 0, result.Error);
        using var output = JsonDocument.Parse(result.Output);
        var identity = output.RootElement.GetProperty("Identity");
        var childId = identity.GetProperty("ProcessId").GetInt32();
        Assert.NotEqual(result.ParentId, childId);
        Assert.Equal(result.ParentId, identity.GetProperty("ParentProcessId").GetInt32());
        Assert.Equal("backend", identity.GetProperty("Role").GetString());
        Assert.Equal(StudioBackendService.ProductVersion, identity.GetProperty("ProductVersion").GetString());
        Assert.Equal(1, output.RootElement.GetProperty("SchemaVersion").GetInt32());
        var project = output.RootElement.GetProperty("Project");
        Assert.Equal("project-synthetic", project.GetProperty("Id").GetString());
        Assert.Equal(11, project.GetProperty("Revision").GetInt64());
        Assert.Equal("clip-main", project.GetProperty("Montage").GetProperty("Clips")[0].GetProperty("Id").GetString());
        Assert.Contains($"Started pid={childId}", result.Error);
        Assert.Contains("Stopped", result.Error);
        AssertAllChildrenExited(result);
        Assert.Equal(fixture.Files.InitialBytes, File.ReadAllBytes(fixture.Files.ProjectPath));
        Assert.Empty(fixture.Files.HistoryFiles);
        Assert.False(File.Exists(fixture.Files.JournalPath));
        Assert.False(File.Exists(Path.Combine(fixture.Files.ProjectDirectory, ".Studio.project.lock")));
    }

    [Theory]
    [InlineData("missing_directory")]
    [InlineData("invalid_project")]
    [InlineData("pending_recovery")]
    public async Task FailedStartupOrRead_ReturnsFailure_AndReapsOnlyOwnedChild(string mode)
    {
        using var fixture = new BackendFixture();
        switch (mode)
        {
            case "missing_directory": Directory.Delete(fixture.Files.ProjectDirectory, true); break;
            case "invalid_project": File.WriteAllText(fixture.Files.ProjectPath, "{"); break;
            case "pending_recovery": File.WriteAllText(fixture.Files.JournalPath, "{}"); break;
        }
        var result = await InspectAsync(fixture);
        Assert.Equal(1, result.ExitCode);
        Assert.Empty(result.Output);
        Assert.Contains("Studio:", result.Error);
        Assert.Contains("Started pid=", result.Error);
        AssertAllChildrenExited(result);
        if (mode == "invalid_project") Assert.Equal("{", File.ReadAllText(fixture.Files.ProjectPath));
        if (mode == "pending_recovery")
        {
            Assert.Equal("{}", File.ReadAllText(fixture.Files.JournalPath));
            Assert.Equal(fixture.Files.InitialBytes, File.ReadAllBytes(fixture.Files.ProjectPath));
        }
        Assert.Empty(fixture.Files.HistoryFiles);
    }

    [Fact]
    public async Task BackendRole_RejectsUnmanagedLaunchWithoutCreatingFiles()
    {
        using var fixture = new BackendFixture();
        var result = await RunAsync(["--backend", fixture.Files.ProjectDirectory, fixture.ConfigPath,
            fixture.Options.Endpoint, fixture.Options.SessionKey]);
        Assert.Equal(2, result.ExitCode);
        Assert.Empty(result.Output);
        Assert.DoesNotContain("Started pid=", result.Error);
        Assert.Equal(fixture.Files.InitialBytes, File.ReadAllBytes(fixture.Files.ProjectPath));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ParentExit_BackendObservesBorrowedOwnerAndExitsWithoutTouchingProject(bool crash)
    {
        using var fixture = new BackendFixture();
        var helperPath = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Windows),
            "System32", "WindowsPowerShell", "v1.0", "powershell.exe");
        using var parent = new Process { StartInfo = StartInfo(helperPath, ["-NoProfile", "-NonInteractive", "-Command",
            "[Console]::Out.WriteLine('READY'); [Console]::In.ReadLine() | Out-Null"]) };
        using var backend = new Process { StartInfo = StartInfo(Executable(), ["--backend", fixture.Files.ProjectDirectory,
            fixture.ConfigPath, fixture.Options.Endpoint, fixture.Options.SessionKey]) };
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(20));
        parent.Start();
        try
        {
            Assert.Equal("READY", await parent.StandardOutput.ReadLineAsync(deadline.Token));
            backend.StartInfo.Environment["-u"] = "1";
            backend.StartInfo.Environment["-p"] = parent.Id.ToString();
            backend.StartInfo.Environment[UnitParentProcessEventsHandler.ControlPipeKey] = "studio-test-" + Guid.NewGuid().ToString("N");
            backend.Start();
            var output = backend.StandardOutput.ReadToEndAsync(deadline.Token);
            var error = backend.StandardError.ReadToEndAsync(deadline.Token);
            await using var app = App.Builder(_ => { }).Setup(context => context
                .UseDiagnostics()
                .PipesServiceTransport(_ => fixture.Options.Endpoint)
                .UseInvocationService<IStudioBackend>()).Build();
            try
            {
                await app.StartAsync(deadline.Token);
                var service = app.Services.Resolve<IStudioBackend>()!;
                var identity = await service.HelloAsync(fixture.Options.SessionKey, deadline.Token);
                Assert.Equal(backend.Id, identity.ProcessId);
                Assert.Equal(parent.Id, identity.ParentProcessId);
                await Assert.ThrowsAsync<InvocationException>(() => service.OpenProjectAsync("wrong-session", deadline.Token));
                var initial = await service.OpenProjectAsync(fixture.Options.SessionKey, deadline.Token);
                Assert.Equal(11, BackendConnection.Decode(identity, initial).Project.Revision);
                await app.StopAsync();
                var changed = System.Text.Json.Nodes.JsonNode.Parse(File.ReadAllText(fixture.Files.ProjectPath))!;
                changed["revision"] = 12;
                File.WriteAllText(fixture.Files.ProjectPath, changed.ToJsonString());
                var externalBytes = File.ReadAllBytes(fixture.Files.ProjectPath);
                await using (var reconnected = App.Builder(_ => { }).Setup(context => context.UseDiagnostics()
                    .PipesServiceTransport(_ => fixture.Options.Endpoint).UseInvocationService<IStudioBackend>()).Build())
                {
                    try
                    {
                        await reconnected.StartAsync(deadline.Token);
                        var fresh = await reconnected.Services.Resolve<IStudioBackend>()!.OpenProjectAsync(fixture.Options.SessionKey, deadline.Token);
                        Assert.Equal(12, BackendConnection.Decode(identity, fresh).Project.Revision);
                    }
                    finally { await reconnected.StopAsync(); }
                }
                // A different Studio controller may stop only its own child, even with the same EXE name.
                using (var other = new BackendFixture())
                {
                    var otherResult = await InspectAsync(other);
                    Assert.True(otherResult.ExitCode == 0, otherResult.Error);
                    AssertAllChildrenExited(otherResult);
                    Assert.False(backend.HasExited);
                }
                if (crash) parent.Kill();
                else
                {
                    await parent.StandardInput.WriteLineAsync("");
                    await parent.StandardInput.FlushAsync(deadline.Token);
                }
                await parent.WaitForExitAsync(deadline.Token);
                await backend.WaitForExitAsync(deadline.Token);
                Assert.Equal(0, backend.ExitCode);
                Assert.Empty(await output);
                Assert.Empty(await error);
                Assert.Equal(externalBytes, File.ReadAllBytes(fixture.Files.ProjectPath));
                Assert.Empty(fixture.Files.HistoryFiles);
            }
            finally { await app.StopAsync(); }
        }
        finally
        {
            await ReapAsync(backend);
            await ReapAsync(parent);
        }
    }

    private static Task<RunResult> InspectAsync(BackendFixture fixture) => RunAsync(
        ["--inspect-project", fixture.Files.ProjectDirectory, "--config", fixture.ConfigPath]);

    private static async Task<RunResult> RunAsync(string[] arguments)
    {
        using var process = new Process { StartInfo = StartInfo(Executable(), arguments) };
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(25));
        process.Start();
        var id = process.Id;
        try
        {
            var output = process.StandardOutput.ReadToEndAsync(deadline.Token);
            var error = process.StandardError.ReadToEndAsync(deadline.Token);
            await process.WaitForExitAsync(deadline.Token);
            return new(id, process.ExitCode, await output, await error);
        }
        finally { await ReapAsync(process); }
    }

    private static ProcessStartInfo StartInfo(string executable, string[] args)
    {
        var info = new ProcessStartInfo(executable)
        {
            UseShellExecute = false, CreateNoWindow = true, WindowStyle = ProcessWindowStyle.Hidden,
            RedirectStandardOutput = true, RedirectStandardError = true, RedirectStandardInput = true
        };
        foreach (var arg in args) info.ArgumentList.Add(arg);
        foreach (var key in new[] { "-u", "-p", UnitParentProcessEventsHandler.ControlPipeKey, "TRUEMOON_MITHRIL__DISABLED" })
            info.Environment.Remove(key);
        return info;
    }

    private static string Executable()
    {
        if (Environment.GetEnvironmentVariable("STUDIO_BACKEND_EXE") is { Length: > 0 } executable) return executable;
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        var configuration = directory.Name;
        while (!File.Exists(Path.Combine(directory.FullName, "TrueMoon.Tales.Studio.slnx")))
            directory = directory.Parent ?? throw new InvalidOperationException("Studio source root not found.");
        return Path.Combine(directory.FullName, ".local", "studio-build", "bin", "TrueMoon.Tales.Studio", configuration, "TrueMoon.Tales.Studio.exe");
    }

    private static async Task ReapAsync(Process process)
    {
        try
        {
            if (!process.HasExited) process.Kill(entireProcessTree: true);
            await process.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(5));
        }
        catch (InvalidOperationException) { } // Start failed; there is no owned OS process.
    }

    private static void AssertAllChildrenExited(RunResult result)
    {
        var ids = Regex.Matches(result.Error, @"Started pid=(\d+)").Select(match => int.Parse(match.Groups[1].Value)).ToArray();
        Assert.NotEmpty(ids);
        foreach (var id in ids)
        {
            try
            {
                using var child = Process.GetProcessById(id);
                Assert.True(child.HasExited, $"Owned backend {id} survived parent shutdown.");
            }
            catch (ArgumentException) { } // Reaped PID is absent.
        }
    }

    private sealed record RunResult(int ParentId, int ExitCode, string Output, string Error);
}
