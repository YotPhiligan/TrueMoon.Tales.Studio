using System.Net;
using System.Net.Http.Json;
using System.Net.Sockets;
using System.Text;
using System.Text.Json.Nodes;
using TrueMoon.Dependencies;
using TrueMoon.Tales.Studio.Backend;
using TrueMoon.Tales.Studio.Backend.Http;
using TrueMoon.Tales.Studio.Persistence;
using Xunit;

namespace TrueMoon.Tales.Studio.Tests.Backend;

public sealed class BackendHttpTests
{
    [Theory]
    [InlineData("invalid_move", 422)]
    [InlineData("recovery_required", 409)]
    [InlineData("backend_exit", 503)]
    public async Task Http_ReportsDomainRecoveryAndDisconnectedBackend_WithoutRetryOrMutation(string mode, int status)
    {
        await using var fixture = new BackendHttpFixture(); await fixture.StartAsync();
        using var client = fixture.Client();
        var opened = await fixture.Connection.OpenAsync();
        var before = File.ReadAllBytes(fixture.Files.Files.ProjectPath);
        if (mode == "recovery_required") File.WriteAllText(fixture.Files.Files.JournalPath, "{}");
        if (mode == "backend_exit")
        {
            var owned = fixture.App.Services.Resolve<BackendProcessFactory>()!.Owned!;
            Assert.True(owned.IsServing(opened.Identity.ProcessId));
            using var child = System.Diagnostics.Process.GetProcessById(opened.Identity.ProcessId);
            child.Kill(entireProcessTree: true); using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(10));
            await child.WaitForExitAsync(deadline.Token);
        }
        using var response = mode == "invalid_move"
            ? await client.PostAsJsonAsync(BackendHttpServer.MoveRoute, new BackendHttpMove { ClipId = "clip-main", StartFrame = 217, ExpectedRevision = 12, Fingerprint = opened.Fingerprint }, BackendHttpServer.Json)
            : await client.GetAsync(BackendHttpServer.ProjectRoute);
        Assert.Equal(status, (int)response.StatusCode);
        var failure = await response.Content.ReadFromJsonAsync<BackendHttpFailure>(BackendHttpServer.Json);
        Assert.Equal(mode == "backend_exit" ? "backend_unavailable" : mode, failure!.Error);
        Assert.True(failure.ReconciliationRequired);
        Assert.Equal(before, File.ReadAllBytes(fixture.Files.Files.ProjectPath)); Assert.Single(fixture.Files.Files.HistoryFiles);
        if (mode == "recovery_required") Assert.Equal("{}", File.ReadAllText(fixture.Files.Files.JournalPath));
    }

    [Fact]
    public async Task HttpMove_UsesSharedWriterAndExactBaseline_OneHistoryAndNoop_RejectsRevisionAndShaIndependently()
    {
        await using var fixture = new BackendHttpFixture(); await fixture.StartAsync();
        using var client = fixture.Client();
        var opened = await client.GetFromJsonAsync<BackendHttpSnapshot>(BackendHttpServer.ProjectRoute, BackendHttpServer.Json);
        Assert.NotNull(opened); Assert.Equal(1, opened.ApiVersion); Assert.Equal(2, opened.SchemaVersion); Assert.Equal(12, opened.Project.Revision);
        Assert.Throws<ProjectStoreBusyException>(() => ProjectWriterLease.Acquire(BackendProjectAccess.Read(fixture.Files.Options)));
        var before = File.ReadAllBytes(fixture.Files.Files.ProjectPath);
        var request = new BackendHttpMove { ClipId = "clip-main", StartFrame = 14, ExpectedRevision = 12, Fingerprint = opened.Fingerprint };
        using var response = await client.PostAsJsonAsync(BackendHttpServer.MoveRoute, request, BackendHttpServer.Json);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var saved = (await response.Content.ReadFromJsonAsync<BackendHttpSnapshot>(BackendHttpServer.Json))!;
        Assert.Equal(13, saved.Project.Revision); Assert.Equal(14, saved.Project.Montage.Clips[0].StartFrame);
        Assert.Equal(opened.Project.Montage.Clips[0] with { StartFrame = 14 }, saved.Project.Montage.Clips[0]);
        Assert.Equal(opened.Project.Montage.Clips[1], saved.Project.Montage.Clips[1]);
        Assert.Equal(2, fixture.Files.Files.HistoryFiles.Length);
        Assert.Contains(fixture.Files.Files.HistoryFiles, path => File.ReadAllBytes(path).SequenceEqual(before));
        foreach (var baseline in new[] { (12L, saved.Fingerprint), (13L, new string('0', 64)), (12L, opened.Fingerprint) })
        {
            var bytes = File.ReadAllBytes(fixture.Files.Files.ProjectPath);
            using var conflict = await client.PostAsJsonAsync(BackendHttpServer.MoveRoute,
                new BackendHttpMove { ClipId = "clip-main", StartFrame = 16, ExpectedRevision = baseline.Item1, Fingerprint = baseline.Item2 }, BackendHttpServer.Json);
            Assert.Equal(HttpStatusCode.Conflict, conflict.StatusCode);
            var error = await conflict.Content.ReadFromJsonAsync<BackendHttpFailure>(BackendHttpServer.Json);
            Assert.Equal("revision_conflict", error!.Error); Assert.True(error.ReconciliationRequired);
            Assert.Equal(bytes, File.ReadAllBytes(fixture.Files.Files.ProjectPath)); Assert.Equal(2, fixture.Files.Files.HistoryFiles.Length);
        }
        using var noop = await client.PostAsJsonAsync(BackendHttpServer.MoveRoute,
            new BackendHttpMove { ClipId = "clip-main", StartFrame = 14, ExpectedRevision = 13, Fingerprint = saved.Fingerprint }, BackendHttpServer.Json);
        Assert.Equal(HttpStatusCode.OK, noop.StatusCode);
        Assert.Equal(saved.Fingerprint, (await noop.Content.ReadFromJsonAsync<BackendHttpSnapshot>(BackendHttpServer.Json))!.Fingerprint);
        Assert.Equal(2, fixture.Files.Files.HistoryFiles.Length);
    }

    [Theory]
    [InlineData("token", 403)]
    [InlineData("origin", 403)]
    [InlineData("host", 403)]
    [InlineData("method", 405)]
    [InlineData("unknown", 404)]
    [InlineData("query", 400)]
    [InlineData("content", 415)]
    [InlineData("missing", 400)]
    [InlineData("duplicate", 400)]
    [InlineData("path", 400)]
    [InlineData("large", 413)]
    public async Task Http_RefusesInvalidAuthorityRoutesAndPayloads_WithoutProjectMutation(string mode, int status)
    {
        await using var fixture = new BackendHttpFixture(); await fixture.StartAsync();
        using var client = fixture.Client(mode != "token");
        var before = File.ReadAllBytes(fixture.Files.Files.ProjectPath);
        var snapshot = ProjectFileCodec.Read(before);
        var body = new JsonObject { ["clip_id"] = "clip-main", ["start_frame"] = 14, ["expected_revision"] = 12, ["fingerprint"] = snapshot.Fingerprint };
        if (mode == "missing") body.Remove("expected_revision");
        if (mode == "path") body["project_directory"] = "C:/not-request-authority";
        var json = body.ToJsonString();
        if (mode == "duplicate") json = json.Insert(1, "\"start_frame\":16,");
        if (mode == "large") json = new string(' ', 4097) + json;
        var path = mode == "unknown" ? "/api/backend/v1/other" : mode == "query" ? BackendHttpServer.MoveRoute + "?path=x" : BackendHttpServer.MoveRoute;
        using var request = new HttpRequestMessage(mode == "method" ? HttpMethod.Put : HttpMethod.Post, path)
            { Content = new StringContent(json, Encoding.UTF8, mode == "content" ? "text/plain" : "application/json") };
        if (mode == "origin") request.Headers.Add("Origin", "https://example.test");
        if (mode == "host") request.Headers.Host = "example.test";
        using var response = await client.SendAsync(request);
        Assert.Equal(status, (int)response.StatusCode);
        Assert.Equal("no-store", response.Headers.CacheControl!.ToString());
        Assert.Equal(before, File.ReadAllBytes(fixture.Files.Files.ProjectPath)); Assert.Single(fixture.Files.Files.HistoryFiles);
    }

    [Fact]
    public async Task HttpStartup_DoesNotMigrateLegacy_AndStopClosesListenerWhileParentStillOwnsWriter()
    {
        await using (var legacy = new BackendHttpFixture())
        {
            var before = File.ReadAllBytes(legacy.Files.Files.ProjectPath);
            await Assert.ThrowsAsync<InvalidDataException>(() => legacy.StartAsync(false));
            Assert.Equal(before, File.ReadAllBytes(legacy.Files.Files.ProjectPath)); Assert.Empty(legacy.Files.Files.HistoryFiles);
        }
        await using var fixture = new BackendHttpFixture(); await fixture.StartAsync();
        using var client = fixture.Client();
        await fixture.Http.DisposeAsync();
        await Assert.ThrowsAsync<HttpRequestException>(() => client.GetAsync(BackendHttpServer.ProjectRoute));
        Assert.Throws<ProjectStoreBusyException>(() => ProjectWriterLease.Acquire(BackendProjectAccess.Read(fixture.Files.Options)));
        Assert.Equal(12, (await fixture.Connection.OpenAsync()).Project.Revision);
    }

    [Fact]
    public async Task HttpStartup_LeavesOccupiedPortAndProjectIntact()
    {
        using var occupied = new TcpListener(IPAddress.Loopback, 0); occupied.Start();
        await using var fixture = new BackendHttpFixture(((IPEndPoint)occupied.LocalEndpoint).Port);
        await Assert.ThrowsAsync<IOException>(() => fixture.StartAsync());
        Assert.True(occupied.Server.IsBound); Assert.Single(fixture.Files.Files.HistoryFiles);
    }
}
