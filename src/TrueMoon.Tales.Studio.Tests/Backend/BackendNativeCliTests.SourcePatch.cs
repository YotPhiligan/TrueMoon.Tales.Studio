using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.AspNetCore.Http;
using TrueMoon.Tales.Studio.Backend;
using TrueMoon.Tales.Studio.Backend.Http;
using TrueMoon.Tales.Studio.Persistence;
using Xunit;

namespace TrueMoon.Tales.Studio.Tests.Backend;

public sealed partial class BackendNativeCliTests
{
    [Fact]
    public async Task NativeSourcePatch_CompletesAgentRequestWithoutChangingFrozenContextOrMontage()
    {
        using var files = new BackendWriteFixture(scenario: true);
        var legacy = JsonNode.Parse(File.ReadAllText(files.Files.ProjectPath))!;
        legacy["blocks"]![0]!["notes"] = "Сохранённые заметки";
        legacy["blocks"]![0]!["opaque_source"] = new JsonObject { ["exact_value"] = 9007199254740993L };
        File.WriteAllText(files.Files.ProjectPath, legacy.ToJsonString());
        var goal = BackendAgentRequestCreationTests.Configure(files);
        var scope = ScenarioInput(files, "scope.json", """
            {"intent":"improve_block","segment_id":"segment-main","item_id":"clip-main","components":["prompt"]}
            """);
        await using var fixture = new BackendHttpFixture(files);
        await fixture.StartAsync();
        var head = await fixture.Connection.OpenAsync();
        var beforeBytes = File.ReadAllBytes(files.Files.ProjectPath);
        var source = head.Project.Sources.Single(row => row.Id == "source-a");
        Assert.Throws<ProjectStoreBusyException>(() => ProjectWriterLease.Acquire(BackendProjectAccess.Read(files.Options)));

        const string actor = "agent:source-patch";
        var id = await BackendAgentRequestUpdateTests.CreateAsync(fixture, goal, head, "--mode", "apply", "--scope-file", scope);
        var frozen = (await ReadRequest()).GetProperty("context");
        var claim = ScenarioSuccess(await RunScenarioAsync(fixture, ["backend-agent-request-claim", "--id", id,
            "--revision", "1", "--actor", actor]));
        Assert.Equal("running", claim.GetProperty("state").GetString());
        Assert.Equal(actor, claim.GetProperty("owner").GetString());
        Assert.Equal(source.Properties["prompt"].GetString(), FrozenSource(frozen).GetProperty("properties").GetProperty("prompt").GetString());

        var prompt = new string('П', 18_000) + "\n  Точная строка.\n";
        var form = ScenarioInput(files, "source.json", JsonSerializer.Serialize(new { source_id = source.Id, prompt }, AgentRequestDraft.Json));
        Assert.True(new FileInfo(form).Length > ScenarioActionWrite.MaxBodyBytes);
        var args = SourcePatchArgs(form, "source-prompt-0001", head, actor);
        var edited = ScenarioSuccess(await RunScenarioAsync(fixture, args));
        Assert.Equal(source.Id, edited.GetProperty("source_id").GetString());
        Assert.True(edited.GetProperty("changed").GetBoolean());

        var saved = await fixture.Connection.OpenAsync();
        Assert.Equal(head.Project.Revision + 1, saved.Project.Revision);
        Assert.Equal(saved.Project.Revision, edited.GetProperty("project_revision").GetInt64());
        var updated = saved.Project.Sources.Single(row => row.Id == source.Id);
        Assert.Equal(source.Title, updated.Title);
        Assert.Equal(source.Label, updated.Label);
        Assert.Equal(prompt, updated.Properties["prompt"].GetString());
        foreach (var (name, value) in source.Properties.Where(entry => entry.Key != "prompt"))
            Assert.True(JsonElement.DeepEquals(value, updated.Properties[name]), name);
        Assert.Equal(JsonSerializer.Serialize(source.Takes), JsonSerializer.Serialize(updated.Takes));
        Assert.Equal(JsonSerializer.Serialize(head.Project.Sources.Where(row => row.Id != source.Id)),
            JsonSerializer.Serialize(saved.Project.Sources.Where(row => row.Id != source.Id)));
        Assert.Equal(JsonSerializer.Serialize(head.Project.Segments), JsonSerializer.Serialize(saved.Project.Segments));
        Assert.Equal(JsonSerializer.Serialize(head.Project.Montage), JsonSerializer.Serialize(saved.Project.Montage));
        Assert.Equal(2, files.Files.HistoryFiles.Length);
        Assert.Contains(files.Files.HistoryFiles, entry => File.ReadAllBytes(entry).SequenceEqual(beforeBytes));

        var result = ScenarioInput(files, "result.txt", "Промпт источника сохранён.");
        var target = ScenarioInput(files, "target.json", """{"segment_id":"segment-main","item_id":"clip-main"}""");
        var finished = ScenarioSuccess(await RunScenarioAsync(fixture, ["backend-agent-request-finish", "--id", id,
            "--revision", "2", "--actor", actor, "--result-file", result, "--target-file", target]));
        Assert.Equal("completed", finished.GetProperty("state").GetString());
        Assert.Equal(saved.Project.Revision, finished.GetProperty("completed_project_revision").GetInt64());
        Assert.Equal("clip-main", (await ReadRequest()).GetProperty("result_target").GetProperty("item_id").GetString());
        Assert.Equal(frozen.GetRawText(), (await ReadRequest()).GetProperty("context").GetRawText());
        var currentContext = ScenarioSuccess(await RunScenarioAsync(fixture, ["backend-agent-context", "--block", source.Id]));
        Assert.Equal(prompt, FrozenSource(currentContext.GetProperty("context")).GetProperty("properties").GetProperty("prompt").GetString());
        Assert.Equal(saved.Fingerprint, (await fixture.Connection.OpenAsync()).Fingerprint);
        Assert.False(Directory.Exists(Path.Combine(files.Files.ProjectDirectory, "Production", "Jobs")));

        async Task<JsonElement> ReadRequest() =>
            ScenarioSuccess(await RunScenarioAsync(fixture, ["backend-agent-request", "--id", id])).GetProperty("request");

        static JsonElement FrozenSource(JsonElement context) => context.GetProperty("project").GetProperty("sources")
            .EnumerateArray().Single(row => row.GetProperty("id").GetString() == "source-a");
    }

