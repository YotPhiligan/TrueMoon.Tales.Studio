using System.Globalization;
using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.AspNetCore.Http;
using TrueMoon.Tales.Studio.Backend;
using TrueMoon.Tales.Studio.Backend.Http;
using TrueMoon.Tales.Studio.Contracts.Projects;
using Xunit;

namespace TrueMoon.Tales.Studio.Tests.Backend;

public sealed partial class BackendNativeCliTests
{
    [Fact]
    public async Task NativeScenarioOrder_MovesWithinSegmentAndChecksTimingWithoutMovingClips()
    {
        using var files = new BackendWriteFixture(scenario: true);
        await using var fixture = new BackendHttpFixture(files);
        await fixture.StartAsync();
        var (head, first, second) = await PrepareScenarioOrderAsync(fixture);
        var originalHistory = files.Files.HistoryFiles.Length;
        var args = ScenarioOperationArgs("move", "scenario-order-0001", head, second, "--before", first);
        var receipt = ScenarioSuccess(await RunScenarioAsync(fixture, args));
        Assert.Equal(second, receipt.GetProperty("action_id").GetString());
        Assert.Equal(head.Project.Revision + 1, receipt.GetProperty("project_revision").GetInt64());
        var moved = await fixture.Connection.OpenAsync();
        Assert.Equal(new[] { "clip-main", second, first }, ScenarioOrder(moved));
        Assert.Equal(head.Project.Montage.Clips, moved.Project.Montage.Clips);
        Assert.Equal(head.Project.Montage.Windows, moved.Project.Montage.Windows);
        Assert.Equal(originalHistory + 1, files.Files.HistoryFiles.Length);

        var noChange = ScenarioSuccess(await RunScenarioAsync(fixture,
            ScenarioOperationArgs("move", "scenario-order-noop", moved, second, "--before", first)));
        Assert.False(noChange.GetProperty("changed").GetBoolean());
        AssertError(await RunScenarioAsync(fixture, ScenarioOperationArgs("move", "scenario-order-wrong", moved, second, "--before", "clip-extra")),
            1, "invalid_scenario_edit", reconcile: true);
        AssertError(await RunScenarioAsync(fixture, ScenarioOperationArgs("move", "scenario-order-timing", moved, second, "--before", "clip-main")),
            1, "scenario_order_timing", reconcile: true);
        Assert.Equal(moved.Fingerprint, (await fixture.Connection.OpenAsync()).Fingerprint);
        Assert.Equal(originalHistory + 1, files.Files.HistoryFiles.Length);

        ScenarioSuccess(await RunScenarioAsync(fixture, ScenarioOperationArgs("move", "scenario-order-to-end", moved, second)));
        var atEnd = await fixture.Connection.OpenAsync();
        Assert.Equal(new[] { "clip-main", first, second }, ScenarioOrder(atEnd));
        Assert.Equal(head.Project.Montage.Clips, atEnd.Project.Montage.Clips);
        Assert.Equal(originalHistory + 2, files.Files.HistoryFiles.Length);
    }

