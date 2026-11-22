using System.Globalization;
using System.Net;
using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using TrueMoon.Tales.Studio.Backend;
using TrueMoon.Tales.Studio.Backend.Http;
using TrueMoon.Tales.Studio.Persistence;
using TrueMoon.Tales.Studio.Tests.Support;
using Xunit;

namespace TrueMoon.Tales.Studio.Tests.Backend;

public sealed class BackendAgentRequestUpdateTests
{
    private const string Key = "agent-update-key-001";
    private const string Owner = "agent:continuity";

    [Fact]
    public async Task NativeAgentUpdate_ConcurrentClaimAndOwnerChecksGuardFinish()
    {
        await using var fixture = new BackendHttpFixture();
        var goal = BackendAgentRequestCreationTests.Configure(fixture.Files);
        await fixture.StartAsync();
        var baseline = await fixture.Connection.OpenAsync();
        var id = await CreateAsync(fixture, goal, baseline);
        var path = RequestPath(fixture.Files, id);
        var pending = File.ReadAllBytes(path);
        var resultPath = WriteInput(fixture, "result.txt", "Стыки сверены.");
        Error(await UpdateAsync(fixture, "finish", id, 1, Owner, "--result-file", resultPath), 3, 409, "agent_request_state_conflict");
        Assert.Equal(pending, File.ReadAllBytes(path));

        var claims = await Task.WhenAll(UpdateAsync(fixture, "claim", id, 1, Owner), UpdateAsync(fixture, "claim", id, 1, "agent:other"));
        var winner = Assert.Single(claims, reply => reply.Code == 0);
        Error(Assert.Single(claims, reply => reply.Code != 0), 3, 409, "agent_request_revision_conflict");
        var running = Success(winner);
        var owner = running.GetProperty("owner").GetString()!;
        var other = owner == Owner ? "agent:other" : Owner;
        Assert.Equal("running", running.GetProperty("state").GetString());
        Assert.Equal(2, running.GetProperty("revision").GetInt64());
        var before = File.ReadAllBytes(path);
        Error(await UpdateAsync(fixture, "claim", id, 2, other), 3, 409, "agent_request_state_conflict");
        Error(await UpdateAsync(fixture, "finish", id, 1, owner, "--result-file", resultPath), 3, 409, "agent_request_revision_conflict");
        Error(await UpdateAsync(fixture, "finish", id, 2, other, "--result-file", resultPath), 3, 409, "agent_request_owner_conflict");
        Error(await UpdateAsync(fixture, "fail", id, 2, other, "--result-file", resultPath), 3, 409, "agent_request_owner_conflict");
        Assert.Equal(before, File.ReadAllBytes(path));

        var completed = Success(await UpdateAsync(fixture, "finish", id, 2, $" {owner} ", "--result-file", resultPath));
        Assert.Equal("completed", completed.GetProperty("state").GetString());
        Assert.Equal(3, completed.GetProperty("revision").GetInt64());
        Assert.Equal(owner, completed.GetProperty("owner").GetString());
        Assert.Equal(baseline.Project.Revision, completed.GetProperty("completed_project_revision").GetInt64());
        Assert.Equal(running.GetProperty("snapshot_sha256").GetString(), completed.GetProperty("snapshot_sha256").GetString());
        var saved = File.ReadAllBytes(path);
        var detail = Success(await RunAsync(fixture, "backend-agent-request", "--id", id)).GetProperty("request");
        Assert.Equal("Стыки сверены.", detail.GetProperty("result").GetString());
        Assert.Equal(StoredField(pending, "context"), StoredField(saved, "context"));
        Assert.Equal(StoredField(pending, "submission_sha256"), StoredField(saved, "submission_sha256"));
        Error(await UpdateAsync(fixture, "cancel", id, 3, owner), 3, 409, "agent_request_state_conflict");
        Error(await UpdateAsync(fixture, "finish", id, 3, owner, "--result-file", resultPath), 3, 409, "agent_request_state_conflict");
        Assert.Equal(saved, File.ReadAllBytes(path));
        await AssertProjectUnchangedAsync(fixture, baseline, historyEntries: 1);
        Assert.Single(Directory.GetFiles(Path.GetDirectoryName(path)!));
    }