    [Fact]
    public async Task NativeSourcePatch_RejectsStaleAndInvalidEditsAndTreatsEqualValuesAsNoOp()
    {
        using var files = new BackendWriteFixture(scenario: true);
        var legacy = JsonNode.Parse(File.ReadAllText(files.Files.ProjectPath))!;
        legacy["blocks"]![0]!["notes"] = "Заметки до очистки";
        File.WriteAllText(files.Files.ProjectPath, legacy.ToJsonString());
        await using var fixture = new BackendHttpFixture(files);
        await fixture.StartAsync();
        var head = await fixture.Connection.OpenAsync();
        var bytes = File.ReadAllBytes(files.Files.ProjectPath);
        var original = head.Project.Sources.Single(row => row.Id == "source-a");
        const string key = "source-refusal-0001";
        const string valid = """{"source_id":"source-a","title":"  Новое имя  ","notes":""}""";
        var path = ScenarioInput(files, "source.json", valid);
        AssertError(await RunScenarioAsync(fixture, SourcePatchArgs(path, key, head with
        { Project = head.Project with { Revision = head.Project.Revision - 1 } })), 3, "revision_conflict", reconcile: true);
        AssertError(await RunScenarioAsync(fixture, SourcePatchArgs(path, key, head with { Fingerprint = new string('0', 64) })),
            3, "revision_conflict", reconcile: true);
        File.WriteAllText(path, """{"source_id":"missing-source","notes":"Правка"}""");
        AssertError(await RunScenarioAsync(fixture, SourcePatchArgs(path, key, head)), 1, "invalid_scenario_edit", reconcile: true);
        foreach (var invalid in new[]
        {
            """{"source_id":"source-a"}""",
            """{"source_id":"source-a","notes":null}""",
            """{"source_id":"source-a","title":"  "}""",
            """{"source_id":"source-a","settings":{}}""",
            """{"source_id":"source-a","prompt":"Первый","prompt":"Второй"}""",
            JsonSerializer.Serialize(new { source_id = "source-a", prompt = new string('x', 100_001) })
        })
        {
            File.WriteAllText(path, invalid);
            AssertError(await RunScenarioAsync(fixture, SourcePatchArgs(path, key, head)), 2, "invalid_arguments", reconcile: false);
        }
        Assert.Equal(bytes, File.ReadAllBytes(files.Files.ProjectPath));
        Assert.Single(files.Files.HistoryFiles);

        File.WriteAllText(path, valid);
        Assert.True(ScenarioSuccess(await RunScenarioAsync(fixture, SourcePatchArgs(path, key, head))).GetProperty("changed").GetBoolean());
        var saved = await fixture.Connection.OpenAsync();
        Assert.Equal(head.Project.Revision + 1, saved.Project.Revision);
        var source = saved.Project.Sources.Single(row => row.Id == original.Id);
        Assert.Equal("Новое имя", source.Title);
        Assert.Equal("", source.Properties["notes"].GetString());
        Assert.Equal(original.Properties["prompt"].GetString(), source.Properties["prompt"].GetString());
        var unchanged = ScenarioSuccess(await RunScenarioAsync(fixture, SourcePatchArgs(path, "source-noop-000001", saved)));
        Assert.False(unchanged.GetProperty("changed").GetBoolean());
        Assert.Equal(saved.Project.Revision, unchanged.GetProperty("project_revision").GetInt64());
        Assert.Equal(saved.Fingerprint, (await fixture.Connection.OpenAsync()).Fingerprint);
        Assert.Equal(2, files.Files.HistoryFiles.Length);
    }

