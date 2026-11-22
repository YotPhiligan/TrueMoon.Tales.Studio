using System.Globalization;
using System.Net;
using System.Net.Http.Json;
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

public sealed class BackendAgentRequestCreationTests
{
    private const string Key = "agent-create-key-001";
    private const string Goal = "Сверить стык и описание часов.";

    [Fact]
    public async Task NativeCreate_FreezesContextAndLeavesProjectAndHistoryUnchanged()
    {
        await using var fixture = new BackendHttpFixture();
        var goal = Configure(fixture.Files);
        var plan = Path.Combine(fixture.Files.Files.ProjectDirectory, "Plan.md");
        File.WriteAllText(plan, "Сохранённый план сцены");
        await fixture.StartAsync();
        var baseline = await fixture.Connection.OpenAsync();
        var files = fixture.Files.Files;
        var original = File.ReadAllBytes(files.ProjectPath);
        var history = Assert.Single(files.HistoryFiles);
        var historyBytes = File.ReadAllBytes(history);

        var receipt = Success(await CreateAsync(fixture, goal, Key, baseline, "--block", "A", "--mode", "apply", "--actor", "  автор  "));
        var id = receipt.GetProperty("id").GetString()!;
        Assert.Equal("pending", receipt.GetProperty("state").GetString());
        Assert.Equal(1, receipt.GetProperty("revision").GetInt64());
        Assert.Equal(baseline.Project.Revision, receipt.GetProperty("base_project_revision").GetInt64());
        Assert.Equal(baseline.Fingerprint, receipt.GetProperty("fingerprint").GetString());
        Assert.Equal(Key, receipt.GetProperty("client_key").GetString());
        var path = Assert.Single(RequestFiles(fixture.Files));
        Assert.Equal(id + ".json", Path.GetFileName(path));
        var saved = File.ReadAllBytes(path);

        File.WriteAllText(plan, "Новая версия плана");
        var detail = Success(await RunAsync(fixture, "backend-agent-request", "--id", id)).GetProperty("request");
        var context = detail.GetProperty("context");
        Assert.Equal(3, context.GetProperty("context_version").GetInt32());
        Assert.Equal("source-a", detail.GetProperty("block_id").GetString());
        Assert.Equal("source", detail.GetProperty("block_kind").GetString());
        Assert.Equal("A", detail.GetProperty("block_label").GetString());
        Assert.Equal(Goal, detail.GetProperty("goal").GetString());
        Assert.Equal("автор", detail.GetProperty("actor").GetString());
        Assert.Equal("apply", detail.GetProperty("mode").GetString());
        Assert.False(context.GetProperty("automatic_dispatch").GetBoolean());
        Assert.Equal(baseline.Project.Revision, context.GetProperty("project").GetProperty("revision").GetInt64());
        Assert.Equal(baseline.Fingerprint, context.GetProperty("fingerprint").GetString());
        var document = Assert.Single(context.GetProperty("documents").EnumerateArray(), row => row.GetProperty("role").GetString() == "project_plan");
        Assert.Equal("Сохранённый план сцены", document.GetProperty("text").GetString());
        CheckSnapshotHash(context, receipt.GetProperty("snapshot_sha256").GetString());
        var summary = Assert.Single(Success(await RunAsync(fixture, "backend-agent-requests")).GetProperty("requests").EnumerateArray());
        Assert.Equal(id, summary.GetProperty("id").GetString());
        Assert.Equal(original, File.ReadAllBytes(files.ProjectPath));
        Assert.Equal(history, Assert.Single(files.HistoryFiles));
        Assert.Equal(historyBytes, File.ReadAllBytes(history));
        Assert.Equal(saved, File.ReadAllBytes(path));
        Assert.False(Directory.Exists(Path.Combine(files.ProjectDirectory, "Production", "Jobs")));
    }

