using System.Globalization;
using System.Text;
using System.Text.Json;
using Microsoft.AspNetCore.Http;
using TrueMoon.Tales.Studio.Backend;
using TrueMoon.Tales.Studio.Backend.Http;
using TrueMoon.Tales.Studio.Persistence;
using Xunit;

namespace TrueMoon.Tales.Studio.Tests.Backend;

public sealed partial class BackendNativeCliTests
{
    [Fact]
    public async Task NativeSourceCreate_PrepareBlockFinishesWithDraftLinkedToNewAction()
    {
        using var files = new BackendWriteFixture(scenario: true);
        var goal = BackendAgentRequestCreationTests.Configure(files);
        const string actor = "agent:draft-source";
        var scope = ScenarioInput(files, "scope.json", """
            {"intent":"prepare_block","segment_id":"segment-main","components":["description","prompt"],
             "draft":{"title":"У костра","description":"Приближает руку к огню.","duration_frames":12}}
            """);
        await using var fixture = new BackendHttpFixture(files);
        await fixture.StartAsync();
        var initial = await fixture.Connection.OpenAsync();
        Assert.Throws<ProjectStoreBusyException>(() => ProjectWriterLease.Acquire(BackendProjectAccess.Read(files.Options)));
        var requestId = await BackendAgentRequestUpdateTests.CreateAsync(fixture, goal, initial, "--mode", "apply", "--scope-file", scope);
        var frozen = (await ReadRequest()).GetProperty("context");
        var claim = ScenarioSuccess(await RunScenarioAsync(fixture, ["backend-agent-request-claim", "--id", requestId,
            "--revision", "1", "--actor", actor]));
        Assert.Equal("running", claim.GetProperty("state").GetString());
        Assert.Equal(actor, claim.GetProperty("owner").GetString());
        Assert.Equal(initial.Fingerprint, (await fixture.Connection.OpenAsync()).Fingerprint);

        var actionForm = ScenarioInput(files, "action.json", """
            {"segment_id":"segment-main","title":"У костра","description":"Приближает руку к огню.","planned_duration":12}
            """);
        var newAction = ScenarioSuccess(await RunScenarioAsync(fixture, ScenarioArgs(actionForm, "draft-action-00001", initial, actor)));
        var actionId = newAction.GetProperty("action_id").GetString()!;
        var afterAction = await fixture.Connection.OpenAsync();
        var action = Assert.Single(afterAction.Project.Segments.Single(row => row.Id == "segment-main").Actions, row => row.Id == actionId);
        Assert.Equal(12, action.PlannedDurationFrames);
        Assert.Empty(action.Sources);
        var beforeSource = File.ReadAllBytes(files.Files.ProjectPath);

        var draftForm = ScenarioInput(files, "draft-source.json", """{"title":"Крупный план рук"}""");
        var createArgs = SourceCreateArgs(draftForm, "draft-source-00001", afterAction, actor);
        var created = ScenarioSuccess(await RunScenarioAsync(fixture, createArgs));
        var sourceId = created.GetProperty("source_id").GetString()!;
        Assert.True(created.GetProperty("changed").GetBoolean());
        Assert.Equal(afterAction.Project.Revision + 1, created.GetProperty("project_revision").GetInt64());
        Assert.False(created.TryGetProperty("take_id", out _));
        Assert.False(created.TryGetProperty("asset_id", out _));
        var afterSource = await fixture.Connection.OpenAsync();
        Assert.Equal(afterAction.Project.Sources.Length + 1, afterSource.Project.Sources.Length);
        var draft = Assert.Single(afterSource.Project.Sources, row => row.Id == sourceId);
        Assert.Equal("Крупный план рук", draft.Title);
        Assert.Empty(draft.Takes);
        Assert.Empty(draft.Properties);
        Assert.Equal(JsonSerializer.Serialize(afterAction.Project.Segments), JsonSerializer.Serialize(afterSource.Project.Segments));
        Assert.Contains(files.Files.HistoryFiles, entry => File.ReadAllBytes(entry).SequenceEqual(beforeSource));

        const string prompt = "Крупный план рук у костра.";
        var patch = ScenarioInput(files, "source-patch.json", JsonSerializer.Serialize(new { source_id = sourceId, prompt }));
        var patched = ScenarioSuccess(await RunScenarioAsync(fixture, SourcePatchArgs(patch, "draft-source-text-01", afterSource, actor)));
        Assert.Equal(sourceId, patched.GetProperty("source_id").GetString());
        var afterPatch = await fixture.Connection.OpenAsync();
        var linked = ScenarioSuccess(await RunScenarioAsync(fixture, ["backend-scenario-action-link-source", "--segment", "segment-main",
            "--action", actionId, "--source", sourceId, "--purpose", "деталь", "--actor", actor, "--client-key", "draft-source-link-01",
            "--revision", afterPatch.Project.Revision.ToString(CultureInfo.InvariantCulture), "--sha256", afterPatch.Fingerprint]));
        Assert.Equal(actionId, linked.GetProperty("action_id").GetString());

        var completedProject = await fixture.Connection.OpenAsync();
        var savedAction = completedProject.Project.Segments.Single(row => row.Id == "segment-main").Actions.Single(row => row.Id == actionId);
        var binding = Assert.Single(savedAction.Sources);
        Assert.Equal(sourceId, binding.SourceId);
        Assert.Equal("деталь", binding.Purpose);
        var savedDraft = Assert.Single(completedProject.Project.Sources, row => row.Id == sourceId);
        Assert.Equal(prompt, savedDraft.Properties["prompt"].GetString());
        Assert.Empty(savedDraft.Takes);
        Assert.Equal(JsonSerializer.Serialize(initial.Project.Sources),
            JsonSerializer.Serialize(completedProject.Project.Sources.Where(source => source.Id != sourceId)));
        Assert.Equal(JsonSerializer.Serialize(afterAction.Project.Montage), JsonSerializer.Serialize(completedProject.Project.Montage));
        Assert.True(JsonElement.DeepEquals(initial.Project.Properties["assets"], completedProject.Project.Properties["assets"]));
        Assert.Equal(initial.Project.Revision + 4, completedProject.Project.Revision);
        Assert.Equal(5, files.Files.HistoryFiles.Length);

        var result = ScenarioInput(files, "result.txt", "Добавлены действие и связанный черновой источник.");
        var target = ScenarioInput(files, "target.json", JsonSerializer.Serialize(new { segment_id = "segment-main", item_id = actionId }));
        var finished = ScenarioSuccess(await RunScenarioAsync(fixture, ["backend-agent-request-finish", "--id", requestId,
            "--revision", "2", "--actor", actor, "--result-file", result, "--target-file", target]));
        Assert.Equal("completed", finished.GetProperty("state").GetString());
        Assert.Equal(completedProject.Project.Revision, finished.GetProperty("completed_project_revision").GetInt64());
        var stored = await ReadRequest();
        Assert.Equal(actionId, stored.GetProperty("result_target").GetProperty("item_id").GetString());
        Assert.Equal(frozen.GetRawText(), stored.GetProperty("context").GetRawText());
        Assert.True(JsonElement.DeepEquals(created, ScenarioSuccess(await RunScenarioAsync(fixture, createArgs))));
        Assert.Equal(completedProject.Fingerprint, (await fixture.Connection.OpenAsync()).Fingerprint);
        Assert.Equal(5, files.Files.HistoryFiles.Length);
        Assert.False(Directory.Exists(Path.Combine(files.Files.ProjectDirectory, "Production", "Jobs")));

        async Task<JsonElement> ReadRequest() =>
            ScenarioSuccess(await RunScenarioAsync(fixture, ["backend-agent-request", "--id", requestId])).GetProperty("request");
    }

