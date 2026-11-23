using System.Globalization;
using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.AspNetCore.Http;
using TrueMoon.Tales.Studio.Backend;
using TrueMoon.Tales.Studio.Backend.Http;
using Xunit;

namespace TrueMoon.Tales.Studio.Tests.Backend;

public sealed partial class BackendNativeCliTests
{
    [Fact]
    public async Task NativeBrief_MigratesLegacyFieldsAndFreezesBriefForEachNewRequest()
    {
        using var files = new BackendWriteFixture(scenario: true);
        await using var fixture = new BackendHttpFixture(files);
        var goal = BackendAgentRequestCreationTests.Configure(files);
        var project = JsonNode.Parse(File.ReadAllText(files.Files.ProjectPath))!.AsObject();
        var legacyBrief = JsonNode.Parse("""
            {"synopsis":"Первый замысел 🌓","visual_style":"Холодный свет","continuity_rules":"Часы на правой руке","agent_notes":"Временная заметка","opaque":{"number":9007199254740993}}
            """);
        project["brief"] = legacyBrief;
        File.WriteAllText(files.Files.ProjectPath, project.ToJsonString());
        var original = File.ReadAllBytes(files.Files.ProjectPath);

        await fixture.StartAsync();
        Assert.Equal(original, File.ReadAllBytes(Assert.Single(files.Files.HistoryFiles)));
        var migrated = await fixture.Connection.OpenAsync();
        var migratedBytes = File.ReadAllBytes(files.Files.ProjectPath);
        var frozenLegacy = (await files.Files.Store().OpenAsync()).OriginalDocument.GetProperty("legacy_document").GetRawText();
        var before = ScenarioSuccess(await RunScenarioAsync(fixture, ["backend-brief"]));
        Assert.Equal(migrated.Fingerprint, before.GetProperty("fingerprint").GetString());
        Assert.Equal(migrated.Project.Revision, before.GetProperty("project_revision").GetInt64());
        var card = before.GetProperty("brief");
        Assert.Equal("Первый замысел 🌓", card.GetProperty("synopsis").GetString());
        Assert.Equal("Холодный свет", card.GetProperty("visual_style").GetString());
        Assert.Equal("Часы на правой руке", card.GetProperty("continuity_rules").GetString());
        Assert.Equal("Временная заметка", card.GetProperty("agent_notes").GetString());
        Assert.Equal(migratedBytes, File.ReadAllBytes(files.Files.ProjectPath));
        Assert.Single(files.Files.HistoryFiles);

        var first = await BriefRequestAsync(fixture, goal, "brief-context-before", migrated);
        var firstId = first.GetProperty("id").GetString()!;
        var requestPath = Path.Combine(files.Files.ProjectDirectory, "Production", "AgentRequests", firstId + ".json");
        var firstBytes = File.ReadAllBytes(requestPath);
        var patch = ScenarioInput(files, "brief.json", """{"agent_notes":"","synopsis":"Уточнённый замысел"}""");
        var updated = ScenarioSuccess(await RunScenarioAsync(fixture, BriefArgs(patch, "brief-patch-migration", migrated)));
        Assert.True(updated.GetProperty("changed").GetBoolean());
        var current = await fixture.Connection.OpenAsync();
        Assert.Equal(migrated.Project.Revision + 1, updated.GetProperty("project_revision").GetInt64());
        Assert.Equal(migrated.Project.Revision + 1, current.Project.Revision);
        Assert.Equal(2, files.Files.HistoryFiles.Length);
        var reopened = await files.Files.Store().OpenAsync();
        Assert.Equal(frozenLegacy, reopened.OriginalDocument.GetProperty("legacy_document").GetRawText());
        Assert.Equal(9007199254740993, reopened.Project.Properties["brief"].GetProperty("opaque").GetProperty("number").GetInt64());

        var live = ScenarioSuccess(await RunScenarioAsync(fixture, ["backend-agent-context"])).GetProperty("context");
        var second = await BriefRequestAsync(fixture, goal, "brief-context-after", current);
        var firstContext = await ReadRequestContextAsync(first);
        var secondContext = await ReadRequestContextAsync(second);
        Assert.Equal("Первый замысел 🌓", ContextBrief(firstContext).GetProperty("synopsis").GetString());
        Assert.Equal("Временная заметка", ContextBrief(firstContext).GetProperty("agent_notes").GetString());
        Assert.Equal(firstBytes, File.ReadAllBytes(requestPath));
        Assert.True(JsonElement.DeepEquals(ContextBrief(live), ContextBrief(secondContext)));
        Assert.Equal("Уточнённый замысел", ContextBrief(secondContext).GetProperty("synopsis").GetString());
        Assert.Equal("", ContextBrief(secondContext).GetProperty("agent_notes").GetString());
        Assert.Equal("Холодный свет", ContextBrief(secondContext).GetProperty("visual_style").GetString());
        Assert.Equal("Часы на правой руке", ContextBrief(secondContext).GetProperty("continuity_rules").GetString());
        Assert.Equal(2, files.Files.HistoryFiles.Length);
        Assert.False(Directory.Exists(Path.Combine(files.Files.ProjectDirectory, "Production", "Jobs")));

        async Task<JsonElement> ReadRequestContextAsync(JsonElement receipt)
        {
            var detail = ScenarioSuccess(await RunScenarioAsync(fixture, ["backend-agent-request", "--id", receipt.GetProperty("id").GetString()!]));
            var snapshot = detail.GetProperty("request").GetProperty("context");
            var body = JsonNode.Parse(snapshot.GetRawText())!.AsObject();
            var sha = body["snapshot_sha256"]!.GetValue<string>();
            body.Remove("snapshot_sha256");
            Assert.Equal(receipt.GetProperty("snapshot_sha256").GetString(), sha);
            Assert.Equal(Convert.ToHexString(SHA256.HashData(JsonSerializer.SerializeToUtf8Bytes(body))), sha);
            return snapshot;
        }
    }