    [Fact]
    public async Task NativeCreate_ConcurrentDuplicateAndRestartUseOneSavedRequest()
    {
        using var files = new BackendWriteFixture();
        var goal = Configure(files);
        OpenedBackendProject baseline;
        string id, path;
        byte[] before;
        await using (var fixture = new BackendHttpFixture(files))
        {
            await fixture.StartAsync();
            baseline = await fixture.Connection.OpenAsync();
            var replies = await Task.WhenAll(CreateAsync(fixture, goal, Key, baseline), CreateAsync(fixture, goal, Key, baseline));
            var receipt = Success(replies[0]);
            Assert.True(JsonElement.DeepEquals(receipt, Success(replies[1])));
            id = receipt.GetProperty("id").GetString()!;
            path = Assert.Single(RequestFiles(files));
            before = File.ReadAllBytes(path);
            var moved = Success(await RunAsync(fixture, "backend-move", "--clip", "clip-main", "--start-frame", "14",
                "--revision", baseline.Project.Revision.ToString(CultureInfo.InvariantCulture), "--sha256", baseline.Fingerprint));
            Assert.Equal(baseline.Project.Revision + 1, moved.GetProperty("project").GetProperty("revision").GetInt64());
        }
        await using (var restarted = new BackendHttpFixture(files))
        {
            await restarted.StartAsync(migrate: false);
            var replay = Success(await CreateAsync(restarted, goal, Key, baseline));
            Assert.Equal(id, replay.GetProperty("id").GetString());
            Assert.Equal(baseline.Project.Revision, replay.GetProperty("base_project_revision").GetInt64());
            var current = await restarted.Connection.OpenAsync();
            Assert.Equal(baseline.Project.Revision + 1, current.Project.Revision);
            AssertFailure(await CreateAsync(restarted, goal, Key, current), 3, 409, "agent_request_key_conflict");
            File.WriteAllText(goal, "Изменённая цель при том же ключе");
            AssertFailure(await CreateAsync(restarted, goal, Key, baseline), 3, 409, "agent_request_key_conflict");
            Assert.Equal(path, Assert.Single(RequestFiles(files)));
            Assert.Equal(before, File.ReadAllBytes(path));
        }
    }

    [Fact]
    public async Task NativeCreate_RejectsRevisionAndShaConflictsBeforePublishing()
    {
        await using var fixture = new BackendHttpFixture();
        var goal = Configure(fixture.Files);
        await fixture.StartAsync();
        var baseline = await fixture.Connection.OpenAsync();
        var projectPath = fixture.Files.Files.ProjectPath;
        var before = File.ReadAllBytes(projectPath);
        AssertFailure(await CreateAsync(fixture, goal, Key, baseline with
        { Project = baseline.Project with { Revision = baseline.Project.Revision - 1 } }), 3, 409, "revision_conflict");
        AssertFailure(await CreateAsync(fixture, goal, Key, baseline with { Fingerprint = new string('0', 64) }), 3, 409, "revision_conflict");
        Assert.False(Directory.Exists(RequestsDirectory(fixture.Files)));
        Assert.Equal(before, File.ReadAllBytes(projectPath));
        Assert.Single(fixture.Files.Files.HistoryFiles);
        Assert.Equal("pending", Success(await CreateAsync(fixture, goal, Key, baseline)).GetProperty("state").GetString());
    }

