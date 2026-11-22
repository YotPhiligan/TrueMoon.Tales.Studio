using System.Globalization;
using System.Text.Json;
using TrueMoon.Tales.Studio.Backend;
using TrueMoon.Tales.Studio.Backend.Export;
using TrueMoon.Tales.Studio.Persistence;
using TrueMoon.Tales.Studio.Tests.Support;
using Xunit;

namespace TrueMoon.Tales.Studio.Tests.Backend;

public sealed class BackendAgentBreakdownTests
{
    [Fact]
    public async Task NativeBreakdown_PreviewsAndAppendsTextActionsInOneUndoableCommit()
    {
        using var files = new BackendWriteFixture(scenario: true);
        await using var fixture = new BackendHttpFixture(files);
        var goal = BackendAgentRequestCreationTests.Configure(files);
        await fixture.StartAsync();
        var original = await fixture.Connection.OpenAsync();
        var id = await CompleteAsync(fixture, goal, original, """
            [
              {"title":"Контроль часов","description":"Эйко смотрит на циферблат","duration_frames":24,"gap_before_frames":4,"sound_notes":"Тихий ход"},
              {"title":"Реакция","description":"Она опускает руку","duration_frames":6,"gap_before_frames":6,"continuity_notes":"Часы на левой руке"}
            ]
            """);
        var before = File.ReadAllBytes(files.Files.ProjectPath);
        var task = RequestPath(files, id);
        var taskBefore = File.ReadAllBytes(task);
        var plan = await PreviewAsync(fixture, id);
        Assert.Equal(original.Fingerprint, plan.Fingerprint);
        Assert.Equal(original.Project.Revision, plan.ProjectRevision);
        Assert.Equal("ready", plan.Status);
        Assert.Equal((240, 36L, 40L, 164L), (plan.WindowFrames!.Value, plan.UsedFrames!.Value, plan.ProposedFrames, plan.RemainingFrames!.Value));
        Assert.Equal([(40L, 64L), (70L, 76L)], plan.Items.Select(item => (item.StartFrame!.Value, item.EndFrame!.Value)));
        Assert.Equal(before, File.ReadAllBytes(files.Files.ProjectPath));
        Assert.Equal(taskBefore, File.ReadAllBytes(task));
        Assert.Single(files.Files.HistoryFiles);

        var reply = Success(await ApplyAsync(fixture, plan, "--actor", "монтажёр"));
        var saved = await fixture.Connection.OpenAsync();
        Assert.Equal(original.Project.Revision + 1, saved.Project.Revision);
        Assert.Equal(saved.Fingerprint, reply.GetProperty("fingerprint").GetString());
        Assert.Equal(2, files.Files.HistoryFiles.Length);
        Assert.True(saved.CanUndoMontage);
        Assert.True(BackendAgentBreakdown.IsApplied(saved.Project, id));
        Assert.Equal(JsonSerializer.Serialize(original.Project.Montage), JsonSerializer.Serialize(saved.Project.Montage));
        Assert.Equal(JsonSerializer.Serialize(original.Project.Sources), JsonSerializer.Serialize(saved.Project.Sources));
        var segment = saved.Project.Segments.Single(segment => segment.Id == plan.SegmentId);
        var originalSegment = original.Project.Segments.Single(segment => segment.Id == plan.SegmentId);
        Assert.Equal(JsonSerializer.Serialize(originalSegment.Actions), JsonSerializer.Serialize(segment.Actions.Take(originalSegment.Actions.Length)));
        var added = segment.Actions.Skip(originalSegment.Actions.Length).ToArray();
        Assert.Equal(plan.Items.Select(row => row.Id), added.Select(action => action.Id));
        Assert.Equal("Тихий ход", added[0].SoundNotes);
        Assert.Equal("Часы на левой руке", added[1].ContinuityNotes);
        Assert.Equal([4, 6], added.Select(action => action.GapBeforeFrames));
        Assert.All(added, action => Assert.Empty(action.Sources));
        var window = saved.Project.Montage.Windows.Single(row => row.SegmentId == plan.SegmentId);
        var clips = saved.Project.Montage.Clips.Where(clip => clip.WindowId == window.Id).ToArray();
        Assert.Equal([(40, 24), (70, 6)], BackendAnimatic.PlannedShots(window, segment.Actions, clips)
            .TakeLast(2).Select(shot => (shot.Start!.Value, shot.Duration!.Value)));
        using var stored = JsonDocument.Parse(File.ReadAllBytes(files.Files.ProjectPath));
        Assert.Equal("монтажёр", stored.RootElement.GetProperty("last_write").GetProperty("actor").GetString());

        var undone = await fixture.Connection.EditScenarioAsync(new UndoMontage(), saved);
        Assert.Equal(originalSegment.Actions.Select(row => row.Id), undone.Project.Segments.Single(row => row.Id == plan.SegmentId).Actions.Select(row => row.Id));
        Assert.True(undone.CanRedoMontage);
        var used = await PreviewAsync(fixture, id);
        Assert.Equal("applied", used.Status);
        Error(await ApplyAsync(fixture, used), 3, 409, "agent_breakdown_already_applied");
        Assert.Equal(undone.Fingerprint, (await fixture.Connection.OpenAsync()).Fingerprint);
        var redone = await fixture.Connection.EditScenarioAsync(new RedoMontage(), undone);
        Assert.Equal(segment.Actions.Select(row => row.Id), redone.Project.Segments.Single(row => row.Id == plan.SegmentId).Actions.Select(row => row.Id));
        Assert.Equal(taskBefore, File.ReadAllBytes(task));
        Assert.False(Directory.Exists(Path.Combine(files.Files.ProjectDirectory, "Production", "Jobs")));
    }