    [Fact]
    public async Task NativeBrief_RejectsStaleBaselineAndInvalidPatchAndSkipsNoOp()
    {
        using var files = new BackendWriteFixture();
        await using var fixture = new BackendHttpFixture(files);
        await fixture.StartAsync();
        var head = await fixture.Connection.OpenAsync();
        var saved = File.ReadAllBytes(files.Files.ProjectPath);
        var empty = ScenarioSuccess(await RunScenarioAsync(fixture, ["backend-brief"])).GetProperty("brief");
        Assert.Equal(4, empty.EnumerateObject().Count());
        Assert.All(empty.EnumerateObject(), field => Assert.Equal("", field.Value.GetString()));
        var patch = ScenarioInput(files, "brief.json", """{"visual_style":"Ночной свет"}""");
        var stale = head with { Project = head.Project with { Revision = head.Project.Revision - 1 } };
        AssertError(await RunScenarioAsync(fixture, BriefArgs(patch, "brief-validation-001", stale)), 3, "revision_conflict", reconcile: true);
        AssertError(await RunScenarioAsync(fixture, BriefArgs(patch, "brief-validation-001", head with { Fingerprint = new string('0', 64) })),
            3, "revision_conflict", reconcile: true);
        foreach (var invalid in new[] { "{}", """{"synopsis":null}""", """{"extra":"bad"}""", """{"synopsis":"x","synopsis":"y"}""" })
        {
            File.WriteAllText(patch, invalid);
            AssertError(await RunScenarioAsync(fixture, BriefArgs(patch, "brief-validation-001", head)), 2, "invalid_arguments", reconcile: false);
        }
        using var client = fixture.Client();
        using var body = JsonContent.Create(new
        {
            expected_revision = head.Project.Revision, fingerprint = head.Fingerprint, client_key = "brief-validation-001",
            brief = new { synopsis = "verbatim", extra = "invalid" }
        });
        using var response = await client.PatchAsync(BackendHttpServer.BriefRoute, body);
        Assert.Equal(422, (int)response.StatusCode);
        File.WriteAllText(patch, """{"visual_style":""}""");
        var same = ScenarioSuccess(await RunScenarioAsync(fixture, BriefArgs(patch, "brief-validation-001", head)));
        Assert.False(same.GetProperty("changed").GetBoolean());
        Assert.Equal(head.Project.Revision, same.GetProperty("project_revision").GetInt64());
        Assert.Equal(saved, File.ReadAllBytes(files.Files.ProjectPath));
        Assert.Single(files.Files.HistoryFiles);
    }