    [Fact]
    public async Task NativeSourcePatch_ReplaysLostReceiptAcrossConcurrentCallsAndRestart()
    {
        using var files = new BackendWriteFixture(scenario: true);
        const string key = "source-lost-reply-01";
        var path = ScenarioInput(files, "source.json", """{"source_id":"source-a","notes":"Новый текст"}""");
        string[] args;
        JsonElement receipt;
        string fingerprint;
        await using (var fixture = new BackendHttpFixture(files))
        {
            await fixture.StartAsync();
            var head = await fixture.Connection.OpenAsync();
            args = SourcePatchArgs(path, key, head);
            using var client = fixture.Client();
            var posts = 0;
            await WithStubAsync(async context =>
            {
                Assert.Equal(BackendHttpServer.ScenarioSourcePatchRoute, context.Request.Path.Value);
                Assert.Equal(HttpMethods.Post, context.Request.Method);
                Interlocked.Increment(ref posts);
                using var body = await JsonDocument.ParseAsync(context.Request.Body, cancellationToken: context.RequestAborted);
                using var content = new StringContent(body.RootElement.GetRawText(), Encoding.UTF8, "application/json");
                using var write = await client.PostAsync(BackendHttpServer.ScenarioSourcePatchRoute, content, context.RequestAborted);
                write.EnsureSuccessStatusCode();
                context.Abort();
            }, async address =>
            {
                AssertError(await RunAsync(address, Fingerprint, args), 1, "backend_unavailable", reconcile: true);
                Assert.Equal(1, Volatile.Read(ref posts));
            });
            var replies = await Task.WhenAll(RunScenarioAsync(fixture, args), RunScenarioAsync(fixture, args));
            receipt = ScenarioSuccess(replies[0]);
            Assert.True(JsonElement.DeepEquals(receipt, ScenarioSuccess(replies[1])));
            var saved = await fixture.Connection.OpenAsync();
            fingerprint = saved.Fingerprint;
            Assert.Equal(head.Project.Revision + 1, saved.Project.Revision);
            Assert.Equal("Новый текст", saved.Project.Sources.Single(row => row.Id == "source-a").Properties["notes"].GetString());
            Assert.Equal(2, files.Files.HistoryFiles.Length);
        }

        await using var restarted = new BackendHttpFixture(files);
        await restarted.StartAsync(migrate: false);
        Assert.True(JsonElement.DeepEquals(receipt, ScenarioSuccess(await RunScenarioAsync(restarted, args))));
        File.WriteAllText(path, """{"source_id":"source-a","notes":"Изменённый запрос"}""");
        AssertError(await RunScenarioAsync(restarted, args), 3, "scenario_action_key_conflict", reconcile: true);
        Assert.Equal(fingerprint, (await restarted.Connection.OpenAsync()).Fingerprint);
        Assert.Equal(2, files.Files.HistoryFiles.Length);
    }

    private static string[] SourcePatchArgs(string form, string key, OpenedBackendProject baseline, string actor = "author") =>
        ["backend-scenario-source-patch", "--file", form, "--client-key", key, "--sha256", baseline.Fingerprint,
            "--revision", baseline.Project.Revision.ToString(CultureInfo.InvariantCulture), "--actor", actor];
}
