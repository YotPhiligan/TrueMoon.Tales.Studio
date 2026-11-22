using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using TrueMoon.Tales.Studio.Backend;
using TrueMoon.Tales.Studio.Backend.Http;
using TrueMoon.Tales.Studio.Persistence;
using TrueMoon.Tales.Studio.Tests.Persistence;
using TrueMoon.Tales.Studio.Tests.Support;
using Xunit;

namespace TrueMoon.Tales.Studio.Tests.Backend;

public sealed class BackendAgentContextTests
{
    [Fact]
    public async Task NativeContext_ReadsSavedProjectAndActionThroughHttpAndEnerit()
    {
        await using var fixture = new BackendHttpFixture();
        var baseline = await SeedAsync(fixture);
        await fixture.StartAsync(migrate: false);
        var files = fixture.Files.Files;
        var before = ReadFiles(files.Root);

        var overview = Success(await RunAsync(fixture));
        var project = overview.GetProperty("context").GetProperty("project");
        Assert.Equal(baseline.Project.Id, project.GetProperty("id").GetString());
        Assert.Equal(baseline.Project.Revision, project.GetProperty("revision").GetInt64());
        Assert.Equal("Монтаж: часы и реакция", project.GetProperty("properties").GetProperty("brief").GetProperty("summary").GetString());
        Assert.Equal("Музыкальная опора", project.GetProperty("montage").GetProperty("properties")
            .GetProperty("markers")[0].GetProperty("name").GetString());
        Assert.Equal(JsonValueKind.Null, overview.GetProperty("context").GetProperty("block").ValueKind);

        var selected = Success(await RunAsync(fixture, "clip-main"));
        var context = selected.GetProperty("context");
        Assert.Equal(3, context.GetProperty("context_version").GetInt32());
        Assert.Equal(2, context.GetProperty("schema_version").GetInt32());
        Assert.Equal(baseline.Fingerprint, context.GetProperty("fingerprint").GetString());
        Assert.Equal(Hash(File.ReadAllBytes(files.ProjectPath)), context.GetProperty("fingerprint").GetString());
        Assert.False(context.GetProperty("automatic_dispatch").GetBoolean());
        Assert.Equal("clip-main", context.GetProperty("selector").GetString());
        var block = context.GetProperty("block");
        Assert.Equal("action", block.GetProperty("kind").GetString());
        Assert.Equal("clip-main", block.GetProperty("id").GetString());
        Assert.Equal(["source-b", "source-a"], Strings(block.GetProperty("source_ids")));
        var action = Assert.Single(block.GetProperty("actions").EnumerateArray());
        Assert.Equal("segment-main", action.GetProperty("segment_id").GetString());
        Assert.Equal("new-text-action", action.GetProperty("previous_action_id").GetString());
        Assert.Equal(JsonValueKind.Null, action.GetProperty("next_action_id").ValueKind);
        var placements = block.GetProperty("placements");
        Assert.Equal(["clip-main", "clip-reaction", "clip-main-tail"], placements.EnumerateArray().Select(row => row.GetProperty("clip_id").GetString()));
        Assert.Equal("clip-extra", placements[2].GetProperty("next_clip_id").GetString());
        Assert.Equal("clip-main", placements[1].GetProperty("previous_clip_id").GetString());
        var continuation = Assert.Single(block.GetProperty("continuations").EnumerateArray());
        Assert.Equal("source-b", continuation.GetProperty("source_id").GetString());
        Assert.Equal("take-a", continuation.GetProperty("take_id").GetString());
        Assert.Equal(["source-a"], Strings(continuation.GetProperty("origin_source_ids")));
        var sources = context.GetProperty("project").GetProperty("sources");
        var original = PersistenceFixture.ById(sources, "source-a");
        Assert.Equal("Оригинальный промпт", original.GetProperty("properties").GetProperty("prompt").GetString());
        Assert.Equal("accepted", PersistenceFixture.ById(original.GetProperty("takes"), "take-a")
            .GetProperty("properties").GetProperty("review_state").GetString());

        var assets = context.GetProperty("assets");
        var available = PersistenceFixture.ById(assets, "video-b");
        Assert.Equal("available", available.GetProperty("status").GetString());
        Assert.Equal(Path.Combine(files.ProjectDirectory, "video", "B.mp4"), available.GetProperty("resolved_path").GetString());
        var denied = PersistenceFixture.ById(assets, "video-a");
        Assert.Equal("access_denied", denied.GetProperty("status").GetString());
        Assert.Equal(JsonValueKind.Null, denied.GetProperty("resolved_path").ValueKind);
        Assert.Equal("missing", PersistenceFixture.ById(assets, "missing").GetProperty("status").GetString());

        var documents = context.GetProperty("documents").EnumerateArray().ToArray();
        var card = Assert.Single(documents, row => row.GetProperty("reference").GetString() == "@project/shot.md");
        Assert.Equal("# Опора часов", card.GetProperty("text").GetString());
        Assert.Equal(Hash(File.ReadAllBytes(Path.Combine(files.ProjectDirectory, "shot.md"))), card.GetProperty("sha256").GetString());
        var external = Assert.Single(documents, row => row.GetProperty("reference").GetString() == "@projects/other.md");
        Assert.Equal("access_denied", external.GetProperty("status").GetString());
        Assert.Equal(JsonValueKind.Null, external.GetProperty("text").ValueKind);
        var plan = Assert.Single(documents, row => row.GetProperty("role").GetString() == "project_plan");
        Assert.True(plan.GetProperty("truncated").GetBoolean());
        Assert.Equal(4000, plan.GetProperty("text").GetString()!.Length);
        Assert.Equal(JsonValueKind.Null, plan.GetProperty("sha256").ValueKind);

        Assert.Equal(selected.GetProperty("snapshot_sha256").GetString(),
            Success(await RunAsync(fixture, "clip-main")).GetProperty("snapshot_sha256").GetString());
        Assert.Equal(before, ReadFiles(files.Root));
    }

