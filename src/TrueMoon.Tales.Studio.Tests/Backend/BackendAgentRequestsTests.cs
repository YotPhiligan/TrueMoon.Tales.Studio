using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using TrueMoon.Tales.Studio.Backend;
using TrueMoon.Tales.Studio.Backend.Http;
using TrueMoon.Tales.Studio.Persistence;
using TrueMoon.Tales.Studio.Tests.Support;
using Xunit;

namespace TrueMoon.Tales.Studio.Tests.Backend;

public sealed class BackendAgentRequestsTests
{
    private const string ProjectId = "project-synthetic";
    private const string OlderId = "req-0123456789abcdef";
    private const string NewerId = "req-fedcba9876543210";
    private const string MissingId = "req-0000000000000000";

    [Fact]
    public async Task NativeAgentRequests_ListsLegacySummariesAndReadsExactSavedRequestWithoutWriting()
    {
        await using var fixture = new BackendHttpFixture();
        var directory = RequestsDirectory(fixture.Files);
        Save(directory, Record(OlderId, "2026-11-20T18:00:00+00:00"));
        var finished = Record(NewerId, "2026-11-21T18:00:00.123456+00:00");
        finished["state"] = "completed";
        finished["revision"] = 3;
        finished["owner"] = "agent:qa";
        finished["result"] = "Сверены стыки и звук.";
        finished["completed_project_revision"] = 12;
        finished["scope"] = JsonNode.Parse("""{"intent":"breakdown_segment","segment_id":"segment-main","item_id":null}""");
        finished["result_target"] = JsonNode.Parse("""{"segment_id":"segment-main","item_id":"clip-main","block_id":"source-a"}""");
        finished["proposal"] = JsonNode.Parse("""[{"title":"Реакция","duration_frames":24}]""");
        finished["opaque_field"] = JsonNode.Parse("""{"large_integer":9007199254740993,"array":[false,null]}""");
        Save(directory, finished, bom: true);
        File.WriteAllText(Path.Combine(directory, MissingId + ".json.pending"), "partial");
        await fixture.StartAsync();
        var before = ObserveFiles(fixture.Files.Files.ProjectDirectory);

        var list = Success(await RunAsync(fixture));
        var rows = list.GetProperty("requests");
        Assert.Equal([NewerId, OlderId], rows.EnumerateArray().Select(row => row.GetProperty("id").GetString()));
        var completed = rows[0];
        Assert.Equal("completed", completed.GetProperty("state").GetString());
        Assert.Equal(3, completed.GetProperty("revision").GetInt64());
        Assert.Equal(11, completed.GetProperty("base_project_revision").GetInt64());
        Assert.Equal(12, completed.GetProperty("completed_project_revision").GetInt64());
        Assert.Equal("agent:qa", completed.GetProperty("owner").GetString());
        Assert.Equal("Сверены стыки и звук.", completed.GetProperty("result").GetString());
        Assert.Equal("разбор часов Эйко", completed.GetProperty("goal").GetString());
        Assert.Equal("breakdown_segment", completed.GetProperty("scope").GetProperty("intent").GetString());
        Assert.Equal("clip-main", completed.GetProperty("result_target").GetProperty("item_id").GetString());
        Assert.True(completed.GetProperty("has_proposal").GetBoolean());
        Assert.False(completed.TryGetProperty("context", out _));
        Assert.False(completed.TryGetProperty("proposal", out _));
        Assert.False(rows[1].TryGetProperty("has_proposal", out _));

        var detail = Success(await RunAsync(fixture, NewerId)).GetProperty("request");
        using var original = JsonDocument.Parse(finished.ToJsonString());
        Assert.True(JsonElement.DeepEquals(original.RootElement, detail));
        Assert.Equal(before, ObserveFiles(fixture.Files.Files.ProjectDirectory));
    }

    [Fact]
    public async Task NativeAgentRequests_AppliesLimitAndTieBreakWithoutLosingDirectLookup()
    {
        await using var fixture = new BackendHttpFixture();
        var directory = RequestsDirectory(fixture.Files);
        for (var index = 202; index > 0; index--)
            Save(directory, Record($"req-{index:x16}", index > 1 ? "2026-11-22T06:00:00+00:00" : "2026-11-21T06:00:00+00:00"));
        await fixture.StartAsync();

        var rows = Success(await RunAsync(fixture)).GetProperty("requests");
        Assert.Equal(200, rows.GetArrayLength());
        Assert.Equal(Enumerable.Range(3, 200).Reverse().Select(index => $"req-{index:x16}"),
            rows.EnumerateArray().Select(row => row.GetProperty("id").GetString()));
        var older = Success(await RunAsync(fixture, "req-0000000000000001")).GetProperty("request");
        Assert.Equal("2026-11-21T06:00:00+00:00", older.GetProperty("updated_at").GetString());
    }

