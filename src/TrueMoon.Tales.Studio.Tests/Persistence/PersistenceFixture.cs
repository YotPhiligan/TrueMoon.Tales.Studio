using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using TrueMoon.Tales.Studio.Contracts.Projects;
using TrueMoon.Tales.Studio.Persistence;

namespace TrueMoon.Tales.Studio.Tests.Persistence;

internal sealed class PersistenceFixture : IDisposable
{
    private readonly string _allocatedRoot;
    internal string Root => _allocatedRoot;
    internal string ProjectDirectory { get; }
    internal string ProjectPath { get; }
    internal string HistoryDirectory => Path.Combine(ProjectDirectory, "Production", "History");
    internal string JournalPath => Path.Combine(ProjectDirectory, ".Studio.project.transaction.json");
    internal string[] HistoryFiles => Directory.Exists(HistoryDirectory) ? Directory.GetFiles(HistoryDirectory, "*.json") : [];
    internal byte[] InitialBytes { get; } = LegacyBytes();

    internal PersistenceFixture(bool legacyLayout = false, string fileName = "Studio.project.json")
    {
        _allocatedRoot = Path.GetFullPath(Path.Combine(Path.GetTempPath(), "studio-store-" + Guid.NewGuid().ToString("N")));
        ProjectDirectory = Path.Combine(_allocatedRoot, "project");
        ProjectPath = Path.Combine(legacyLayout ? Path.Combine(ProjectDirectory, "Production") : ProjectDirectory, fileName);
        Directory.CreateDirectory(Path.GetDirectoryName(ProjectPath)!);
        File.WriteAllBytes(ProjectPath, InitialBytes);
    }

    internal ProjectStore Store(bool writable = true, IAtomicFileReplacement? replacement = null, TimeProvider? clock = null) =>
        new(new ProjectStoreOptions { ProjectDirectory = ProjectDirectory, AllowedWriteRoots = writable ? [ProjectDirectory] : [] },
            replacement ?? new AtomicFileReplacement(), clock ?? TimeProvider.System);

    internal static ProjectContract Expanded(ProjectContract project)
    {
        var first = project.Segments.Single(s => s.Id == "segment-main");
        var main = first.Actions.Single();
        var clip = project.Montage.Clips.Single(c => c.Id == "clip-main");
        var extra = project.Montage.Clips.Single(c => c.Id == "clip-extra");
        return project with
        {
            Name = "Сборка A–B–A",
            Sources = project.Sources.Reverse().Select(s => s with { Takes = s.Takes.Reverse().ToArray() }).ToArray(),
            Segments =
            [
                project.Segments.Single(s => s.Id == "segment-extra"),
                first with { Actions =
                [
                    new() { Id = "new-text-action", Title = "Пока только текст" },
                    main with { Sources = [new() { SourceId = "source-b", Purpose = "reaction" }, new() { SourceId = "source-a", Purpose = "main" }] }
                ] }
            ],
            Montage = project.Montage with
            {
                Windows = project.Montage.Windows.Reverse().ToArray(),
                Clips =
                [
                    extra,
                    clip with { Id = "clip-main-tail", StartFrame = 84, SourceInFrame = 48, SourceOutFrame = 72 },
                    clip with { Id = "clip-reaction", SourceId = "source-b", TakeId = "take-b", StartFrame = 48, SourceInFrame = 0, SourceOutFrame = 24 },
                    clip
                ]
            }
        };
    }

    internal static byte[] Annotate(byte[] bytes)
    {
        var root = JsonNode.Parse(bytes)!.AsObject();
        root["opaque_envelope"] = "envelope";
        if (root["last_write"] is JsonObject audit) audit["opaque_audit"] = "audit";
        var project = root["project"]!;
        project["opaque_project"] = "project";
        foreach (var segment in project["segments"]!.AsArray())
        {
            segment!["opaque_segment"] = segment["id"]!.GetValue<string>();
            foreach (var action in segment["actions"]!.AsArray())
            {
                action!["opaque_action"] = action["id"]!.GetValue<string>();
                foreach (var binding in action["sources"]!.AsArray())
                    binding!["opaque_binding"] = binding["source_id"]!.GetValue<string>();
            }
        }
        foreach (var source in project["sources"]!.AsArray())
        {
            source!["opaque_source"] = source["id"]!.GetValue<string>();
            foreach (var take in source["takes"]!.AsArray()) take!["opaque_take"] = take["id"]!.GetValue<string>();
        }
        foreach (var window in project["montage"]!["windows"]!.AsArray()) window!["opaque_window"] = window["id"]!.GetValue<string>();
        foreach (var clip in project["montage"]!["clips"]!.AsArray()) clip!["opaque_clip"] = clip["id"]!.GetValue<string>();
        return Encoding.UTF8.GetBytes(root.ToJsonString());
    }