    [Fact]
    public async Task NativeContext_ResolvesSourceLabelsAndActionTitles_RejectsAmbiguousAndUnknownSelectors()
    {
        await using var fixture = new BackendHttpFixture();
        await SeedAsync(fixture);
        await fixture.StartAsync(migrate: false);
        var before = ReadFiles(fixture.Files.Files.Root);
        var shared = Success(await RunAsync(fixture, "B")).GetProperty("context").GetProperty("block");
        Assert.Equal("source", shared.GetProperty("kind").GetString());
        Assert.Equal("source-b", shared.GetProperty("id").GetString());
        Assert.Equal(["clip-extra", "clip-main"], shared.GetProperty("actions").EnumerateArray().Select(row => row.GetProperty("action_id").GetString()));
        Assert.Equal(["clip-reaction", "clip-extra"], shared.GetProperty("placements").EnumerateArray().Select(row => row.GetProperty("clip_id").GetString()));

        var unplaced = Success(await RunAsync(fixture, "Пока только текст")).GetProperty("context").GetProperty("block");
        Assert.Equal("new-text-action", unplaced.GetProperty("id").GetString());
        Assert.Empty(unplaced.GetProperty("source_ids").EnumerateArray());
        Assert.Empty(unplaced.GetProperty("placements").EnumerateArray());
        Assert.Equal("clip-main", unplaced.GetProperty("actions")[0].GetProperty("next_action_id").GetString());

        Error(await RunAsync(fixture, "Повтор"), 3, 409, "agent_context_block_ambiguous");
        Error(await RunAsync(fixture, "b"), 1, 404, "agent_context_block_missing");
        using var anonymous = fixture.Client(authenticated: false);
        using var request = new StringContent("{\"block\":\"B\"}", Encoding.UTF8, "application/json");
        using var response = await anonymous.PostAsync(BackendHttpServer.AgentContextRoute, request);
        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
        Assert.Equal(before, ReadFiles(fixture.Files.Files.Root));
    }

    [Fact]
    public async Task AgentContext_ReadsWithoutOwningWriter_AndRequiresExplicitMigration()
    {
        using var fixture = new BackendWriteFixture();
        ConfigureMedia(fixture);
        using var service = fixture.Service();
        var before = ReadFiles(fixture.Files.Root);
        var legacy = await service.ReadAgentContextAsync(fixture.Options.SessionKey, "");
        Assert.Equal("migration_required", legacy.ErrorCode);
        Assert.Empty(legacy.ContextJson);
        Assert.Equal(before, ReadFiles(fixture.Files.Root));

        var store = fixture.Files.Store();
        var migrated = await store.MigrateAsync(await store.OpenAsync(), 11, "context-fixture");
        using var owner = ProjectWriterLease.Acquire(BackendProjectAccess.Read(fixture.Options));
        var current = await service.ReadAgentContextAsync(fixture.Options.SessionKey, "A");
        Assert.Empty(current.ErrorCode);
        using var json = JsonDocument.Parse(current.ContextJson);
        Assert.Equal(migrated.Fingerprint, json.RootElement.GetProperty("fingerprint").GetString());
        Assert.Equal("source-a", json.RootElement.GetProperty("block").GetProperty("id").GetString());
        Assert.Equal(migrated.Fingerprint, (await store.OpenAsync()).Fingerprint);
        Assert.Single(fixture.Files.HistoryFiles);
    }