    [Fact]
    public async Task NativeAgentRequests_RejectsMissingInvalidAndForeignRecordsAndRequiresToken()
    {
        await using var fixture = new BackendHttpFixture();
        await fixture.StartAsync();
        var directory = RequestsDirectory(fixture.Files);
        var projectDirectory = fixture.Files.Files.ProjectDirectory;
        var before = ObserveFiles(projectDirectory);
        Assert.False(Directory.Exists(directory));
        Assert.Empty(Success(await RunAsync(fixture)).GetProperty("requests").EnumerateArray());
        Error(await RunAsync(fixture, MissingId), 1, 404, "agent_request_not_found");
        Error(await RunAsync(fixture, "../other"), 2, null, "invalid_arguments");
        Assert.False(Directory.Exists(directory));
        Assert.Equal(before, ObserveFiles(projectDirectory));

        var foreign = Record(NewerId, "2026-11-21T00:00:00+00:00");
        foreign["project_id"] = "another-project";
        Save(directory, foreign);
        before = ObserveFiles(projectDirectory);
        Error(await RunAsync(fixture, NewerId), 1, 422, "invalid_agent_request_record");
        Error(await RunAsync(fixture), 1, 422, "invalid_agent_request_record");

        using var unauthorized = fixture.Client(authenticated: false);
        using var forbidden = await unauthorized.GetAsync(BackendHttpServer.AgentRequestsRoute + "/" + NewerId);
        Assert.Equal(HttpStatusCode.Forbidden, forbidden.StatusCode);
        using var authorized = fixture.Client();
        using var malformed = await authorized.GetAsync(BackendHttpServer.AgentRequestsRoute + "/req-short");
        Assert.Equal(HttpStatusCode.UnprocessableEntity, malformed.StatusCode);
        using var wrongMethod = await authorized.PostAsync(BackendHttpServer.AgentRequestsRoute + "/" + NewerId, content: null);
        Assert.Equal(HttpStatusCode.MethodNotAllowed, wrongMethod.StatusCode);
        Assert.Equal(before, ObserveFiles(projectDirectory));
    }

    [Fact]
    public async Task NativeAgentRequests_RefusesOversizedDetailButKeepsSummaryAvailable()
    {
        await using var fixture = new BackendHttpFixture();
        var directory = RequestsDirectory(fixture.Files);
        var record = Record(NewerId, "2026-11-21T00:00:00+00:00");
        record["context"]!["large"] = new string('x', BackendAgentRequests.MaxResponseBytes);
        Save(directory, record);
        var smaller = Record(OlderId, "2026-11-20T00:00:00+00:00");
        var longExcerpt = new string('y', 700 * 1024);
        smaller["context"]!["large"] = longExcerpt;
        Save(directory, smaller);
        await fixture.StartAsync();

        var summaries = Success(await RunAsync(fixture)).GetProperty("requests");
        Assert.Equal([NewerId, OlderId], summaries.EnumerateArray().Select(row => row.GetProperty("id").GetString()));
        Assert.Equal(longExcerpt, Success(await RunAsync(fixture, OlderId)).GetProperty("request")
            .GetProperty("context").GetProperty("large").GetString());
        Error(await RunAsync(fixture, NewerId), 1, 413, "agent_requests_too_large");
    }

    [Fact]
    public async Task NativeAgentRequests_RefusesOversizedListInsteadOfReturningPartialRows()
    {
        await using var fixture = new BackendHttpFixture();
        var directory = RequestsDirectory(fixture.Files);
        var result = new string('z', 100_000);
        for (var index = 1; index <= 8; index++)
        {
            var record = Record($"req-{index:x16}", "2026-11-21T00:00:00+00:00");
            record["result"] = result;
            Save(directory, record);
        }
        await fixture.StartAsync();

        Error(await RunAsync(fixture), 1, 413, "agent_requests_too_large");
        Assert.Equal(result, Success(await RunAsync(fixture, "req-0000000000000008")).GetProperty("request")
            .GetProperty("result").GetString());
    }

    [Fact]
    public async Task NativeAgentRequests_RejectsProtectedProductionDirectory()
    {
        await using var fixture = new BackendHttpFixture();
        var directory = RequestsDirectory(fixture.Files);
        Save(directory, Record(NewerId, "2026-11-21T00:00:00+00:00"));
        var configPath = fixture.Files.Options.ConfigPath;
        var config = JsonNode.Parse(File.ReadAllText(configPath))!.AsObject();
        config["protected_roots"] = new JsonArray(directory);
        File.WriteAllText(configPath, config.ToJsonString());
        await fixture.StartAsync();

        Error(await RunAsync(fixture), 1, 403, "access_denied");
        Error(await RunAsync(fixture, NewerId), 1, 403, "access_denied");
    }

