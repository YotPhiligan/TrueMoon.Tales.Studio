using System.Globalization;
using System.Text;
using System.Text.Json;
using Microsoft.AspNetCore.Http;
using TrueMoon.Argentis;
using TrueMoon.Dependencies;
using TrueMoon.Tales.Studio.Backend;
using TrueMoon.Tales.Studio.Backend.Http;
using TrueMoon.Tales.Studio.Persistence;
using TrueMoon.Tales.Studio.Presentation.Scene;
using TrueMoon.Tales.Studio.Tests.Support;
using Xunit;

namespace TrueMoon.Tales.Studio.Tests.Backend;

public sealed partial class BackendNativeCliTests
{
    private static readonly ScenarioClipDraft Placement = new("segment-main", "clip-main", "source-a", "take-a", 36, 12, 60);

    [BackendMediaFact(requirePython: false)]
    public async Task NativeClipPlacement_CommitsOnceAndAllowsRoutedEditorUndo()
    {
        using var files = new BackendWriteFixture(scenario: true);
        await using var fixture = new BackendMediaFixture(files);
        await fixture.StartAsync();
        var backend = fixture.Backend;
        const string title = "Новый монтажный план";
        var actionHead = await backend.Connection.EditScenarioAsync(new SaveAction(Placement.SegmentId, null, title, "", 24), fixture.Baseline);
        var action = Assert.Single(actionHead.Project.Segments.Single(row => row.Id == Placement.SegmentId).Actions,
            row => row.Title == title);
        var head = await backend.Connection.EditScenarioAsync(new LinkSource(Placement.SegmentId, action.Id, Placement.SourceId, null), actionHead);
        var before = File.ReadAllBytes(files.Files.ProjectPath);
        var historyCount = files.Files.HistoryFiles.Length;
        var placement = Placement with { ActionId = action.Id };
        var form = ScenarioInput(files, "placement.json", JsonSerializer.Serialize(placement, AgentRequestDraft.Json));
        var args = ClipPlacementArgs(form, "clip-placement-0001", head);
        Assert.Throws<ProjectStoreBusyException>(() => ProjectWriterLease.Acquire(BackendProjectAccess.Read(files.Options)));
        var results = await Task.WhenAll(RunScenarioAsync(backend, args), RunScenarioAsync(backend, args));
        var receipt = ScenarioSuccess(results[0]);
        Assert.True(JsonElement.DeepEquals(receipt, ScenarioSuccess(results[1])));
        var clipId = receipt.GetProperty("clip_id").GetString()!;
        Assert.Equal(head.Project.Revision + 1, receipt.GetProperty("project_revision").GetInt64());
        Assert.Equal(action.Id, receipt.GetProperty("action_id").GetString());
        Assert.Equal(placement.SegmentId, receipt.GetProperty("segment_id").GetString());
        Assert.Equal(placement.SourceId, receipt.GetProperty("source_id").GetString());
        Assert.Equal(placement.TakeId, receipt.GetProperty("take_id").GetString());

        var saved = await backend.Connection.OpenAsync();
        var clip = Assert.Single(saved.Project.Montage.Clips, row => row.Id == clipId);
        Assert.Equal(saved.Project.Revision, receipt.GetProperty("project_revision").GetInt64());
        Assert.Equal(head.Project.Montage.Clips.Length + 1, saved.Project.Montage.Clips.Length);
        Assert.Equal(head.Project.Montage.Clips, saved.Project.Montage.Clips.Where(row => row.Id != clipId));
        Assert.Equal(head.Project.Montage.Windows, saved.Project.Montage.Windows);
        Assert.Equal(head.Project.Montage.Windows.Single(row => row.SegmentId == placement.SegmentId).Id, clip.WindowId);
        Assert.Equal((action.Id, placement.SourceId, placement.TakeId), (clip.ActionId, clip.SourceId, clip.TakeId));
        Assert.Equal((36, 12, 60d, (double?)72d), (clip.StartFrame, clip.DurationFrames, clip.SourceInFrame, clip.SourceOutFrame));
        Assert.Equal(24, saved.Project.Segments.Single(row => row.Id == placement.SegmentId).Actions.Single(row => row.Id == action.Id).PlannedDurationFrames);
        Assert.Equal(historyCount + 1, files.Files.HistoryFiles.Length);
        Assert.Contains(files.Files.HistoryFiles, path => File.ReadAllBytes(path).SequenceEqual(before));

        var model = backend.App.Services.Resolve<SceneViewModel>()!;
        using (var host = new RasterWindow<SceneView>(backend.App.Services.Resolve<IFactory<SceneView>>()!.Create()!,
            "native-clip-placement", 1440, 1120))
        {
            host.PumpUntil(() => model.Loaded && model.Idle);
            var select = Assert.Single(RasterWindow<SceneView>.Walk(host.Window.Navigation).OfType<Button>(),
                button => button.Value?.EndsWith(title, StringComparison.Ordinal) == true);
            host.Click(select);
            host.PumpUntil(() => model.Idle && model.Scene?.Clip.Id == clipId || model.HasError);
            Assert.False(model.HasError, model.Status);
            Assert.Equal(clipId, model.Scene!.Clip.Id);
            Assert.Equal(saved.Fingerprint, model.Scene.Fingerprint);
            Assert.Equal((36, 12), (model.TimelineStart, model.TimelineDuration));
            Assert.NotNull(model.Pixels);

            var undo = Assert.Single(RasterWindow<SceneView>.Walk(host.Window.Inspector).OfType<Button>(), button => button.Value == "Undo");
            Assert.True(undo.IsEnabled);
            host.Click(undo);
            host.PumpUntil(() => model.Idle && model.SavedRevision == saved.Project.Revision + 1 || model.HasError);
            Assert.False(model.HasError, model.Status);
            Assert.True(model.CanRedoMontage);
            Assert.DoesNotContain(model.Scene!.Choices, row => row.Clip.Id == clipId);
        }

        var undone = await backend.Connection.OpenAsync();
        Assert.Equal(head.Project.Montage.Clips, undone.Project.Montage.Clips);
        Assert.Equal(historyCount + 2, files.Files.HistoryFiles.Length);
        Assert.True(JsonElement.DeepEquals(receipt, ScenarioSuccess(await RunScenarioAsync(backend, args))));
        Assert.Equal(undone.Fingerprint, (await backend.Connection.OpenAsync()).Fingerprint);
        Assert.Equal(historyCount + 2, files.Files.HistoryFiles.Length);
    }