    [Theory]
    [InlineData("improve_block", "clip-main", null, "A")]
    [InlineData("prepare_block", null, "clip-main", null)]
    [InlineData("breakdown_segment", null, null, null)]
    public async Task NativeCreate_BindsScopeToSavedScenarioWithoutEditingIt(string intent, string? item, string? after, string? block)
    {
        using var files = new BackendWriteFixture(scenario: true);
        var goal = Configure(files);
        await using var fixture = new BackendHttpFixture(files);
        await fixture.StartAsync();
        var baseline = await fixture.Connection.OpenAsync();
        var scope = Path.Combine(files.Files.Root, "scope.json");
        var form = new AgentRequestScope
        {
            Intent = intent, SegmentId = "segment-main", ItemId = item,
            Draft = after is null ? null : new() { Title = "Вставка", AfterItemId = after, DurationFrames = 12 }
        };
        File.WriteAllText(scope, JsonSerializer.Serialize(form, AgentRequestDraft.Json));
        string[] options = block is null ? ["--scope-file", scope] : ["--scope-file", scope, "--block", block];
        var receipt = Success(await CreateAsync(fixture, goal, Key, baseline, options));
        var detail = Success(await RunAsync(fixture, "backend-agent-request", "--id", receipt.GetProperty("id").GetString()!))
            .GetProperty("request");
        var saved = detail.GetProperty("scope");
        Assert.Equal("Первый смысловой сегмент", saved.GetProperty("segment_name").GetString());
        Assert.Equal(item, saved.GetProperty("item_id").GetString());
        Assert.Equal(intent, saved.GetProperty("intent").GetString());
        if (after is not null) Assert.Equal(after, saved.GetProperty("draft").GetProperty("after_item_id").GetString());
        Assert.Equal(item, detail.GetProperty("block_id").GetString());
        var context = detail.GetProperty("context");
        Assert.True(JsonElement.DeepEquals(saved, context.GetProperty("request_scope")));
        CheckSnapshotHash(context, receipt.GetProperty("snapshot_sha256").GetString());
        Assert.Equal(baseline.Fingerprint, (await fixture.Connection.OpenAsync()).Fingerprint);
        Assert.Single(files.Files.HistoryFiles);
    }

    [Fact]
    public async Task NativeCreate_RejectsBadScopeAndLegacyKeyWithoutWriting()
    {
        using var files = new BackendWriteFixture(scenario: true);
        Configure(files);
        var directory = RequestsDirectory(files);
        Directory.CreateDirectory(directory);
        const string oldId = "req-0123456789abcdef";
        var legacy = JsonSerializer.SerializeToUtf8Bytes(new
        {
            id = oldId, project_id = "project-synthetic", revision = 1, updated_at = "2026-11-20T12:00:00+00:00",
            client_key = Key, context = new { context_version = 2 }
        });
        var existing = Path.Combine(directory, oldId + ".json");
        File.WriteAllBytes(existing, legacy);
        await using var fixture = new BackendHttpFixture(files);
        await fixture.StartAsync();
        var baseline = await fixture.Connection.OpenAsync();
        var draft = Draft(baseline, Key);
        using var http = fixture.Client();
        using var unauthenticated = fixture.Client(false);
        using var denied = await unauthenticated.PostAsJsonAsync(BackendHttpServer.AgentRequestsRoute, draft, AgentRequestDraft.Json);
        Assert.Equal(HttpStatusCode.Forbidden, denied.StatusCode);
        await ErrorAsync(draft, HttpStatusCode.Conflict, "agent_request_key_conflict");
        var unused = draft with { ClientKey = "agent-create-key-002", Block = "B", Scope = new()
        { Intent = "improve_block", SegmentId = "segment-main", ItemId = "clip-main" } };
        await ErrorAsync(unused, HttpStatusCode.UnprocessableEntity, "invalid_agent_request_scope");
        await ErrorAsync(unused with { Block = null, Scope = unused.Scope! with { ItemId = "clip-extra" } },
            HttpStatusCode.NotFound, "agent_request_scope_missing");
        await ErrorAsync(unused with { Block = null, Scope = new()
        {
            Intent = "prepare_block", SegmentId = "segment-main", Draft = new() { Title = "Новый план", AfterItemId = "clip-extra" }
        } }, HttpStatusCode.Conflict, "agent_request_insertion_conflict");
        Assert.Equal(existing, Assert.Single(RequestFiles(files)));
        Assert.Equal(legacy, File.ReadAllBytes(existing));

        async Task ErrorAsync(AgentRequestDraft form, HttpStatusCode status, string code)
        {
            using var response = await http.PostAsJsonAsync(BackendHttpServer.AgentRequestsRoute, form, AgentRequestDraft.Json);
            Assert.Equal(status, response.StatusCode);
            using var json = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
            Assert.Equal(code, json.RootElement.GetProperty("error").GetString());
        }
    }

