using System.Diagnostics;
using System.Globalization;
using System.Text.Json;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using TrueMoon.Tales.Studio.Backend;
using TrueMoon.Tales.Studio.Backend.Http;
using TrueMoon.Tales.Studio.Persistence;
using Xunit;

namespace TrueMoon.Tales.Studio.Tests.Backend;

public sealed class BackendNativeCliTests
{
    private static string Fingerprint => new('A', 64);

    [Fact]
    public async Task NativeMove_UsesExistingWriter_AndConflictsAndNoopDoNotWrite()
    {
        await using var fixture = new BackendHttpFixture();
        await fixture.StartAsync();
        var files = fixture.Files.Files;
        var before = File.ReadAllBytes(files.ProjectPath);
        var original = await fixture.Connection.OpenAsync();

        var initial = Snapshot(await RunAsync(fixture.Http.Address, fixture.Http.Token, "backend-project"));
        Assert.Equal(original.Project.Id, initial.Project.Id);
        Assert.Equal(original.Fingerprint, initial.Fingerprint);
        Assert.Equal(before, File.ReadAllBytes(files.ProjectPath));
        Assert.Single(files.HistoryFiles);
        Assert.Throws<ProjectStoreBusyException>(() => ProjectWriterLease.Acquire(BackendProjectAccess.Read(fixture.Files.Options)));

        var moved = Snapshot(await MoveAsync(initial.Project.Revision, initial.Fingerprint));
        var clip = Assert.Single(moved.Project.Montage.Clips, row => row.Id == "clip-main");
        Assert.Equal(14, clip.StartFrame);
        Assert.Equal(initial.Project.Revision + 1, moved.Project.Revision);
        Assert.NotEqual(initial.Fingerprint, moved.Fingerprint);
        Assert.Equal(original.Identity.ProcessId, (await fixture.Connection.OpenAsync()).Identity.ProcessId);
        Assert.Equal(2, files.HistoryFiles.Length);
        Assert.Contains(files.HistoryFiles, path => File.ReadAllBytes(path).SequenceEqual(before));
        var committed = File.ReadAllBytes(files.ProjectPath);

        AssertError(await MoveAsync(initial.Project.Revision, moved.Fingerprint), 3, "revision_conflict", reconcile: true);
        AssertError(await MoveAsync(moved.Project.Revision, initial.Fingerprint), 3, "revision_conflict", reconcile: true);
        Assert.Equal(moved.Fingerprint, Snapshot(await MoveAsync(moved.Project.Revision, moved.Fingerprint)).Fingerprint);
        Assert.Equal(committed, File.ReadAllBytes(files.ProjectPath));
        Assert.Equal(2, files.HistoryFiles.Length);

        Task<CliResult> MoveAsync(long revision, string sha) => RunAsync(fixture.Http.Address, fixture.Http.Token,
            "backend-move", "--clip", "clip-main", "--start-frame", "14",
            "--revision", revision.ToString(CultureInfo.InvariantCulture), "--sha256", sha);
    }

    [BackendMediaFact]
    public async Task NativeTrim_ReturnsCommittedExtent_AndRejectsAStaleRepeat()
    {
        await using var fixture = new BackendMediaFixture();
        await fixture.StartAsync();
        var before = File.ReadAllBytes(fixture.Files.ProjectPath);
        string[] trim = ["backend-trim", "--clip", "clip-main", "--source-in", "8", "--source-out", "48", "--start", "14",
            "--revision", fixture.Baseline.Project.Revision.ToString(CultureInfo.InvariantCulture), "--sha256", fixture.Baseline.Fingerprint];
        var result = Snapshot(await RunAsync(fixture.Backend.Http.Address, fixture.Backend.Http.Token, trim));
        var clip = Assert.Single(result.Project.Montage.Clips, row => row.Id == "clip-main");
        Assert.Equal((14, 20, 8d, 48d), (clip.StartFrame, clip.DurationFrames, clip.SourceInFrame, clip.SourceOutFrame!.Value));
        var committed = File.ReadAllBytes(fixture.Files.ProjectPath);
        Assert.Equal(result.Fingerprint, ProjectFileCodec.Read(committed).Fingerprint);
        AssertError(await RunAsync(fixture.Backend.Http.Address, fixture.Backend.Http.Token, trim), 3, "revision_conflict", reconcile: true);
        Assert.Equal(committed, File.ReadAllBytes(fixture.Files.ProjectPath));
        Assert.Equal(2, fixture.Files.HistoryFiles.Length);
        Assert.Contains(fixture.Files.HistoryFiles, path => File.ReadAllBytes(path).SequenceEqual(before));
    }