    [BackendMediaFact(requirePython: false)]
    public async Task NativeClipPlacement_RejectsStaleBaselineAndInvalidPlacementWithoutWriting()
    {
        using var files = new BackendWriteFixture(scenario: true);
        await using var fixture = new BackendMediaFixture(files);
        await fixture.StartAsync();
        var backend = fixture.Backend;
        var head = fixture.Baseline;
        const string key = "clip-refusal-00001";
        var form = ScenarioInput(files, "placement.json", JsonSerializer.Serialize(Placement, AgentRequestDraft.Json));
        var stale = head with { Project = head.Project with { Revision = head.Project.Revision - 1 } };
        AssertError(await RunScenarioAsync(backend, ClipPlacementArgs(form, key, stale)), 3, "revision_conflict", reconcile: true);
        AssertError(await RunScenarioAsync(backend, ClipPlacementArgs(form, key, head with { Fingerprint = new string('0', 64) })),
            3, "revision_conflict", reconcile: true);
        foreach (var (placement, error) in new[]
        {
            (Placement with { SourceId = "source-b", TakeId = "take-b" }, "invalid_scenario_edit"),
            (Placement with { TakeId = "take-b" }, "invalid_scenario_edit"),
            (Placement with { StartFrame = -1 }, "scenario_window_conflict"),
            (Placement with { StartFrame = 229 }, "scenario_window_conflict"),
            (Placement with { DurationFrames = 0 }, "scenario_window_conflict"),
            (Placement with { StartFrame = 35 }, "scenario_clip_overlap"),
            (Placement with { SourceInFrame = 61 }, "scenario_source_range")
        })
        {
            File.WriteAllText(form, JsonSerializer.Serialize(placement, AgentRequestDraft.Json));
            AssertError(await RunScenarioAsync(backend, ClipPlacementArgs(form, key, head)), 1, error, reconcile: true);
        }
        File.WriteAllText(form, """{"segment_id":"segment-main","action_id":"clip-main","source_id":"source-a","take_id":"take-a","start_frame":36,"duration_frames":12}""");
        AssertError(await RunScenarioAsync(backend, ClipPlacementArgs(form, key, head)), 2, "invalid_arguments", reconcile: false);
        fixture.AssertUnchanged();
        File.WriteAllText(form, JsonSerializer.Serialize(Placement, AgentRequestDraft.Json));
        Assert.True(ScenarioSuccess(await RunScenarioAsync(backend, ClipPlacementArgs(form, key, head))).GetProperty("changed").GetBoolean());
        Assert.Equal(2, files.Files.HistoryFiles.Length);
    }