    [Fact]
    public async Task NativeBreakdown_RejectsStaleBaselineAndDuplicateAfterRestart()
    {
        using var files = new BackendWriteFixture(scenario: true);
        var goal = BackendAgentRequestCreationTests.Configure(files);
        string id;
        string appliedSha;
        await using (var fixture = new BackendHttpFixture(files))
        {
            await fixture.StartAsync();
            var head = await fixture.Connection.OpenAsync();
            id = await CompleteAsync(fixture, goal, head);
            var stale = await PreviewAsync(fixture, id);
            var edited = await fixture.Connection.EditScenarioAsync(new SaveMontageMarker(null, "Метка", 24), head);
            Error(await ApplyAsync(fixture, stale), 3, 409, "revision_conflict");
            var current = await PreviewAsync(fixture, id);
            Error(await ApplyAsync(fixture, current with { Fingerprint = new string('A', 64) }), 3, 409, "revision_conflict");
            Assert.Equal(edited.Fingerprint, (await fixture.Connection.OpenAsync()).Fingerprint);
            Assert.Equal(2, files.Files.HistoryFiles.Length);
            var replies = await Task.WhenAll(ApplyAsync(fixture, current), ApplyAsync(fixture, current));
            var committed = Success(Assert.Single(replies, reply => reply.Code == 0));
            Error(Assert.Single(replies, reply => reply.Code != 0), 3, 409, "revision_conflict");
            appliedSha = committed.GetProperty("fingerprint").GetString()!;
        }
        await using (var restarted = new BackendHttpFixture(files))
        {
            await restarted.StartAsync(migrate: false);
            var plan = await PreviewAsync(restarted, id);
            Assert.Equal("applied", plan.Status);
            Assert.Equal(appliedSha, plan.Fingerprint);
            Error(await ApplyAsync(restarted, plan), 3, 409, "agent_breakdown_already_applied");
            Assert.Equal(appliedSha, (await restarted.Connection.OpenAsync()).Fingerprint);
            Assert.Equal(3, files.Files.HistoryFiles.Length);
        }
    }