    [Theory]
    [InlineData("http://localhost:8080/")]
    [InlineData("http://127.1:8080/")]
    [InlineData("http://127.0.0.1:8080/api/")]
    [InlineData("http://127.0.0.1:8080/?scope=project")]
    public async Task NativeProject_RefusesNoncanonicalServerAddresses(string url)
    {
        AssertError(await RunAsync(new Uri(url), Fingerprint, "backend-project"), 2, "invalid_arguments", reconcile: false);
    }

    [Fact]
    public async Task NativeMove_RequiresBaselineBeforeContactingServer()
    {
        AssertError(await RunAsync(new Uri("http://127.0.0.1:1/"), Fingerprint, "backend-move",
            "--clip", "clip-main", "--start-frame", "14", "--revision", "12"), 2, "invalid_arguments", reconcile: false);
    }

    [Theory]
    [InlineData("redirect", "backend_http_error")]
    [InlineData("lost", "backend_unavailable")]
    [InlineData("duplicate-json", "invalid_response")]
    public async Task NativeWrite_SendsOnePostAndRequiresReconciliationWhenTheReplyIsUnknown(string mode, string error)
    {
        var builder = WebApplication.CreateSlimBuilder(new WebApplicationOptions { Args = [] });
        builder.Logging.ClearProviders();
        builder.WebHost.UseKestrel(options => options.Listen(System.Net.IPAddress.Loopback, 0));
        await using var app = builder.Build();
        var count = 0;
        string? method = null, route = null;
        app.Run(async context =>
        {
            Interlocked.Increment(ref count);
            method = context.Request.Method;
            route = context.Request.Path;
            await context.Request.Body.CopyToAsync(Stream.Null, context.RequestAborted);
            if (mode == "lost") { context.Abort(); return; }
            if (mode == "redirect")
            {
                context.Response.StatusCode = StatusCodes.Status307TemporaryRedirect;
                context.Response.Headers.Location = "/redirected";
                return;
            }
            context.Response.ContentType = "application/json";
            await context.Response.WriteAsync("""{"api_version":1,"api_version":1}""");
        });
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(25));
        await app.StartAsync(deadline.Token);
        try
        {
            var addresses = app.Services.GetRequiredService<IServer>().Features.Get<IServerAddressesFeature>()!;
            var result = await RunAsync(new Uri(addresses.Addresses.Single()), Fingerprint,
                "backend-move", "--clip", "clip-main", "--start-frame", "14", "--revision", "12", "--sha256", Fingerprint);
            AssertError(result, 1, error, reconcile: true);
            Assert.Equal(1, Volatile.Read(ref count));
            Assert.Equal(HttpMethods.Post, method);
            Assert.Equal(BackendHttpServer.MoveRoute, route);
        }
        finally { await app.StopAsync(deadline.Token); }
    }

    private static BackendHttpSnapshot Snapshot(CliResult result)
    {
        Assert.True(result.Code == 0, result.Error);
        Assert.Empty(result.Error);
        return JsonSerializer.Deserialize<BackendHttpSnapshot>(result.Output, BackendHttpServer.Json)!;
    }

    private static void AssertError(CliResult result, int code, string expected, bool reconcile)
    {
        Assert.Equal(code, result.Code);
        Assert.Empty(result.Output);
        using var payload = JsonDocument.Parse(result.Error);
        Assert.Equal(expected, payload.RootElement.GetProperty("error").GetString());
        Assert.Equal(reconcile, payload.RootElement.GetProperty("reconciliation_required").GetBoolean());
        Assert.False(payload.RootElement.GetProperty("automatic_retry").GetBoolean());
    }

    private static async Task<CliResult> RunAsync(Uri address, string token, params string[] args)
    {
        var start = new ProcessStartInfo(BackendWriteFixture.Executable())
        {
            UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true, RedirectStandardError = true
        };
        foreach (var arg in args.Concat(["--url", address.OriginalString])) start.ArgumentList.Add(arg);
        start.Environment["STUDIO_BACKEND_HTTP_TOKEN"] = token;
        using var child = Process.Start(start) ?? throw new IOException("Studio CLI did not start.");
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(20));
        var output = child.StandardOutput.ReadToEndAsync(deadline.Token);
        var error = child.StandardError.ReadToEndAsync(deadline.Token);
        try
        {
            await child.WaitForExitAsync(deadline.Token);
            return new(child.ExitCode, await output, await error);
        }
        finally
        {
            if (!child.HasExited) child.Kill(entireProcessTree: true);
            await child.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(5));
        }
    }

    private sealed record CliResult(int Code, string Output, string Error);
}