    [Fact]
    public async Task NativeSourceCreate_RejectsStaleAndInvalidDraftsWithoutChangingProjectBytes()
    {
        using var files = new BackendWriteFixture();
        await using var fixture = new BackendHttpFixture(files);
        await fixture.StartAsync();
        var head = await fixture.Connection.OpenAsync();
        var prior = File.ReadAllBytes(files.Files.ProjectPath);
        const string key = "draft-invalid-0001";
        const string valid = """{"title":"  Черновой источник  "}""";
        var form = ScenarioInput(files, "source.json", valid);
        var stale = head with { Project = head.Project with { Revision = head.Project.Revision - 1 } };
        AssertError(await RunScenarioAsync(fixture, SourceCreateArgs(form, key, stale)), 3, "revision_conflict", reconcile: true);
        AssertError(await RunScenarioAsync(fixture, SourceCreateArgs(form, key, head with { Fingerprint = new string('0', 64) })),
            3, "revision_conflict", reconcile: true);
        foreach (var invalid in new[]
        {
            """{}""",
            """{"title":null}""",
            """{"title":"  "}""",
            """{"title":"План","takes":[]}""",
            """{"title":"План","source_id":"source-a"}""",
            """{"title":"План","title":"Второй план"}""",
            JsonSerializer.Serialize(new { title = new string('a', 161) })
        })
        {
            File.WriteAllText(form, invalid);
            AssertError(await RunScenarioAsync(fixture, SourceCreateArgs(form, key, head)), 2, "invalid_arguments", reconcile: false);
        }
        Assert.Equal(prior, File.ReadAllBytes(files.Files.ProjectPath));
        Assert.Single(files.Files.HistoryFiles);
        File.WriteAllText(form, valid);
        var saved = ScenarioSuccess(await RunScenarioAsync(fixture, SourceCreateArgs(form, key, head)));
        var current = await fixture.Connection.OpenAsync();
        Assert.Equal(head.Project.Revision + 1, current.Project.Revision);
        Assert.Equal(head.Project.Sources.Length + 1, current.Project.Sources.Length);
        var source = Assert.Single(current.Project.Sources, row => row.Id == saved.GetProperty("source_id").GetString());
        Assert.Equal("Черновой источник", source.Title);
        Assert.Empty(source.Takes);
        Assert.Equal(2, files.Files.HistoryFiles.Length);
    }