    [Fact]
    public async Task NativeBrief_RecoversLostPatchReceiptAfterConcurrentRetryAndRestart()
    {
        using var files = new BackendWriteFixture();
        const string key = "brief-canonical-retry";
        var patch = ScenarioInput(files, "brief.json", """{"visual_style":"Ночь","synopsis":"Замысел"}""");
        string[] args;
        JsonElement receipt;
        string currentFingerprint;
        await using (var fixture = new BackendHttpFixture(files))
        {
            await fixture.StartAsync();
            var head = await fixture.Connection.OpenAsync();
            args = BriefArgs(patch, key, head);
            using var client = fixture.Client();
            var requests = 0;
            await WithStubAsync(async context =>
            {
                Assert.Equal(BackendHttpServer.BriefRoute, context.Request.Path.Value);
                Assert.Equal(HttpMethods.Patch, context.Request.Method);
                Interlocked.Increment(ref requests);
                using var form = await JsonDocument.ParseAsync(context.Request.Body);
                using var body = new StringContent(form.RootElement.GetRawText(), Encoding.UTF8, "application/json");
                using var committed = await client.PatchAsync(BackendHttpServer.BriefRoute, body, context.RequestAborted);
                committed.EnsureSuccessStatusCode();
                context.Abort();
            }, async address =>
            {
                AssertError(await RunAsync(address, Fingerprint, args), 1, "backend_unavailable", reconcile: true);
                Assert.Equal(1, Volatile.Read(ref requests));
            });
            var committed = await fixture.Connection.OpenAsync();
            Assert.Equal(head.Project.Revision + 1, committed.Project.Revision);
            File.WriteAllText(patch, """{"synopsis":"Замысел","visual_style":"Ночь"}""");
            var retries = await Task.WhenAll(RunScenarioAsync(fixture, args), RunScenarioAsync(fixture, args));
            receipt = ScenarioSuccess(retries[0]);
            Assert.True(receipt.GetProperty("changed").GetBoolean());
            Assert.True(JsonElement.DeepEquals(receipt, ScenarioSuccess(retries[1])));
            Assert.Equal(committed.Fingerprint, (await fixture.Connection.OpenAsync()).Fingerprint);
            Assert.Equal(2, files.Files.HistoryFiles.Length);

            var nextPatch = ScenarioInput(files, "next.json", """{"synopsis":"Новый замысел"}""");
            ScenarioSuccess(await RunScenarioAsync(fixture, BriefArgs(nextPatch, "brief-next-revision", committed)));
            currentFingerprint = (await fixture.Connection.OpenAsync()).Fingerprint;
        }
        await using var restarted = new BackendHttpFixture(files);
        await restarted.StartAsync(migrate: false);
        Assert.True(JsonElement.DeepEquals(receipt, ScenarioSuccess(await RunScenarioAsync(restarted, args))));
        var current = await restarted.Connection.OpenAsync();
        AssertError(await RunScenarioAsync(restarted, BriefArgs(patch, key, current)), 3, "brief_key_conflict", reconcile: true);
        File.WriteAllText(patch, """{"synopsis":"Другой замысел"}""");
        AssertError(await RunScenarioAsync(restarted, args), 3, "brief_key_conflict", reconcile: true);
        Assert.Equal("Новый замысел", ScenarioSuccess(await RunScenarioAsync(restarted, ["backend-brief"]))
            .GetProperty("brief").GetProperty("synopsis").GetString());
        Assert.Equal(currentFingerprint, (await restarted.Connection.OpenAsync()).Fingerprint);
        Assert.Equal(3, files.Files.HistoryFiles.Length);
    }

    private static JsonElement ContextBrief(JsonElement context) => context.GetProperty("project").GetProperty("properties").GetProperty("brief");

    private static async Task<JsonElement> BriefRequestAsync(BackendHttpFixture fixture, string goal, string key, OpenedBackendProject baseline) =>
        ScenarioSuccess(await RunScenarioAsync(fixture, ["backend-agent-request-create", "--goal-file", goal,
            "--client-key", key, "--revision", baseline.Project.Revision.ToString(CultureInfo.InvariantCulture), "--sha256", baseline.Fingerprint]));

    private static string[] BriefArgs(string path, string key, OpenedBackendProject baseline) =>
        ["backend-brief-update", "--file", path, "--client-key", key, "--revision", baseline.Project.Revision.ToString(CultureInfo.InvariantCulture),
            "--sha256", baseline.Fingerprint];
}