    [Fact]
    public async Task AgentRequests_ReadsWithSeparateWriterAndRequiresExplicitMigration()
    {
        using var fixture = new BackendWriteFixture();
        var directory = RequestsDirectory(fixture);
        Save(directory, Record(OlderId, "2026-11-20T00:00:00+00:00"));
        using var service = fixture.Service();
        var original = ObserveFiles(fixture.Files.ProjectDirectory);
        var legacy = await service.ReadAgentRequestsAsync(fixture.Options.SessionKey, OlderId);
        Assert.Equal("migration_required", legacy.ErrorCode);
        Assert.Equal(original, ObserveFiles(fixture.Files.ProjectDirectory));

        var store = fixture.Files.Store();
        await store.MigrateAsync(await store.OpenAsync(), 11, "agent-request-fixture");
        using var owner = ProjectWriterLease.Acquire(BackendProjectAccess.Read(fixture.Options));
        var reply = await service.ReadAgentRequestsAsync(fixture.Options.SessionKey, OlderId);
        Assert.Empty(reply.ErrorCode);
        using var json = JsonDocument.Parse(reply.Json);
        Assert.Equal(OlderId, json.RootElement.GetProperty("request").GetProperty("id").GetString());
        Assert.Single(fixture.Files.HistoryFiles);
    }

    private static string RequestsDirectory(BackendWriteFixture fixture) =>
        Path.Combine(fixture.Files.ProjectDirectory, "Production", "AgentRequests");

    private static JsonObject Record(string id, string updated) => new()
    {
        ["id"] = id, ["project_id"] = ProjectId, ["block_id"] = "source-a", ["block_label"] = "A",
        ["goal"] = "разбор часов Эйко", ["mode"] = "propose", ["state"] = "pending", ["revision"] = 1,
        ["base_project_revision"] = 11, ["created_at"] = "2026-11-20T00:00:00+00:00", ["updated_at"] = updated,
        ["actor"] = "author", ["owner"] = null, ["result"] = "", ["completed_project_revision"] = null,
        ["context"] = new JsonObject
        {
            ["context_version"] = 2,
            ["project"] = new JsonObject { ["id"] = ProjectId, ["revision"] = 11 },
            ["snapshot_sha256"] = new string('b', 64)
        }
    };

    private static void Save(string directory, JsonObject record, bool bom = false)
    {
        Directory.CreateDirectory(directory);
        File.WriteAllText(Path.Combine(directory, record["id"]!.GetValue<string>() + ".json"), record.ToJsonString(), new UTF8Encoding(bom));
    }

    private static async Task<(int Code, string Output, string Error)> RunAsync(BackendHttpFixture fixture, string? id = null)
    {
        string[] args = id is null ? ["backend-agent-requests"] : ["backend-agent-request", "--id", id];
        using var cli = BackendCliProcess.Native(fixture.Http.Address, fixture.Http.Token, args);
        return await cli.WaitAsync();
    }

    private static JsonElement Success((int Code, string Output, string Error) reply)
    {
        Assert.True(reply.Code == 0, reply.Error);
        Assert.Empty(reply.Error);
        using var json = JsonDocument.Parse(reply.Output);
        Assert.Equal(1, json.RootElement.GetProperty("api_version").GetInt32());
        Assert.Equal(ProjectId, json.RootElement.GetProperty("project_id").GetString());
        return json.RootElement.Clone();
    }

    private static void Error((int Code, string Output, string Error) reply, int code, int? status, string error)
    {
        Assert.Equal(code, reply.Code);
        Assert.Empty(reply.Output);
        using var json = JsonDocument.Parse(reply.Error);
        var root = json.RootElement;
        Assert.Equal(error, root.GetProperty("error").GetString());
        if (status is null) Assert.Equal(JsonValueKind.Null, root.GetProperty("status").ValueKind);
        else Assert.Equal(status.Value, root.GetProperty("status").GetInt32());
        Assert.False(root.GetProperty("reconciliation_required").GetBoolean());
        Assert.False(root.GetProperty("automatic_retry").GetBoolean());
    }

    private static string[] ObserveFiles(string root) => Directory.EnumerateFiles(root, "*", SearchOption.AllDirectories)
        .Where(path => Path.GetFileName(path) != ".Studio.project.writer.lock")
        .OrderBy(path => path, StringComparer.Ordinal)
        .Select(path => Path.GetRelativePath(root, path) + "=" + Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(path)))).ToArray();
}