    internal static JsonElement ById(JsonElement array, string id, string property = "id") =>
        array.EnumerateArray().Single(row => row.GetProperty(property).GetString() == id);

    internal static byte[] LegacyBytes() => Encoding.UTF8.GetBytes("\uFEFF" + """
        {
          "schema_version":1,"id":"project-synthetic","name":"Синтетическая копия","revision":11,"fps":24,
          "root":"Z:/not-an-allowed-write-root","updated_at":"2026-11-09T00:00:00Z",
          "assets":{"video-a":{"path":"Z:/media-never-opened/A.mp4"},"video-b":{"path":"@videos/Fixtures/B.mp4"}},
          "segments":[{"id":"production-library","name":"Библиотека","block_ids":["source-a","source-b"]}],
          "activity":[{"revision":10,"actor":"author","action":"Прежняя правка"}],
          "opaque_root":{"precise_integer":9007199254740993,"unicode":"Прежние данные"},
          "blocks":[
            {"id":"source-a","label":"A","title":"Общий план","prompt":"Оригинальный промпт","settings":{"seed":"18446744073709551615","profile":"original"},
             "takes":[{"id":"take-a","video_asset_id":"video-a","native_av":"@videos/native.safetensors","review_state":"accepted"},{"id":"take-a-alt","video_asset_id":"video-a"}]},
            {"id":"source-b","label":"B","title":"Реакция","takes":[{"id":"take-b","video_asset_id":"video-b"}]}
          ],
          "timeline":{"version":1,"fps":24,
            "segments":[{"id":"segment-main","name":"Первый сегмент","color":"mint","start_frame":0,"duration_frames":240},{"id":"segment-extra","name":"Другой сегмент","color":"blue","start_frame":300,"duration_frames":120}],
            "clips":[{"id":"clip-main","segment_id":"segment-main","block_id":"source-a","take_id":"take-a","start_frame":12,"duration_frames":24,"source_in_frame":0,"source_out_frame":24},
                     {"id":"clip-extra","segment_id":"segment-extra","block_id":"source-b","take_id":"take-b","start_frame":312,"duration_frames":24,"source_in_frame":0}],
            "scenario":{"version":1,"segments":[
              {"id":"segment-main","name":"Первый смысловой сегмент","description":"Содержание A","color":"mint","start_frame":0,"duration_frames":240,"items":[{"id":"clip-main","block_id":"source-a","title":"Действие A","description":"Основное действие","duration_frames":24,"gap_before_frames":12,"take_id":"take-a","source_in_frame":0}]},
              {"id":"segment-extra","name":"Другой смысловой сегмент","description":"Содержание B","color":"blue","start_frame":300,"duration_frames":120,"items":[{"id":"clip-extra","block_id":"source-b","title":"Действие B","description":"Другое действие","duration_frames":24,"gap_before_frames":12,"take_id":"take-b","source_in_frame":0}]}
            ]},"markers":[{"id":"beat","frame":24,"name":"Музыкальная опора"}],"undo":[{"opaque_history":true}],"redo":[]
          }
        }
        """);

    public void Dispose()
    {
        var path = Path.GetFullPath(_allocatedRoot);
        var temp = Path.TrimEndingDirectorySeparator(Path.GetFullPath(Path.GetTempPath())) + Path.DirectorySeparatorChar;
        var comparison = OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;
        if (!path.StartsWith(temp, comparison) || !Path.GetFileName(path).StartsWith("studio-store-", StringComparison.Ordinal))
            throw new InvalidOperationException("Refusing cleanup outside the allocated test directory.");
        if (Directory.Exists(path)) Directory.Delete(path, true);
    }
}