    [Theory]
    [InlineData("insufficient_frames")]
    [InlineData("existing_timing_unresolved")]
    [InlineData("transition_conflict")]
    public async Task NativeBreakdown_BlocksUnplaceableProposalWithoutWrites(string reason)
    {
        using var files = new BackendWriteFixture(scenario: true);
        await using var fixture = new BackendHttpFixture(files);
        var goal = BackendAgentRequestCreationTests.Configure(files);
        await fixture.StartAsync();
        var head = await fixture.Connection.OpenAsync();
        var proposal = reason == "insufficient_frames" ? """[{"title":"План","description":"Сцена","duration_frames":205}]""" : null;
        var id = await CompleteAsync(fixture, goal, head, proposal);
        if (reason == "existing_timing_unresolved")
            head = await fixture.Connection.EditScenarioAsync(new SaveAction("segment-main", null, "Неизвестно время", "", null), head);
        if (reason == "transition_conflict")
        {
            var clips = head.Project.Montage.Clips.OrderBy(clip => clip.StartFrame).ToArray();
            var gap = clips[1].StartFrame - clips[0].StartFrame - clips[0].DurationFrames;
            head = await fixture.Connection.EditScenarioAsync(new SetBlackTransition(clips[0].Id, clips[1].Id, 4, gap, 4), head);
        }
        var history = files.Files.HistoryFiles.Length;
        var task = File.ReadAllBytes(RequestPath(files, id));
        var plan = await PreviewAsync(fixture, id);
        Assert.Equal(head.Fingerprint, plan.Fingerprint);
        Assert.Equal("blocked", plan.Status);
        Assert.Equal([reason], plan.Issues.AsEnumerable());
        if (reason == "insufficient_frames") Assert.Equal(-1L, plan.RemainingFrames);
        if (reason == "existing_timing_unresolved") Assert.Null(plan.Items[0].StartFrame);
        Error(await ApplyAsync(fixture, plan), 1, 422, "agent_breakdown_blocked");
        Assert.Equal(head.Fingerprint, (await fixture.Connection.OpenAsync()).Fingerprint);
        Assert.Equal(history, files.Files.HistoryFiles.Length);
        Assert.Equal(task, File.ReadAllBytes(RequestPath(files, id)));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task NativeBreakdown_StartsAtEmptyWindowOrAfterSavedDetachedClip(bool keepClip)
    {
        using var files = new BackendWriteFixture(scenario: true);
        var goal = BackendAgentRequestCreationTests.Configure(files);
        var store = files.Files.Store();
        var migrated = await store.MigrateAsync(await store.OpenAsync(), 11, "fixture");
        var project = migrated.Project;
        var clip = project.Montage.Clips.Single(row => row.Id == "clip-main");
        await store.SaveAsync(migrated, project with
        {
            Segments = project.Segments.Select(segment => segment.Id == "segment-main" ? segment with { Actions = [] } : segment).ToArray(),
            Montage = project.Montage with
            {
                Clips = project.Montage.Clips.Where(row => keepClip || row.Id != clip.Id)
                    .Select(row => row.Id == clip.Id ? row with { ActionId = null } : row).ToArray()
            }
        }, project.Revision, "fixture");
        await using var fixture = new BackendHttpFixture(files);
        await fixture.StartAsync(migrate: false);
        var head = await fixture.Connection.OpenAsync();
        var id = await CompleteAsync(fixture, goal, head);
        var plan = await PreviewAsync(fixture, id);
        Assert.Equal(keepClip ? clip.StartFrame + clip.DurationFrames : 0, plan.Items[0].StartFrame);
        Success(await ApplyAsync(fixture, plan));
        var saved = await fixture.Connection.OpenAsync();
        var action = Assert.Single(saved.Project.Segments.Single(row => row.Id == "segment-main").Actions);
        Assert.Equal(keepClip ? plan.Items[0].StartFrame : null, action.AnchorFrame);
        if (keepClip) Assert.Null(saved.Project.Montage.Clips.Single(row => row.Id == clip.Id).ActionId);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task NativeBreakdown_HandlesExistingWebProposalAndHonorsLegacyAppliedMarker(bool applied)
    {
        using var files = new BackendWriteFixture(scenario: true);
        BackendAgentRequestCreationTests.Configure(files);
        const string id = "req-0123456789abcdef";
        var path = RequestPath(files, id);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, """
            {"id":"req-0123456789abcdef","project_id":"project-synthetic","state":"completed","mode":"propose","revision":3,
             "updated_at":"2026-11-21T07:00:00+00:00","scope":{"segment_id":"segment-main","intent":"breakdown_segment"},
             "context":{"context_version":2},"proposal":[{"title":"Архивный план","description":"Действие","duration_frames":12,"gap_before_frames":0}]}
            """);
        var bytes = File.ReadAllBytes(path);
        var store = files.Files.Store();
        var migrated = await store.MigrateAsync(await store.OpenAsync(), 11, "fixture");
        if (applied)
        {
            var project = migrated.Project;
            var source = project.Sources[0];
            var tagged = new Dictionary<string, JsonElement>(source.Properties) { ["scenario_breakdown_id"] = JsonSerializer.SerializeToElement(id) };
            await store.SaveAsync(migrated, project with { Sources = [source with { Properties = tagged }, .. project.Sources.Skip(1)] },
                project.Revision, "fixture");
        }
        await using var fixture = new BackendHttpFixture(files);
        await fixture.StartAsync(migrate: false);
        var plan = await PreviewAsync(fixture, id);
        Assert.Equal(applied ? "applied" : "ready", plan.Status);
        if (applied) Error(await ApplyAsync(fixture, plan), 3, 409, "agent_breakdown_already_applied");
        else
        {
            Success(await ApplyAsync(fixture, plan));
            Assert.Equal("Архивный план", (await fixture.Connection.OpenAsync()).Project.Segments
                .Single(row => row.Id == "segment-main").Actions[^1].Title);
        }
        Assert.Equal(2, files.Files.HistoryFiles.Length);
        Assert.Equal(bytes, File.ReadAllBytes(path));
    }

    [Fact]
    public async Task NativeBreakdown_RefusesMissingOrUnfinishedRequests()
    {
        using var files = new BackendWriteFixture(scenario: true);
        await using var fixture = new BackendHttpFixture(files);
        var goal = BackendAgentRequestCreationTests.Configure(files);
        await fixture.StartAsync();
        var head = await fixture.Connection.OpenAsync();
        const string preview = "backend-agent-request-breakdown-preview";
        Error(await RunAsync(fixture, preview, "--id", "req-0000000000000001"), 1, 404, "agent_request_not_found");
        var scope = Path.Combine(files.Files.Root, "pending-scope.json");
        File.WriteAllText(scope, """{"intent":"breakdown_segment","segment_id":"segment-main"}""");
        var id = await BackendAgentRequestUpdateTests.CreateAsync(fixture, goal, head, "--scope-file", scope);
        var task = File.ReadAllBytes(RequestPath(files, id));
        Error(await RunAsync(fixture, preview, "--id", id), 3, 409, "agent_breakdown_not_ready");
        Error(await RunAsync(fixture, "backend-agent-request-breakdown-apply", "--id", id,
            "--revision", head.Project.Revision.ToString(CultureInfo.InvariantCulture), "--sha256", head.Fingerprint),
            3, 409, "agent_breakdown_not_ready");
        Assert.Equal(head.Fingerprint, (await fixture.Connection.OpenAsync()).Fingerprint);
        Assert.Equal(task, File.ReadAllBytes(RequestPath(files, id)));
        Assert.Single(files.Files.HistoryFiles);
    }

    internal static async Task<string> CompleteAsync(BackendHttpFixture fixture, string goal, OpenedBackendProject head, string? proposal = null)
    {
        var root = fixture.Files.Files.Root;
        var scope = Path.Combine(root, "breakdown-scope.json");
        File.WriteAllText(scope, """{"intent":"breakdown_segment","segment_id":"segment-main"}""");
        var id = await BackendAgentRequestUpdateTests.CreateAsync(fixture, goal, head, "--scope-file", scope);
        Success(await RunAsync(fixture, "backend-agent-request-claim", "--id", id, "--revision", "1", "--actor", "agent:qa"));
        var form = Path.Combine(root, "breakdown-proposal.json");
        File.WriteAllText(form, proposal ?? """[{"title":"План","description":"Действие","duration_frames":12}]""");
        Success(await RunAsync(fixture, "backend-agent-request-finish", "--id", id, "--revision", "2", "--actor", "agent:qa",
            "--proposal-file", form, "--result-file", goal));
        return id;
    }

    private static async Task<AgentBreakdownPlan> PreviewAsync(BackendHttpFixture fixture, string id) =>
        BackendAgentBreakdown.Decode(Success(await RunAsync(fixture, "backend-agent-request-breakdown-preview", "--id", id)), id);

    private static Task<(int Code, string Output, string Error)> ApplyAsync(BackendHttpFixture fixture, AgentBreakdownPlan plan, params string[] options) =>
        RunAsync(fixture, ["backend-agent-request-breakdown-apply", "--id", plan.RequestId, "--revision",
            plan.ProjectRevision.ToString(CultureInfo.InvariantCulture), "--sha256", plan.Fingerprint, .. options]);

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

    private static void Error((int Code, string Output, string Error) reply, int code, int status, string error)
    {
        Assert.Equal(code, reply.Code);
        Assert.Empty(reply.Output);
        using var json = JsonDocument.Parse(reply.Error);
        Assert.Equal(error, json.RootElement.GetProperty("error").GetString());
        Assert.Equal(status, json.RootElement.GetProperty("status").GetInt32());
        Assert.False(json.RootElement.GetProperty("automatic_retry").GetBoolean());
    }

    private static string RequestPath(BackendWriteFixture files, string id) =>
        Path.Combine(files.Files.ProjectDirectory, "Production", "AgentRequests", id + ".json");
}