    [Fact]
    public async Task NativeAgentUpdate_FailStoresLongUnicodeResultAtCurrentProjectRevision()
    {
        await using var fixture = new BackendHttpFixture();
        var goal = BackendAgentRequestCreationTests.Configure(fixture.Files);
        await fixture.StartAsync();
        var baseline = await fixture.Connection.OpenAsync();
        var id = await CreateAsync(fixture, goal, baseline);
        Success(await UpdateAsync(fixture, "claim", id, 1, Owner));
        var changed = await fixture.Connection.EditScenarioAsync(new SaveMontageMarker(null, "Контрольная отметка", 24), baseline);
        Assert.Equal(baseline.Project.Revision + 1, changed.Project.Revision);
        var result = new string('Я', AgentRequestUpdate.MaxResultChars);
        var path = WriteInput(fixture, "failure.txt", result);
        var failed = Success(await UpdateAsync(fixture, "fail", id, 2, Owner, "--result-file", path));
        Assert.Equal("failed", failed.GetProperty("state").GetString());
        Assert.Equal(3, failed.GetProperty("revision").GetInt64());
        Assert.Equal(changed.Project.Revision, failed.GetProperty("completed_project_revision").GetInt64());
        var saved = Success(await RunAsync(fixture, "backend-agent-request", "--id", id)).GetProperty("request");
        Assert.Equal(baseline.Project.Revision, saved.GetProperty("base_project_revision").GetInt64());
        Assert.Equal(changed.Project.Revision, saved.GetProperty("completed_project_revision").GetInt64());
        Assert.Equal(Owner, saved.GetProperty("owner").GetString());
        Assert.Equal(Hash(result), Hash(saved.GetProperty("result").GetString()!));
        await AssertProjectUnchangedAsync(fixture, changed, historyEntries: 2);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task NativeAgentUpdate_CancelAcceptsPendingOrRunningAndKeepsOriginalOwner(bool claimed)
    {
        await using var fixture = new BackendHttpFixture();
        var goal = BackendAgentRequestCreationTests.Configure(fixture.Files);
        await fixture.StartAsync();
        var baseline = await fixture.Connection.OpenAsync();
        var id = await CreateAsync(fixture, goal, baseline);
        if (claimed) Success(await UpdateAsync(fixture, "claim", id, 1, Owner));
        var revision = claimed ? 2 : 1;
        var reason = WriteInput(fixture, "cancel.txt", "Остановлено автором");
        using var unauthorized = fixture.Client(authenticated: false);
        using var denied = await unauthorized.PostAsJsonAsync(BackendHttpServer.AgentRequestsRoute + "/" + id,
            new AgentRequestUpdate { ExpectedRevision = revision, Actor = "author", Action = "cancel" }, AgentRequestDraft.Json);
        Assert.Equal(HttpStatusCode.Forbidden, denied.StatusCode);

        var cancelled = Success(await UpdateAsync(fixture, "cancel", id, revision, "author", "--result-file", reason));
        Assert.Equal("cancelled", cancelled.GetProperty("state").GetString());
        Assert.Equal(revision + 1, cancelled.GetProperty("revision").GetInt64());
        Assert.Equal(claimed ? Owner : null, cancelled.GetProperty("owner").GetString());
        Assert.Equal(JsonValueKind.Null, cancelled.GetProperty("completed_project_revision").ValueKind);
        var saved = Success(await RunAsync(fixture, "backend-agent-request", "--id", id)).GetProperty("request");
        Assert.Equal("Остановлено автором", saved.GetProperty("result").GetString());
        Assert.Equal("author", saved.GetProperty("actor").GetString());
        Error(await UpdateAsync(fixture, "claim", id, revision + 1, Owner), 3, 409, "agent_request_state_conflict");
        await AssertProjectUnchangedAsync(fixture, baseline, historyEntries: 1);
    }

    [Fact]
    public async Task NativeAgentUpdate_FinishValidatesNewActionAgainstFrozenAndCurrentScenario()
    {
        using var files = new BackendWriteFixture(scenario: true);
        await using var fixture = new BackendHttpFixture(files);
        var goal = BackendAgentRequestCreationTests.Configure(files);
        await fixture.StartAsync();
        var baseline = await fixture.Connection.OpenAsync();
        var scope = WriteInput(fixture, "scope.json", """{"intent":"prepare_block","segment_id":"segment-main","draft":{"title":"Новый план"}}""");
        var id = await CreateAsync(fixture, goal, baseline, "--mode", "apply", "--scope-file", scope);
        Success(await UpdateAsync(fixture, "claim", id, 1, Owner));
        var result = WriteInput(fixture, "result.txt", "Добавлен подготовленный план.");
        var path = RequestPath(files, id);
        var savedRunning = File.ReadAllBytes(path);
        Error(await UpdateAsync(fixture, "finish", id, 2, Owner, "--result-file", result), 1, 422, "invalid_agent_request_update");
        var target = Target("clip-main");
        Error(await UpdateAsync(fixture, "finish", id, 2, Owner, "--result-file", result, "--target-file", target), 1, 422, "invalid_agent_request_update");
        Assert.Equal(savedRunning, File.ReadAllBytes(path));

        var updated = await fixture.Connection.EditScenarioAsync(new SaveAction("segment-main", null, "Новый план", "", 12), baseline);
        var newAction = updated.Project.Segments.Single(segment => segment.Id == "segment-main").Actions.Single(action => action.Title == "Новый план");
        var receipt = Success(await UpdateAsync(fixture, "finish", id, 2, Owner, "--result-file", result, "--target-file", Target(newAction.Id)));
        Assert.Equal(updated.Project.Revision, receipt.GetProperty("completed_project_revision").GetInt64());
        var detail = Success(await RunAsync(fixture, "backend-agent-request", "--id", id)).GetProperty("request");
        var saved = detail.GetProperty("result_target");
        Assert.Equal("segment-main", saved.GetProperty("segment_id").GetString());
        Assert.Equal(newAction.Id, saved.GetProperty("item_id").GetString());
        Assert.Equal(newAction.Id, saved.GetProperty("block_id").GetString());
        Assert.Equal(StoredField(savedRunning, "context"), StoredField(File.ReadAllBytes(path), "context"));
        await AssertProjectUnchangedAsync(fixture, updated, historyEntries: 2);

        string Target(string actionId) => WriteInput(fixture, "target.json", JsonSerializer.Serialize(new { segment_id = "segment-main", item_id = actionId }));
    }

    [Fact]
    public async Task NativeAgentUpdate_FinishStoresScopedProposalWithoutApplyingIt()
    {
        using var files = new BackendWriteFixture(scenario: true);
        await using var fixture = new BackendHttpFixture(files);
        var goal = BackendAgentRequestCreationTests.Configure(files);
        await fixture.StartAsync();
        var baseline = await fixture.Connection.OpenAsync();
        var scope = WriteInput(fixture, "breakdown.json", """{"intent":"breakdown_segment","segment_id":"segment-main"}""");
        var id = await CreateAsync(fixture, goal, baseline, "--scope-file", scope);
        Success(await UpdateAsync(fixture, "claim", id, 1, Owner));
        var result = WriteInput(fixture, "result.txt", "Предложена разбивка.");
        Error(await UpdateAsync(fixture, "finish", id, 2, Owner, "--result-file", result), 1, 422, "invalid_agent_request_update");
        var invalid = WriteInput(fixture, "invalid.json", """[{"title":"План","description":"Действие","duration_frames":0}]""");
        Error(await UpdateAsync(fixture, "finish", id, 2, Owner, "--result-file", result, "--proposal-file", invalid), 2, null, "invalid_arguments");
        var proposal = WriteInput(fixture, "proposal.json", """[{"title":" Реакция ","description":"Смотрит на часы","duration_frames":24,"sound_notes":"Тихий щелчок"}]""");
        var finished = Success(await UpdateAsync(fixture, "finish", id, 2, Owner, "--result-file", result, "--proposal-file", proposal));
        Assert.Equal("completed", finished.GetProperty("state").GetString());
        var detail = Success(await RunAsync(fixture, "backend-agent-request", "--id", id)).GetProperty("request");
        var item = Assert.Single(detail.GetProperty("proposal").EnumerateArray());
        Assert.Equal("Реакция", item.GetProperty("title").GetString());
        Assert.Equal(24, item.GetProperty("duration_frames").GetInt32());
        Assert.Equal(0, item.GetProperty("gap_before_frames").GetInt32());
        Assert.Equal("Тихий щелчок", item.GetProperty("sound_notes").GetString());
        var list = Success(await RunAsync(fixture, "backend-agent-requests"));
        Assert.True(Assert.Single(list.GetProperty("requests").EnumerateArray()).GetProperty("has_proposal").GetBoolean());
        await AssertProjectUnchangedAsync(fixture, baseline, historyEntries: 1);
    }

    [Fact]
    public async Task NativeAgentUpdate_UpdatesMigratedWebRequestWithoutReencodingItsSnapshot()
    {
        await using var fixture = new BackendHttpFixture();
        BackendAgentRequestCreationTests.Configure(fixture.Files);
        const string id = "req-0123456789abcdef";
        var path = RequestPath(fixture.Files, id);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        var web = Encoding.UTF8.GetBytes("""
            {
              "id":"req-0123456789abcdef", "project_id":"project-synthetic", "revision":2,"state":"running",
              "owner":"agent:continuity","updated_at":"2026-11-21T07:00:00+00:00","mode":"propose",
              "context": { "context_version": 2,
                "snapshot_sha256": "bbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbb",
                "legacy": "\u041a" },
              "opaque": { "big": 9007199254740993 }
            }
            """);
        File.WriteAllBytes(path, web);
        await fixture.StartAsync();
        var baseline = await fixture.Connection.OpenAsync();
        var result = WriteInput(fixture, "report.txt", "Историческая запись обработана.");
        var updated = Success(await UpdateAsync(fixture, "finish", id, 2, Owner, "--result-file", result));
        Assert.Equal(3, updated.GetProperty("revision").GetInt64());
        Assert.Equal("completed", updated.GetProperty("state").GetString());
        Assert.Equal(new string('b', 64), updated.GetProperty("snapshot_sha256").GetString());
        Assert.Equal(StoredField(web, "context"), StoredField(File.ReadAllBytes(path), "context"));
        Assert.Equal(StoredField(web, "opaque"), StoredField(File.ReadAllBytes(path), "opaque"));
        await AssertProjectUnchangedAsync(fixture, baseline, historyEntries: 1);
    }

    [Fact]
    public async Task AgentUpdate_RequiresExistingProjectWriter()
    {
        using var fixture = new BackendWriteFixture();
        BackendAgentRequestCreationTests.Configure(fixture);
        var store = fixture.Files.Store();
        await store.MigrateAsync(await store.OpenAsync(), 11, "agent-update-fixture");
        using var owner = ProjectWriterLease.Acquire(BackendProjectAccess.Read(fixture.Options));
        using var service = fixture.Service();
        var command = new AgentRequestUpdate { ExpectedRevision = 1, Action = "claim", Actor = Owner };
        var reply = await service.UpdateAgentRequestAsync(fixture.Options.SessionKey, "req-0123456789abcdef",
            JsonSerializer.SerializeToUtf8Bytes(command, AgentRequestDraft.Json));
        Assert.Equal("writer_required", reply.ErrorCode);
    }

    internal static async Task<string> CreateAsync(BackendHttpFixture fixture, string goal, OpenedBackendProject baseline, params string[] options)
    {
        var receipt = Success(await RunAsync(fixture, ["backend-agent-request-create", "--goal-file", goal, "--client-key", Key,
            "--revision", baseline.Project.Revision.ToString(CultureInfo.InvariantCulture), "--sha256", baseline.Fingerprint, .. options]));
        return receipt.GetProperty("id").GetString()!;
    }

    private static Task<(int Code, string Output, string Error)> UpdateAsync(BackendHttpFixture fixture, string action, string id,
        long revision, string actor, params string[] options) => RunAsync(fixture, ["backend-agent-request-" + action, "--id", id,
            "--revision", revision.ToString(CultureInfo.InvariantCulture), "--actor", actor, .. options]);

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

    private static void Error((int Code, string Output, string Error) reply, int code, int? status, string error)
    {
        Assert.Equal(code, reply.Code);
        Assert.Empty(reply.Output);
        using var json = JsonDocument.Parse(reply.Error);
        Assert.Equal(error, json.RootElement.GetProperty("error").GetString());
        Assert.Equal(status, json.RootElement.GetProperty("status").Deserialize<int?>());
        Assert.False(json.RootElement.GetProperty("automatic_retry").GetBoolean());
    }

    private static string WriteInput(BackendHttpFixture fixture, string name, string text)
    {
        var path = Path.Combine(fixture.Files.Files.Root, name);
        File.WriteAllText(path, text, new UTF8Encoding(true));
        return path;
    }

    private static string RequestPath(BackendWriteFixture fixture, string id) =>
        Path.Combine(fixture.Files.ProjectDirectory, "Production", "AgentRequests", id + ".json");

    private static string StoredField(byte[] bytes, string field)
    {
        using var json = JsonDocument.Parse(bytes);
        return json.RootElement.GetProperty(field).GetRawText();
    }

    private static string Hash(string text) => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(text)));

    private static async Task AssertProjectUnchangedAsync(BackendHttpFixture fixture, OpenedBackendProject baseline, int historyEntries)
    {
        Assert.Equal(baseline.Fingerprint, (await fixture.Connection.OpenAsync()).Fingerprint);
        var files = fixture.Files.Files;
        Assert.Equal(historyEntries, files.HistoryFiles.Length);
        Assert.False(Directory.Exists(Path.Combine(files.ProjectDirectory, "Production", "Jobs")));
    }
}