    [Fact]
    public async Task NativeSourceCreate_RecoversStableIdAfterLostReplyAndRestart()
    {
        using var files = new BackendWriteFixture();
        const string key = "draft-lost-reply-01";
        var form = ScenarioInput(files, "source.json", """{"title":"Черновой источник"}""");
        string[] args;
        JsonElement receipt;
        string projectSha;
        await using (var fixture = new BackendHttpFixture(files))
        {
            await fixture.StartAsync();
            var head = await fixture.Connection.OpenAsync();
            args = SourceCreateArgs(form, key, head);
            using var transport = fixture.Client();
            var posts = 0;
            await WithStubAsync(async context =>
            {
                Assert.Equal(BackendHttpServer.ScenarioSourceCreateRoute, context.Request.Path.Value);
                Assert.Equal(HttpMethods.Post, context.Request.Method);
                Interlocked.Increment(ref posts);
                using var body = await JsonDocument.ParseAsync(context.Request.Body, cancellationToken: context.RequestAborted);
                using var content = new StringContent(body.RootElement.GetRawText(), Encoding.UTF8, "application/json");
                using var saved = await transport.PostAsync(BackendHttpServer.ScenarioSourceCreateRoute, content, context.RequestAborted);
                saved.EnsureSuccessStatusCode();
                context.Abort();
            }, async address =>
            {
                AssertError(await RunAsync(address, Fingerprint, args), 1, "backend_unavailable", reconcile: true);
                Assert.Equal(1, Volatile.Read(ref posts));
            });
            var repeats = await Task.WhenAll(RunScenarioAsync(fixture, args), RunScenarioAsync(fixture, args));
            receipt = ScenarioSuccess(repeats[0]);
            Assert.True(JsonElement.DeepEquals(receipt, ScenarioSuccess(repeats[1])));
            var saved = await fixture.Connection.OpenAsync();
            projectSha = saved.Fingerprint;
            Assert.Equal(head.Project.Revision + 1, saved.Project.Revision);
            var source = Assert.Single(saved.Project.Sources, row => row.Title == "Черновой источник");
            Assert.Equal(source.Id, receipt.GetProperty("source_id").GetString());
            Assert.Equal(2, files.Files.HistoryFiles.Length);
        }
        await using var restarted = new BackendHttpFixture(files);
        await restarted.StartAsync(migrate: false);
        Assert.True(JsonElement.DeepEquals(receipt, ScenarioSuccess(await RunScenarioAsync(restarted, args))));
        File.WriteAllText(form, """{"title":"Другой источник"}""");
        AssertError(await RunScenarioAsync(restarted, args), 3, "scenario_action_key_conflict", reconcile: true);
        var current = await restarted.Connection.OpenAsync();
        Assert.Equal(projectSha, current.Fingerprint);
        Assert.Equal(2, files.Files.HistoryFiles.Length);
    }

    private static string[] SourceCreateArgs(string form, string key, OpenedBackendProject baseline, string actor = "author") =>
        ["backend-scenario-source-create", "--file", form, "--client-key", key, "--sha256", baseline.Fingerprint,
            "--revision", baseline.Project.Revision.ToString(CultureInfo.InvariantCulture), "--actor", actor];
}