    [Fact]
    public async Task NativeScenarioSource_LinksExistingSourceAndUpdatesPurposeWithoutDuplicatingIt()
    {
        using var files = new BackendWriteFixture(scenario: true);
        await using var fixture = new BackendHttpFixture(files);
        await fixture.StartAsync();
        var head = await fixture.Connection.OpenAsync();
        var before = JsonSerializer.Serialize(head.Project.Sources);
        var initialLinks = ScenarioLinks(head);
        Assert.Equal("source-a", Assert.Single(initialLinks).SourceId);
        var args = ScenarioOperationArgs("link-source", "scenario-source-0001", head, "clip-main", "--source", "source-b", "--purpose", "реакция");
        var receipt = ScenarioSuccess(await RunScenarioAsync(fixture, args));
        Assert.Equal("clip-main", receipt.GetProperty("action_id").GetString());
        var linked = await fixture.Connection.OpenAsync();
        Assert.Equal(head.Project.Revision + 1, linked.Project.Revision);
        Assert.Equal(new[] { "source-a", "source-b" }, ScenarioLinks(linked).Select(link => link.SourceId));
        Assert.Equal("реакция", ScenarioLinks(linked)[1].Purpose);
        Assert.Equal(initialLinks[0], ScenarioLinks(linked)[0]);

        AssertError(await RunScenarioAsync(fixture,
            ScenarioOperationArgs("link-source", "scenario-source-unknown", linked, "clip-main", "--source", "missing-source")),
            1, "invalid_scenario_edit", reconcile: true);
        var noChange = ScenarioSuccess(await RunScenarioAsync(fixture,
            ScenarioOperationArgs("link-source", "scenario-source-noop", linked, "clip-main", "--source", "source-b", "--purpose", "реакция")));
        Assert.False(noChange.GetProperty("changed").GetBoolean());
        Assert.Equal(linked.Fingerprint, (await fixture.Connection.OpenAsync()).Fingerprint);
        Assert.Equal(2, files.Files.HistoryFiles.Length);

        ScenarioSuccess(await RunScenarioAsync(fixture,
            ScenarioOperationArgs("link-source", "scenario-source-revise", linked, "clip-main", "--source", "source-b", "--purpose", "деталь")));
        var revised = await fixture.Connection.OpenAsync();
        Assert.Equal("деталь", Assert.Single(ScenarioLinks(revised), link => link.SourceId == "source-b").Purpose);
        ScenarioSuccess(await RunScenarioAsync(fixture,
            ScenarioOperationArgs("link-source", "scenario-source-clear", revised, "clip-main", "--source", "source-b")));
        var cleared = await fixture.Connection.OpenAsync();
        Assert.Null(Assert.Single(ScenarioLinks(cleared), link => link.SourceId == "source-b").Purpose);
        Assert.Equal(2, ScenarioLinks(cleared).Length);
        Assert.Equal(before, JsonSerializer.Serialize(cleared.Project.Sources));
        Assert.Equal(head.Project.Montage.Clips, cleared.Project.Montage.Clips);
        Assert.Equal(head.Project.Montage.Windows, cleared.Project.Montage.Windows);
        Assert.Equal(4, files.Files.HistoryFiles.Length);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task NativeScenarioOperations_RejectConflictsAndReplayOneCommitAfterLostReplyAndRestart(bool move)
    {
        using var files = new BackendWriteFixture(scenario: true);
        const string key = "scenario-operations-001";
        string[] args;
        JsonElement receipt;
        string committedFingerprint;
        int originalHistory;
        await using (var fixture = new BackendHttpFixture(files))
        {
            await fixture.StartAsync();
            var (head, first, second) = await PrepareScenarioOrderAsync(fixture);
            originalHistory = files.Files.HistoryFiles.Length;
            var stale = head with { Project = head.Project with { Revision = head.Project.Revision - 1 } };
            AssertError(await RunScenarioAsync(fixture, BuildArgs(stale)), 3, "revision_conflict", reconcile: true);
            AssertError(await RunScenarioAsync(fixture, BuildArgs(head with { Fingerprint = new string('0', 64) })),
                3, "revision_conflict", reconcile: true);
            Assert.Equal(head.Fingerprint, (await fixture.Connection.OpenAsync()).Fingerprint);
            Assert.Equal(originalHistory, files.Files.HistoryFiles.Length);

            args = BuildArgs(head);
            using var client = fixture.Client();
            using var mixed = await client.PostAsJsonAsync(BackendHttpServer.ScenarioActionRoute, new
            {
                expected_revision = head.Project.Revision, fingerprint = head.Fingerprint, client_key = key,
                move = new { segment_id = "segment-main", action_id = second, before_action_id = first },
                link = new { segment_id = "segment-main", action_id = second, source_id = "source-b" }
            });
            Assert.Equal(422, (int)mixed.StatusCode);
            Assert.Equal(head.Fingerprint, (await fixture.Connection.OpenAsync()).Fingerprint);

            var posts = 0;
            await WithStubAsync(async context =>
            {
                Interlocked.Increment(ref posts);
                Assert.Equal(BackendHttpServer.ScenarioActionRoute, context.Request.Path.Value);
                Assert.Equal(HttpMethods.Post, context.Request.Method);
                using var form = await JsonDocument.ParseAsync(context.Request.Body);
                using var body = new StringContent(form.RootElement.GetRawText(), Encoding.UTF8, "application/json");
                using var saved = await client.PostAsync(BackendHttpServer.ScenarioActionRoute, body, context.RequestAborted);
                saved.EnsureSuccessStatusCode();
                context.Abort();
            }, async address =>
            {
                AssertError(await RunAsync(address, Fingerprint, args), 1, "backend_unavailable", reconcile: true);
                Assert.Equal(1, Volatile.Read(ref posts));
            });
            var committed = await fixture.Connection.OpenAsync();
            Assert.Equal(head.Project.Revision + 1, committed.Project.Revision);
            committedFingerprint = committed.Fingerprint;
            var repeats = await Task.WhenAll(RunScenarioAsync(fixture, args), RunScenarioAsync(fixture, args));
            receipt = ScenarioSuccess(repeats[0]);
            Assert.True(receipt.GetProperty("changed").GetBoolean());
            Assert.True(JsonElement.DeepEquals(receipt, ScenarioSuccess(repeats[1])));
            var otherOperation = move
                ? ScenarioOperationArgs("link-source", key, head, second, "--source", "source-b")
                : ScenarioOperationArgs("move", key, head, second, "--before", first);
            AssertError(await RunScenarioAsync(fixture, otherOperation), 3, "scenario_action_key_conflict", reconcile: true);
            AssertError(await RunScenarioAsync(fixture, BuildArgs(committed)), 3, "scenario_action_key_conflict", reconcile: true);
            Assert.Equal(originalHistory + 1, files.Files.HistoryFiles.Length);

            string[] BuildArgs(OpenedBackendProject baseline) => move
                ? ScenarioOperationArgs("move", key, baseline, second, "--before", first)
                : ScenarioOperationArgs("link-source", key, baseline, "clip-main", "--source", "source-b");
        }

        await using var restarted = new BackendHttpFixture(files);
        await restarted.StartAsync(migrate: false);
        Assert.True(JsonElement.DeepEquals(receipt, ScenarioSuccess(await RunScenarioAsync(restarted, args))));
        Assert.Equal(committedFingerprint, (await restarted.Connection.OpenAsync()).Fingerprint);
        Assert.Equal(originalHistory + 1, files.Files.HistoryFiles.Length);
    }

    [Fact]
    public async Task NativeScenarioOperations_ReplaySaveReceiptWithPreviousSubmissionDigest()
    {
        const string key = "scenario-legacy-key-001";
        using var files = new BackendWriteFixture(scenario: true);
        var store = files.Files.Store();
        var original = await store.OpenAsync();
        var baseline = await store.MigrateAsync(original, original.Project.Revision, "test");
        var action = new ScenarioActionDraft { SegmentId = "segment-main", ActionId = "clip-main", Title = "Уточнение" };
        var previousRequest = new
        {
            expected_revision = baseline.Project.Revision, fingerprint = baseline.Fingerprint.ToUpperInvariant(), client_key = key,
            action, actor = "author"
        };
        var digest = Convert.ToHexString(SHA256.HashData(JsonSerializer.SerializeToUtf8Bytes(previousRequest, AgentRequestDraft.Json)));
        var receipt = new ScenarioActionReceipt(1, baseline.Project.Id, action.SegmentId, action.ActionId, baseline.Project.Revision + 1, digest, true);
        var keyHash = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(key)));
        var edited = BackendScenarioEdit.ApplyAction(baseline.Project, action.ToCommand());
        var committed = await store.SaveAsync(baseline, ScenarioActionWrite.Record(edited, keyHash, receipt), baseline.Project.Revision, "author");

