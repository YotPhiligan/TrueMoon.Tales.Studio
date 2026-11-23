using System.Globalization;
using System.Net.Http.Json;
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
    public async Task NativeScenarioSegment_CreatesWindowAndEditsTextWithoutChangingTimingOrActions()
    {
        using var files = new BackendWriteFixture(scenario: true);
        await using var fixture = new BackendHttpFixture(files);
        await fixture.StartAsync();
        var head = await fixture.Connection.OpenAsync();
        Assert.Throws<ProjectStoreBusyException>(() => ProjectWriterLease.Acquire(BackendProjectAccess.Read(files.Options)));
        var before = File.ReadAllBytes(files.Files.ProjectPath);
        var path = ScenarioInput(files, "segment.json", """
            {"name":" Ночной эпизод ","description":"Действие у костра.","start_frame":240,"duration_frames":60}
            """);
        var receipt = ScenarioSuccess(await RunScenarioAsync(fixture, SegmentArgs(path, "segment-create-0001", head)));
        var id = receipt.GetProperty("segment_id").GetString()!;
        Assert.True(receipt.GetProperty("changed").GetBoolean());
        Assert.False(receipt.TryGetProperty("action_id", out _));
        Assert.Equal(head.Project.Revision + 1, receipt.GetProperty("project_revision").GetInt64());
        var created = await fixture.Connection.OpenAsync();
        var segment = Assert.Single(created.Project.Segments, row => row.Id == id);
        var window = Assert.Single(created.Project.Montage.Windows, row => row.SegmentId == id);
        Assert.Equal("Ночной эпизод", segment.Name);
        Assert.Equal("Действие у костра.", segment.Description);
        Assert.Equal(240, segment.PlannedWindow!.StartFrame);
        Assert.Equal(60, segment.PlannedWindow.DurationFrames);
        Assert.Equal(240, window.StartFrame);
        Assert.Equal(60, window.DurationFrames);
        Assert.False(BackendScenarioEdit.UsesLegacyLayout(created.Project, window.Id));
        Assert.Equal(head.Project.Montage.Windows, created.Project.Montage.Windows.Where(row => row.Id != window.Id).ToArray());
        Assert.Equal(head.Project.Montage.Clips, created.Project.Montage.Clips);
        Assert.Equal(2, files.Files.HistoryFiles.Length);
        Assert.Contains(files.Files.HistoryFiles, history => File.ReadAllBytes(history).SequenceEqual(before));

        var actionForm = ScenarioInput(files, "action.json", JsonSerializer.Serialize(new { segment_id = id, title = "Реакция", planned_duration = 12 }));
        var action = ScenarioSuccess(await RunScenarioAsync(fixture, ScenarioArgs(actionForm, "segment-child-00001", created)));
        var actionId = action.GetProperty("action_id").GetString();
        var withAction = await fixture.Connection.OpenAsync();
        File.WriteAllText(path, JsonSerializer.Serialize(new { segment_id = id, name = "Привал", description = "Уточнённое назначение" }));
        var editArgs = SegmentArgs(path, "segment-revise-0001", withAction);
        var edited = ScenarioSuccess(await RunScenarioAsync(fixture, editArgs));
        var current = await fixture.Connection.OpenAsync();
        var revised = Assert.Single(current.Project.Segments, row => row.Id == id);
        Assert.Equal(id, edited.GetProperty("segment_id").GetString());
        Assert.Equal(withAction.Project.Revision + 1, current.Project.Revision);
        Assert.Equal("Привал", revised.Name);
        Assert.Equal("Уточнённое назначение", revised.Description);
        Assert.Equal(segment.PlannedWindow, revised.PlannedWindow);
        Assert.Equal(actionId, Assert.Single(revised.Actions).Id);
        Assert.Equal(window, Assert.Single(current.Project.Montage.Windows, row => row.SegmentId == id));
        Assert.Equal(head.Project.Montage.Clips, current.Project.Montage.Clips);
        Assert.True(JsonElement.DeepEquals(edited, ScenarioSuccess(await RunScenarioAsync(fixture, editArgs))));
        var noChange = ScenarioSuccess(await RunScenarioAsync(fixture, SegmentArgs(path, "segment-revise-noop", current)));
        Assert.False(noChange.GetProperty("changed").GetBoolean());
        Assert.Equal(current.Fingerprint, (await fixture.Connection.OpenAsync()).Fingerprint);
        Assert.Equal(4, files.Files.HistoryFiles.Length);
        Assert.False(Directory.Exists(Path.Combine(files.Files.ProjectDirectory, "Production", "Jobs")));
    }

    [Fact]
    public async Task NativeScenarioSegment_RejectsStaleBaselineAndInvalidWindowWithoutWrites()
    {
        const string key = "segment-refusal-0001";
        const string valid = """{"name":"Между окнами","start_frame":240,"duration_frames":60}""";
        using var files = new BackendWriteFixture(scenario: true);
        await using var fixture = new BackendHttpFixture(files);
        await fixture.StartAsync();
        var head = await fixture.Connection.OpenAsync();
        var original = File.ReadAllBytes(files.Files.ProjectPath);
        var path = ScenarioInput(files, "segment.json", valid);
        var stale = head with { Project = head.Project with { Revision = head.Project.Revision - 1 } };
        AssertError(await RunScenarioAsync(fixture, SegmentArgs(path, key, stale)), 3, "revision_conflict", reconcile: true);
        AssertError(await RunScenarioAsync(fixture, SegmentArgs(path, key, head with { Fingerprint = new string('0', 64) })),
            3, "revision_conflict", reconcile: true);
        foreach (var timing in new[] { (239, 3), (240, 61), (240, 0), (-1, 1), (int.MaxValue, 1) })
        {
            File.WriteAllText(path, JsonSerializer.Serialize(new { name = "Недопустимое окно", start_frame = timing.Item1, duration_frames = timing.Item2 }));
            AssertError(await RunScenarioAsync(fixture, SegmentArgs(path, key, head)), 1, "scenario_window_conflict", reconcile: true);
        }
        File.WriteAllText(path, """{"segment_id":"missing-segment","name":"Новый текст"}""");
        AssertError(await RunScenarioAsync(fixture, SegmentArgs(path, key, head)), 1, "invalid_scenario_edit", reconcile: true);
        File.WriteAllText(path, """{"segment_id":"segment-main","name":"Попытка сдвига","start_frame":1,"duration_frames":240}""");
        AssertError(await RunScenarioAsync(fixture, SegmentArgs(path, key, head)), 2, "invalid_arguments", reconcile: false);
        File.WriteAllText(path, """{"name":"Неполная форма","duration_frames":60}""");
        AssertError(await RunScenarioAsync(fixture, SegmentArgs(path, key, head)), 2, "invalid_arguments", reconcile: false);
        using var client = fixture.Client();
        using var response = await client.PostAsJsonAsync(BackendHttpServer.ScenarioSegmentRoute, new
        {
            expected_revision = head.Project.Revision, fingerprint = head.Fingerprint, client_key = key,
            action = new { segment_id = "segment-main", title = "Чужой контракт" }
        });
        Assert.Equal(422, (int)response.StatusCode);
        Assert.Equal(original, File.ReadAllBytes(files.Files.ProjectPath));
        Assert.Single(files.Files.HistoryFiles);

        File.WriteAllText(path, valid);
        var args = SegmentArgs(path, key, head);
        var replies = await Task.WhenAll(RunScenarioAsync(fixture, args), RunScenarioAsync(fixture, args));
        var receipt = ScenarioSuccess(replies[0]);
        Assert.True(JsonElement.DeepEquals(receipt, ScenarioSuccess(replies[1])));
        var id = receipt.GetProperty("segment_id").GetString();
        var current = await fixture.Connection.OpenAsync();
        Assert.Single(current.Project.Segments, row => row.Id == id);
        Assert.Single(current.Project.Montage.Windows, row => row.SegmentId == id);
        Assert.Equal(2, files.Files.HistoryFiles.Length);
    }

    [Fact]
    public async Task NativeScenarioSegment_ReplaysLostCreateWithSameIdAfterEditAndRestart()
    {
        const string key = "segment-replay-00001";
        using var files = new BackendWriteFixture(scenario: true);
        var path = ScenarioInput(files, "segment.json", """{"name":"Начало","start_frame":240,"duration_frames":60}""");
        string[] args;
        JsonElement receipt;
        string changedFingerprint;
        string id;
        await using (var fixture = new BackendHttpFixture(files))
        {
            await fixture.StartAsync();
            var head = await fixture.Connection.OpenAsync();
            args = SegmentArgs(path, key, head);
            var sends = 0;
            using var client = fixture.Client();
            await WithStubAsync(async context =>
            {
                Assert.Equal(BackendHttpServer.ScenarioSegmentRoute, context.Request.Path.Value);
                Assert.Equal(HttpMethods.Post, context.Request.Method);
                Interlocked.Increment(ref sends);
                using var form = await JsonDocument.ParseAsync(context.Request.Body);
                using var body = new StringContent(form.RootElement.GetRawText(), Encoding.UTF8, "application/json");
                using var committed = await client.PostAsync(BackendHttpServer.ScenarioSegmentRoute, body, context.RequestAborted);
                committed.EnsureSuccessStatusCode();
                context.Abort();
            }, async address =>
            {
                AssertError(await RunAsync(address, Fingerprint, args), 1, "backend_unavailable", reconcile: true);
                Assert.Equal(1, Volatile.Read(ref sends));
            });
            var saved = await fixture.Connection.OpenAsync();
            Assert.Equal(head.Project.Revision + 1, saved.Project.Revision);
            id = Assert.Single(saved.Project.Segments, row => row.Name == "Начало").Id;
            Assert.Single(saved.Project.Montage.Windows, row => row.SegmentId == id);
            receipt = ScenarioSuccess(await RunScenarioAsync(fixture, args));
            Assert.Equal(id, receipt.GetProperty("segment_id").GetString());
            Assert.Equal(2, files.Files.HistoryFiles.Length);
            var actionForm = ScenarioInput(files, "action.json", """{"segment_id":"segment-main","title":"Чужой запрос"}""");
            AssertError(await RunScenarioAsync(fixture, ScenarioArgs(actionForm, key, head)), 3, "scenario_action_key_conflict", reconcile: true);

            var changed = await fixture.Connection.EditScenarioAsync(new SaveSegment(id, "Изменено в редакторе", "", 0, 0), saved);
            changedFingerprint = changed.Fingerprint;
        }
        await using var restarted = new BackendHttpFixture(files);
        await restarted.StartAsync(migrate: false);
        Assert.True(JsonElement.DeepEquals(receipt, ScenarioSuccess(await RunScenarioAsync(restarted, args))));
        File.WriteAllText(path, """{"name":"Другое начало","start_frame":240,"duration_frames":60}""");
        AssertError(await RunScenarioAsync(restarted, args), 3, "scenario_action_key_conflict", reconcile: true);
        var current = await restarted.Connection.OpenAsync();
        Assert.Equal(changedFingerprint, current.Fingerprint);
        Assert.Equal("Изменено в редакторе", Assert.Single(current.Project.Segments, row => row.Id == id).Name);
        Assert.Single(current.Project.Montage.Windows, row => row.SegmentId == id);
        Assert.Equal(3, files.Files.HistoryFiles.Length);
    }

    private static string[] SegmentArgs(string form, string key, OpenedBackendProject baseline) =>
        ["backend-scenario-segment-save", "--file", form, "--client-key", key, "--sha256", baseline.Fingerprint,
            "--revision", baseline.Project.Revision.ToString(CultureInfo.InvariantCulture)];
}