    [BackendMediaFact(requirePython: false)]
    public async Task NativeClipPlacement_ReplaysLostReceiptAfterRestartAndRejectsChangedRequest()
    {
        using var files = new BackendWriteFixture(scenario: true);
        var form = ScenarioInput(files, "placement.json", JsonSerializer.Serialize(Placement, AgentRequestDraft.Json));
        string[] args;
        JsonElement receipt = default;
        string fingerprint;
        await using (var fixture = new BackendMediaFixture(files))
        {
            await fixture.StartAsync();
            args = ClipPlacementArgs(form, "clip-lost-reply-0001", fixture.Baseline);
            var postCount = 0;
            using var transport = fixture.Backend.Client();
            await WithStubAsync(async context =>
            {
                Assert.Equal(BackendHttpServer.ScenarioClipRoute, context.Request.Path.Value);
                Assert.Equal(HttpMethods.Post, context.Request.Method);
                Interlocked.Increment(ref postCount);
                using var request = await JsonDocument.ParseAsync(context.Request.Body, cancellationToken: context.RequestAborted);
                using var body = new StringContent(request.RootElement.GetRawText(), Encoding.UTF8, "application/json");
                using var response = await transport.PostAsync(BackendHttpServer.ScenarioClipRoute, body, context.RequestAborted);
                response.EnsureSuccessStatusCode();
                using var committed = JsonDocument.Parse(await response.Content.ReadAsByteArrayAsync(context.RequestAborted));
                receipt = committed.RootElement.Clone();
                context.Abort();
            }, async address =>
            {
                AssertError(await RunAsync(address, Fingerprint, args), 1, "backend_unavailable", reconcile: true);
                Assert.Equal(1, Volatile.Read(ref postCount));
            });
            var saved = await fixture.Backend.Connection.OpenAsync();
            fingerprint = saved.Fingerprint;
            Assert.Equal(fixture.Baseline.Project.Revision + 1, saved.Project.Revision);
            Assert.Single(saved.Project.Montage.Clips, row => row.Id == receipt.GetProperty("clip_id").GetString());
            Assert.Equal(2, files.Files.HistoryFiles.Length);
        }

        await using var restarted = new BackendHttpFixture(files);
        await restarted.StartAsync(migrate: false);
        Assert.True(JsonElement.DeepEquals(receipt, ScenarioSuccess(await RunScenarioAsync(restarted, args))));
        File.WriteAllText(form, JsonSerializer.Serialize(Placement with { StartFrame = 48 }, AgentRequestDraft.Json));
        AssertError(await RunScenarioAsync(restarted, args), 3, "scenario_action_key_conflict", reconcile: true);
        var current = await restarted.Connection.OpenAsync();
        Assert.Equal(fingerprint, current.Fingerprint);
        Assert.Single(current.Project.Montage.Clips, row => row.Id == receipt.GetProperty("clip_id").GetString());
        Assert.Equal(2, files.Files.HistoryFiles.Length);
    }

    private static string[] ClipPlacementArgs(string form, string key, OpenedBackendProject baseline) =>
        ["backend-scenario-clip-place", "--file", form, "--client-key", key, "--sha256", baseline.Fingerprint,
            "--revision", baseline.Project.Revision.ToString(CultureInfo.InvariantCulture)];
}