    [Fact]
    public async Task CreateAgentRequest_RequiresExistingWriter()
    {
        using var fixture = new BackendWriteFixture();
        Configure(fixture);
        var store = fixture.Files.Store();
        var baseline = await store.MigrateAsync(await store.OpenAsync(), 11, "agent-create-fixture");
        using var service = fixture.Service();
        var draft = new AgentRequestDraft
        { ExpectedRevision = baseline.Project.Revision, Fingerprint = baseline.Fingerprint, Goal = Goal, ClientKey = Key };
        using var owner = ProjectWriterLease.Acquire(BackendProjectAccess.Read(fixture.Options));
        var reply = await service.CreateAgentRequestAsync(fixture.Options.SessionKey, JsonSerializer.Serialize(draft, AgentRequestDraft.Json));
        Assert.Equal("writer_required", reply.ErrorCode);
        Assert.False(Directory.Exists(RequestsDirectory(fixture)));
        Assert.Equal(baseline.Fingerprint, (await store.OpenAsync()).Fingerprint);
    }

    internal static string Configure(BackendWriteFixture fixture)
    {
        var config = JsonNode.Parse(File.ReadAllText(fixture.Options.ConfigPath))!.AsObject();
        config.Remove("legacy_root");
        File.WriteAllText(fixture.Options.ConfigPath, config.ToJsonString());
        var goal = Path.Combine(fixture.Files.Root, "goal.txt");
        File.WriteAllText(goal, Goal, new UTF8Encoding(true));
        return goal;
    }

    private static AgentRequestDraft Draft(OpenedBackendProject baseline, string key) => new()
    {
        ExpectedRevision = baseline.Project.Revision, Fingerprint = baseline.Fingerprint, ClientKey = key, Goal = Goal
    };

    private static Task<(int Code, string Output, string Error)> CreateAsync(BackendHttpFixture fixture, string goal, string key,
        OpenedBackendProject baseline, params string[] options) => RunAsync(fixture, ["backend-agent-request-create", "--goal-file", goal,
            "--client-key", key, "--revision", baseline.Project.Revision.ToString(CultureInfo.InvariantCulture),
            "--sha256", baseline.Fingerprint, .. options]);

    private static async Task<(int Code, string Output, string Error)> RunAsync(BackendHttpFixture fixture, params string[] args)
    {
        using var cli = BackendCliProcess.Native(fixture.Http.Address, fixture.Http.Token, args);
        return await cli.WaitAsync();
    }

    private static JsonElement Success((int Code, string Output, string Error) reply)
    {
        Assert.True(reply.Code == 0, reply.Error);
        Assert.Empty(reply.Error);
        using var json = JsonDocument.Parse(reply.Output);
        Assert.Equal(1, json.RootElement.GetProperty("api_version").GetInt32());
        return json.RootElement.Clone();
    }

    private static void AssertFailure((int Code, string Output, string Error) reply, int code, int status, string error)
    {
        Assert.Equal(code, reply.Code);
        Assert.Empty(reply.Output);
        using var json = JsonDocument.Parse(reply.Error);
        Assert.Equal(error, json.RootElement.GetProperty("error").GetString());
        Assert.Equal(status, json.RootElement.GetProperty("status").GetInt32());
        Assert.False(json.RootElement.GetProperty("automatic_retry").GetBoolean());
    }

    private static void CheckSnapshotHash(JsonElement snapshot, string? receiptHash)
    {
        var body = JsonNode.Parse(snapshot.GetRawText())!.AsObject();
        var actual = body["snapshot_sha256"]!.GetValue<string>();
        body.Remove("snapshot_sha256");
        Assert.Equal(receiptHash, actual);
        Assert.Equal(Convert.ToHexString(SHA256.HashData(JsonSerializer.SerializeToUtf8Bytes(body))), actual);
    }

    private static string RequestsDirectory(BackendWriteFixture fixture) =>
        Path.Combine(fixture.Files.ProjectDirectory, "Production", "AgentRequests");

    private static string[] RequestFiles(BackendWriteFixture fixture) => Directory.GetFiles(RequestsDirectory(fixture));
}
