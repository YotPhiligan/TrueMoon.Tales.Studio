using System.Net;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using TrueMoon.Tales.Studio.Backend;
using TrueMoon.Tales.Studio.Backend.Http;
using TrueMoon.Tales.Studio.Persistence;
using TrueMoon.Tales.Studio.Tests.Support;
using Xunit;

namespace TrueMoon.Tales.Studio.Tests.Backend;

public sealed class BackendTrimHttpTests
{
    [BackendMediaFact]
    public async Task HttpTrim_UsesOneWriterAndObservedBaseline_CommitsOnceThenNoop_RefusesStaleAndOutOfMedia()
    {
        await using var fixture = new BackendMediaFixture(); await fixture.StartAsync();
        using var client = fixture.Backend.Client();
        var before = File.ReadAllBytes(fixture.Files.ProjectPath);
        var mediaBefore = File.ReadAllBytes(fixture.VideoPath);
        var identity = fixture.Baseline.Identity.ProcessId;
        var request = new BackendHttpTrim { ClipId = "clip-main", SourceInFrame = 8, SourceOutFrame = 48,
            StartFrame = 14, ExpectedRevision = 12, Fingerprint = fixture.Baseline.Fingerprint };
        using var response = await client.PostAsJsonAsync(BackendHttpServer.TrimRoute, request, BackendHttpServer.Json);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var saved = (await response.Content.ReadFromJsonAsync<BackendHttpSnapshot>(BackendHttpServer.Json))!;
        var clip = saved.Project.Montage.Clips.Single(row => row.Id == "clip-main");
        Assert.Equal(13, saved.Project.Revision); Assert.Equal(14, clip.StartFrame); Assert.Equal(20, clip.DurationFrames);
        Assert.Equal(8, clip.SourceInFrame); Assert.Equal(48, clip.SourceOutFrame); Assert.Equal(2, clip.Speed);
        Assert.Equal("source-a", clip.SourceId); Assert.Equal("take-a", clip.TakeId);
        Assert.Equal(2, fixture.Files.HistoryFiles.Length);
        Assert.Contains(fixture.Files.HistoryFiles, path => File.ReadAllBytes(path).SequenceEqual(before));
        Assert.Throws<ProjectStoreBusyException>(() => ProjectWriterLease.Acquire(BackendProjectAccess.Read(fixture.Backend.Files.Options)));
        Assert.Equal(identity, (await fixture.Backend.Connection.OpenAsync()).Identity.ProcessId);
        var committed = File.ReadAllBytes(fixture.Files.ProjectPath);
        foreach (var baseline in new[] { (12L, saved.Fingerprint), (13L, new string('0', 64)), (12L, fixture.Baseline.Fingerprint) })
        {
            using var conflict = await client.PostAsJsonAsync(BackendHttpServer.TrimRoute,
                new BackendHttpTrim { ClipId = "clip-main", SourceInFrame = 10, SourceOutFrame = 48, StartFrame = 15,
                    ExpectedRevision = baseline.Item1, Fingerprint = baseline.Item2 }, BackendHttpServer.Json);
            Assert.Equal(HttpStatusCode.Conflict, conflict.StatusCode);
            var failure = await conflict.Content.ReadFromJsonAsync<BackendHttpFailure>(BackendHttpServer.Json);
            Assert.Equal("revision_conflict", failure!.Error); Assert.True(failure.ReconciliationRequired);
            Assert.Equal(committed, File.ReadAllBytes(fixture.Files.ProjectPath)); Assert.Equal(2, fixture.Files.HistoryFiles.Length);
        }
        using var noop = await client.PostAsJsonAsync(BackendHttpServer.TrimRoute,
            new BackendHttpTrim { ClipId = "clip-main", SourceInFrame = 8, SourceOutFrame = 48, StartFrame = 14,
                ExpectedRevision = 13, Fingerprint = saved.Fingerprint }, BackendHttpServer.Json);
        Assert.Equal(HttpStatusCode.OK, noop.StatusCode);
        var unchanged = (await noop.Content.ReadFromJsonAsync<BackendHttpSnapshot>(BackendHttpServer.Json))!;
        Assert.Equal(13, unchanged.Project.Revision); Assert.Equal(saved.Fingerprint, unchanged.Fingerprint);
        using var overrun = await client.PostAsJsonAsync(BackendHttpServer.TrimRoute,
            new BackendHttpTrim { ClipId = "clip-main", SourceInFrame = 8, SourceOutFrame = 80, StartFrame = 14,
                ExpectedRevision = 13, Fingerprint = saved.Fingerprint }, BackendHttpServer.Json);
        Assert.Equal(HttpStatusCode.UnprocessableEntity, overrun.StatusCode);
        Assert.Equal("invalid_trim", (await overrun.Content.ReadFromJsonAsync<BackendHttpFailure>(BackendHttpServer.Json))!.Error);
        Assert.Equal(committed, File.ReadAllBytes(fixture.Files.ProjectPath)); Assert.Equal(2, fixture.Files.HistoryFiles.Length);
        Assert.Equal(mediaBefore, File.ReadAllBytes(fixture.VideoPath)); Assert.False(File.Exists(fixture.Files.JournalPath));
    }