        await using var fixture = new BackendHttpFixture(files);
        await fixture.StartAsync(migrate: false);
        var form = ScenarioInput(files, "previous-action.json", JsonSerializer.Serialize(action, AgentRequestDraft.Json));
        var replay = ScenarioSuccess(await RunScenarioAsync(fixture, ["backend-scenario-action-save", "--file", form,
            "--client-key", key, "--revision", baseline.Project.Revision.ToString(CultureInfo.InvariantCulture), "--sha256", baseline.Fingerprint]));
        Assert.Equal(receipt, replay.Deserialize<ScenarioActionReceipt>(AgentRequestDraft.Json));
        Assert.Equal(committed.Fingerprint, (await fixture.Connection.OpenAsync()).Fingerprint);
        Assert.Equal(2, files.Files.HistoryFiles.Length);
    }

    private static async Task<(OpenedBackendProject Head, string First, string Second)> PrepareScenarioOrderAsync(BackendHttpFixture fixture)
    {
        var current = await fixture.Connection.OpenAsync();
        current = await fixture.Connection.EditScenarioAsync(new SaveAction("segment-main", null, "Первый план", "", 24), current);
        var first = ScenarioOrder(current)[^1];
        current = await fixture.Connection.EditScenarioAsync(new SaveAction("segment-main", null, "Второй план", "", 24), current);
        return (current, first, ScenarioOrder(current)[^1]);
    }

    private static string[] ScenarioOrder(OpenedBackendProject snapshot) =>
        snapshot.Project.Segments.Single(segment => segment.Id == "segment-main").Actions.Select(action => action.Id).ToArray();

    private static ActionSourceContract[] ScenarioLinks(OpenedBackendProject snapshot) =>
        snapshot.Project.Segments.Single(segment => segment.Id == "segment-main").Actions.Single(action => action.Id == "clip-main").Sources;

    private static string[] ScenarioOperationArgs(string operation, string key, OpenedBackendProject baseline, string action, params string[] options) =>
        ["backend-scenario-action-" + operation, "--segment", "segment-main", "--action", action, "--client-key", key,
            "--revision", baseline.Project.Revision.ToString(CultureInfo.InvariantCulture), "--sha256", baseline.Fingerprint, .. options];

    private static Task<CliResult> RunScenarioAsync(BackendHttpFixture fixture, string[] args) => RunAsync(fixture.Http.Address, fixture.Http.Token, args);
}
