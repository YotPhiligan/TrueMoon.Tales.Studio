using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using TrueMoon.Alloy.Runtime;
using TrueMoon.Argentis;
using TrueMoon.Tales.Studio.Configuration;
using TrueMoon.Tales.Studio.Infrastructure;
using TrueMoon.Tales.Studio.Presentation.Scene;
using TrueMoon.Tales.Studio.Tests.Support;
using Xunit;

namespace TrueMoon.Tales.Studio.Tests.Integration;

public sealed class MoveQaFactAttribute : FactAttribute
{
    public MoveQaFactAttribute()
    {
        if (Environment.GetEnvironmentVariable("STUDIO_MOVE_QA_ENDPOINT") is null)
            Skip = "Requires a fresh registered WindowMenusQA fixture; never writes an author project";
    }
}

public sealed class MoveHttpIntegrationTests
{
    [MoveQaFact]
    [Trait("Category", "MoveHttpIntegration")]
    public void Move_ActualScenarioMatchesDraft_OneCommitExactHistory_AndConflictDoesNotOverwrite()
    {
        var run = Path.GetFullPath(Environment.GetEnvironmentVariable("STUDIO_MOVE_QA_RUN")!);
        if (!run.Contains(Path.Combine(".local", "qa-sessions"), StringComparison.OrdinalIgnoreCase)) throw new InvalidOperationException("Isolated QA session required");
        var endpoint = new Uri(Environment.GetEnvironmentVariable("STUDIO_MOVE_QA_ENDPOINT")!);
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(40));
        var token = deadline.Token;
        using var http = new HttpClient(new SocketsHttpHandler { UseProxy = false }) { BaseAddress = endpoint };
        const string projectRoute = "api/projects/argentis-qa";
        var initial = Wait(http.GetFromJsonAsync<JsonObject>(projectRoute, token));
        var first = initial!["timeline"]!["clips"]!.AsArray()[0]!;
        var id = first["id"]!.GetValue<string>();
        var segmentId = first["segment_id"]!.GetValue<string>();
        var bootstrap = Wait(http.GetFromJsonAsync<JsonObject>("api/bootstrap", token));
        http.DefaultRequestHeaders.Add("X-Studio-Token", bootstrap!["token"]!.GetValue<string>());
        using (var setup = Wait(Post(http, projectRoute + "/timeline", new
        {
            expected_revision = initial["revision"]!.GetValue<int>(), actor = "qa:move-setup", action = "add_clip",
            payload = new { segment_id = segmentId, block_id = first["block_id"]!.GetValue<string>(), take_id = "take-B", start_frame = 8, duration_frames = 4, source_in_frame = 0 }
        }, token))) setup.EnsureSuccessStatusCode();
        var projectRoot = Path.Combine(run, "copy", "projects", "argentis-qa");
        var projectPath = Path.Combine(projectRoot, "Project.studio");
        if (!File.Exists(projectPath)) projectPath = Path.Combine(projectRoot, "Studio.project.json");
        var beforeBytes = File.ReadAllBytes(projectPath);
        var before = JsonNode.Parse(beforeBytes)!;
        var history = Path.Combine(Path.GetDirectoryName(projectPath)!, "Production", "History");
        var historyBefore = Directory.GetFiles(history).ToHashSet();
        using var counter = new CountingHandler { InnerHandler = new SocketsHttpHandler { UseProxy = false } };
        using var api = new StudioHttpClient(new StudioOptions(endpoint, "argentis-qa", "unused"), counter);
        using var model = new SceneViewModel(api, new FakeFrames());
        model.Open(); Settle(model, token);
        Assert.Equal(id, model.Scene!.Clip.Id);
        Assert.True(model.Scene.ScenarioConsistent);
        Assert.True(model.CanBeginMove);
        using var strip = new SceneStrip(model, () => { });
        using var session = new UiSession(strip, new HeadlessSurface(), new(120, 100));
        session.Update();
        session.HandleInput(new(InputKind.PointerDown, 40, 40));
        session.HandleInput(new(InputKind.PointerMove, 135, 40));
        Assert.True(model.Moving);
        Assert.Equal(4, model.TimelineStart);
        Assert.Equal(new[] { 0, 4 }, model.PreviewClips.OrderBy(clip => clip.StartFrame).Select(clip => clip.StartFrame).ToArray());
        Assert.Equal(0, counter.Posts);
        Assert.Equal(beforeBytes, File.ReadAllBytes(projectPath));
        session.HandleInput(new(InputKind.PointerUp, 135, 40));
        session.HandleInput(new(InputKind.PointerUp, 135, 40));
        Settle(model, token);
        Assert.False(model.HasError, model.Status);
        Assert.False(model.Modified);
        Assert.Equal(1, counter.Posts);
        Assert.Equal(before["revision"]!.GetValue<int>() + 1, model.Scene.Revision);
        Assert.Equal(4, model.Scene.Clip.StartFrame);
        var afterBytes = File.ReadAllBytes(projectPath);
        var after = JsonNode.Parse(afterBytes)!;
        Assert.Equal(before["timeline"]!["undo"]!.AsArray().Count + 1, after["timeline"]!["undo"]!.AsArray().Count);
        var addedHistory = Assert.Single(Directory.GetFiles(history), path => !historyBefore.Contains(path));
        Assert.Equal(beforeBytes, File.ReadAllBytes(addedHistory));
        Assert.True(JsonNode.DeepEquals(before["blocks"], after["blocks"]));
        Assert.True(JsonNode.DeepEquals(before["assets"], after["assets"]));
        Assert.True(JsonNode.DeepEquals(before["timeline"]!["segments"], after["timeline"]!["segments"]));
        var oldItems = before["timeline"]!["scenario"]!["segments"]!.AsArray().SelectMany(segment => segment!["items"]!.AsArray()).ToDictionary(item => item!["id"]!.GetValue<string>());
        foreach (var item in after["timeline"]!["scenario"]!["segments"]!.AsArray().SelectMany(segment => segment!["items"]!.AsArray()))
            Assert.True(JsonNode.DeepEquals(oldItems[item!["id"]!.GetValue<string>()], item), "Move changed scenario data beyond ordering");
        // A cancelled move restores every draft placement and does not write.
        session.HandleInput(new(InputKind.PointerDown, 80, 40));
        session.HandleInput(new(InputKind.PointerMove, 40, 40));
        Assert.Equal(0, model.TimelineStart);
        session.HandleInput(new(InputKind.KeyDown, Key: UiKey.Escape));
        session.HandleInput(new(InputKind.PointerUp, 40, 40));
        Assert.Equal(4, model.TimelineStart);
        Assert.Equal(afterBytes, File.ReadAllBytes(projectPath));
        Assert.Equal(1, counter.Posts);
        // A different writer advances the revision between pointer-down and release.
        session.HandleInput(new(InputKind.PointerDown, 80, 40));
        session.HandleInput(new(InputKind.PointerMove, 40, 40));
        using (var edit = Wait(Post(http, projectRoute + "/timeline", new
        {
            expected_revision = model.Scene.Revision, actor = "qa:other-writer", action = "add_marker", payload = new { name = "Other writer", frame = 1 }
        }, token))) edit.EnsureSuccessStatusCode();
        var externalBytes = File.ReadAllBytes(projectPath);
        session.HandleInput(new(InputKind.PointerUp, 40, 40));
        Settle(model, token);
        Assert.True(model.Stale); Assert.True(model.Modified); Assert.True(model.HasError);
        Assert.Equal(0, model.TimelineStart);
        Assert.Equal(2, counter.Posts);
        model.Save(); model.CompleteMove();
        Assert.Equal(2, counter.Posts);
        Assert.Equal(externalBytes, File.ReadAllBytes(projectPath));
        model.Cancel(); model.Open(); Settle(model, token);
        Assert.False(model.Stale); Assert.False(model.Modified);
        Assert.Equal(4, model.TimelineStart);
        var report = new { ok = true, version = typeof(SceneViewModel).Assembly.GetName().Version!.ToString(),
            before_revision = before["revision"]!.GetValue<int>(), saved_revision = after["revision"]!.GetValue<int>(),
            reloaded_revision = model.Scene.Revision, move_posts = counter.Posts, saved_start = 4, source_unchanged = true,
            exact_history = true, cancel_unchanged = true, conflict_unchanged = true };
        File.WriteAllText(Path.Combine(run, "move-http.json"), JsonSerializer.Serialize(report));
    }

    private static T Wait<T>(Task<T> task) => task.GetAwaiter().GetResult();

    // UiSession remains on this owner thread while only HTTP/decoder tasks run elsewhere.
    private static void Settle(SceneViewModel model, CancellationToken token)
    {
        do { token.ThrowIfCancellationRequested(); model.Poll(); if (model.Idle) return; Thread.Sleep(5); } while (true);
    }

    private static async Task<HttpResponseMessage> Post(HttpClient http, string path, object value, CancellationToken token)
    {
        using var content = new StringContent(JsonSerializer.Serialize(value), Encoding.UTF8, "application/json");
        return await http.PostAsync(path, content, token);
    }

    private sealed class CountingHandler : DelegatingHandler
    {
        public int Posts { get; private set; }
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            if (request.Method == HttpMethod.Post) Posts++;
            return base.SendAsync(request, cancellationToken);
        }
    }
}