    [BackendMediaFact]
    public async Task RealCliTrim_UsesSameBackendAndReturnsCommittedClip_StaleRetryIsNotApplied()
    {
        await using var fixture = new BackendMediaFixture(); await fixture.StartAsync();
        var before = File.ReadAllBytes(fixture.Files.ProjectPath);
        using var command = new BackendCliProcess(fixture.Backend.Http.Address, fixture.Backend.Http.Token,
            "backend-trim", "--clip", "clip-main", "--source-in", "8", "--source-out", "48", "--start", "14",
            "--revision", "12", "--sha256", fixture.Baseline.Fingerprint);
        var result = await command.WaitAsync();
        Assert.Equal(0, result.Code); Assert.Equal("", result.Error);
        var saved = JsonSerializer.Deserialize<BackendHttpSnapshot>(result.Output, BackendHttpServer.Json)!;
        Assert.Equal(13, saved.Project.Revision);
        Assert.Equal((14, 20, 8d, 48d), (saved.Project.Montage.Clips[0].StartFrame, saved.Project.Montage.Clips[0].DurationFrames,
            saved.Project.Montage.Clips[0].SourceInFrame, saved.Project.Montage.Clips[0].SourceOutFrame!.Value));
        var opened = await fixture.Backend.Connection.OpenAsync();
        Assert.Equal(fixture.Baseline.Identity.ProcessId, opened.Identity.ProcessId); Assert.Equal(saved.Fingerprint, opened.Fingerprint);
        using var stale = new BackendCliProcess(fixture.Backend.Http.Address, fixture.Backend.Http.Token,
            "backend-trim", "--clip", "clip-main", "--source-in", "10", "--source-out", "48", "--start", "15",
            "--revision", "12", "--sha256", fixture.Baseline.Fingerprint);
        var rejected = await stale.WaitAsync();
        Assert.Equal(3, rejected.Code); Assert.Equal("", rejected.Output);
        using var error = JsonDocument.Parse(rejected.Error);
        Assert.Equal("revision_conflict", error.RootElement.GetProperty("error").GetString());
        Assert.False(error.RootElement.GetProperty("automatic_retry").GetBoolean());
        Assert.Equal(13, (await fixture.Backend.Connection.OpenAsync()).Project.Revision);
        Assert.Equal(2, fixture.Files.HistoryFiles.Length);
        Assert.Contains(fixture.Files.HistoryFiles, path => File.ReadAllBytes(path).SequenceEqual(before));
    }

    [Theory]
    [InlineData("token", 403)]
    [InlineData("origin", 403)]
    [InlineData("method", 405)]
    [InlineData("query", 400)]
    [InlineData("content", 415)]
    [InlineData("missing", 400)]
    [InlineData("duplicate", 400)]
    [InlineData("authority", 400)]
    [InlineData("negative-start", 400)]
    [InlineData("reverse", 400)]
    [InlineData("string", 400)]
    [InlineData("boolean", 400)]
    [InlineData("nonfinite", 400)]
    [InlineData("large", 413)]
    public async Task TrimHttp_RejectsInvalidAuthorityOrShapeBeforeMediaDecode_WithoutMutation(string mode, int status)
    {
        await using var fixture = new BackendHttpFixture(); await fixture.StartAsync();
        using var client = fixture.Client(mode != "token");
        var bytes = File.ReadAllBytes(fixture.Files.Files.ProjectPath);
        var fingerprint = ProjectFileCodec.Read(bytes).Fingerprint;
        var body = new JsonObject { ["clip_id"] = "clip-main", ["source_in_frame"] = 4, ["source_out_frame"] = 20,
            ["start_frame"] = 12, ["expected_revision"] = 12, ["fingerprint"] = fingerprint };
        if (mode == "missing") body.Remove("source_in_frame");
        if (mode == "authority") body["project_directory"] = "C:/request-is-not-authority";
        if (mode == "negative-start") body["start_frame"] = -1;
        if (mode == "reverse") body["source_out_frame"] = 4;
        if (mode == "string") body["source_in_frame"] = "4";
        if (mode == "boolean") body["expected_revision"] = true;
        var json = body.ToJsonString();
        if (mode == "duplicate") json = json.Insert(1, "\"source_in_frame\":5,");
        if (mode == "nonfinite") json = json.Replace("\"source_out_frame\":20", "\"source_out_frame\":1e309", StringComparison.Ordinal);
        if (mode == "large") json = new string(' ', 4097) + json;
        using var request = new HttpRequestMessage(mode == "method" ? HttpMethod.Get : HttpMethod.Post,
            BackendHttpServer.TrimRoute + (mode == "query" ? "?project=other" : ""))
            { Content = new StringContent(json, Encoding.UTF8, mode == "content" ? "text/plain" : "application/json") };
        if (mode == "origin") request.Headers.Add("Origin", "https://example.test");
        using var response = await client.SendAsync(request);
        Assert.Equal(status, (int)response.StatusCode);
        Assert.Equal("no-store", response.Headers.CacheControl!.ToString());
        Assert.Equal(bytes, File.ReadAllBytes(fixture.Files.Files.ProjectPath)); Assert.Single(fixture.Files.Files.HistoryFiles);
        Assert.False(File.Exists(fixture.Files.Files.JournalPath));
    }
}