    private static async Task<ProjectFileDocument> SeedAsync(BackendHttpFixture fixture)
    {
        var files = fixture.Files.Files;
        File.WriteAllBytes(files.ProjectPath, files.InitialBytes);
        ConfigureMedia(fixture.Files);
        Directory.CreateDirectory(Path.Combine(files.ProjectDirectory, "video"));
        File.WriteAllText(Path.Combine(files.ProjectDirectory, "video", "B.mp4"), "metadata only");
        File.WriteAllText(Path.Combine(files.ProjectDirectory, "shot.md"), "# Опора часов", Encoding.UTF8);
        File.WriteAllText(Path.Combine(files.ProjectDirectory, "Plan.md"), new string('Я', 4500));
        File.WriteAllText(Path.Combine(files.Root, "other.md"), "another project");

        var store = files.Store();
        var migrated = await store.MigrateAsync(await store.OpenAsync(), 11, "context-fixture");
        var draft = PersistenceFixture.Expanded(migrated.Project);
        using var assetJson = JsonDocument.Parse("""
            {"video-a":{"path":"Z:/media-never-opened/A.mp4"},"video-b":{"path":"@videos/Fixtures/B.mp4"},
             "missing":{"path":"@project/video/missing.mp4"}}
            """);
        var projectProperties = new Dictionary<string, JsonElement>(draft.Properties)
        {
            ["brief"] = JsonSerializer.SerializeToElement(new { summary = "Монтаж: часы и реакция" }),
            ["assets"] = assetJson.RootElement
        };
        var sources = draft.Sources.Select(source => source with
        {
            Properties = new Dictionary<string, JsonElement>(source.Properties)
            {
                ["card_path"] = JsonSerializer.SerializeToElement(source.Id == "source-a" ? "@project/shot.md" : "@projects/other.md"),
                ["context_take_id"] = JsonSerializer.SerializeToElement(source.Id == "source-b" ? "take-a" : null)
            }
        }).ToArray();
        var segments = draft.Segments.Select(segment => segment with
        {
            Actions = segment.Actions.Select(action => action.Id == "new-text-action" ? action : action with { Title = "Повтор" }).ToArray()
        }).ToArray();
        return await store.SaveAsync(migrated, draft with { Sources = sources, Segments = segments, Properties = projectProperties },
            migrated.Project.Revision, "context-fixture");
    }

    private static void ConfigureMedia(BackendWriteFixture fixture)
    {
        var configPath = fixture.Options.ConfigPath;
        var config = JsonNode.Parse(File.ReadAllText(configPath))!.AsObject();
        config.Remove("legacy_root");
        config["mounts"] = new JsonObject { ["videos"] = fixture.Files.ProjectDirectory };
        config["path_aliases"] = new JsonArray(new JsonObject { ["from"] = "@videos/Fixtures", ["to"] = "@project/video" });
        File.WriteAllText(configPath, config.ToJsonString());
    }

    private static async Task<(int Code, string Output, string Error)> RunAsync(BackendHttpFixture fixture, string? selector = null)
    {
        string[] args = selector is null ? ["backend-agent-context"] : ["backend-agent-context", "--block", selector];
        using var cli = BackendCliProcess.Native(fixture.Http.Address, fixture.Http.Token, args);
        return await cli.WaitAsync();
    }

    private static JsonElement Success((int Code, string Output, string Error) reply)
    {
        Assert.True(reply.Code == 0, reply.Error);
        Assert.Empty(reply.Error);
        using var json = JsonDocument.Parse(reply.Output);
        var root = json.RootElement;
        Assert.Equal(1, root.GetProperty("api_version").GetInt32());
        Assert.Equal(Hash(Encoding.UTF8.GetBytes(root.GetProperty("context").GetRawText())), root.GetProperty("snapshot_sha256").GetString());
        return root.Clone();
    }

    private static void Error((int Code, string Output, string Error) reply, int code, int status, string error)
    {
        Assert.Equal(code, reply.Code);
        Assert.Empty(reply.Output);
        using var json = JsonDocument.Parse(reply.Error);
        Assert.Equal(error, json.RootElement.GetProperty("error").GetString());
        Assert.Equal(status, json.RootElement.GetProperty("status").GetInt32());
        Assert.False(json.RootElement.GetProperty("reconciliation_required").GetBoolean());
        Assert.False(json.RootElement.GetProperty("automatic_retry").GetBoolean());
    }

    private static IEnumerable<string?> Strings(JsonElement array) => array.EnumerateArray().Select(row => row.GetString());
    private static string Hash(byte[] bytes) => Convert.ToHexString(SHA256.HashData(bytes));
    private static string[] ReadFiles(string root) => Directory.GetFiles(root, "*", SearchOption.AllDirectories)
        .Where(path => Path.GetFileName(path) != ".Studio.project.writer.lock")
        .OrderBy(path => path, StringComparer.Ordinal)
        .Select(path => Path.GetRelativePath(root, path) + "=" + Hash(File.ReadAllBytes(path))).ToArray();
}
