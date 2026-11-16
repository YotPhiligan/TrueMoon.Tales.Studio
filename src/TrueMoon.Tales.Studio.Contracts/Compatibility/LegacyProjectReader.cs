using System.Text.Json;
using TrueMoon.Tales.Studio.Contracts.Projects;
using TrueMoon.Tales.Studio.Contracts.Serialization;

namespace TrueMoon.Tales.Studio.Contracts.Compatibility;

/// <summary>Pure in-memory adaptation of schema_version=1; never migrates files or launches work.</summary>
public static class LegacyProjectReader
{
    /// <summary>Reads a legacy project, retaining an owned clone of every original field.</summary>
    /// <param name="json">The project JSON; a leading Unicode BOM is accepted.</param>
    /// <returns>Story intentions, saved montage, and the unchanged original document.</returns>
    public static LegacyProjectReadResult Read(string json)
    {
        ArgumentNullException.ThrowIfNull(json);
        using var document = JsonDocument.Parse(json.StartsWith('\uFEFF') ? json[1..] : json);
        return Read(document.RootElement);
    }

    /// <summary>Reads a caller-owned JSON element and clones its data before returning.</summary>
    /// <param name="document">The legacy project object.</param>
    /// <returns>A snapshot independent of the caller's JsonDocument lifetime.</returns>
    public static LegacyProjectReadResult Read(JsonElement document)
    {
        Object(document);
        var version = Integer(document, "schema_version");
        if (version != 1) throw new NotSupportedException($"Unsupported legacy project version {version}.");
        var original = document.Clone();
        var sources = Rows(original, "blocks").Select(source => new SourceContract
        {
            Id = Required(source, "id"), Label = Text(source, "label") ?? "", Title = Text(source, "title") ?? "",
            Takes = Rows(source, "takes").Select(take => new TakeContract
            {
                Id = Required(take, "id"), VideoAssetId = Text(take, "video_asset_id"),
                Properties = Extras(take, "id", "video_asset_id")
            }).ToArray(),
            Properties = Extras(source, "id", "label", "title", "takes")
        }).ToArray();
        var sourceIndex = Unique(sources, s => s.Id);
        var timeline = OptionalObject(original, "timeline");
        if (timeline is JsonElement saved && Integer(saved, "version", 1) != 1)
            throw new NotSupportedException("Unsupported legacy timeline version.");
        var savedWindows = Rows(timeline, "segments").ToArray();
        var savedClips = Rows(timeline, "clips").ToArray();
        var scenario = OptionalObject(timeline, "scenario");
        var active = scenario is JsonElement story && story.EnumerateObject().Any();
        if (active && Integer(scenario!.Value, "version") != 1)
            throw new NotSupportedException("Unsupported legacy scenario version.");
        var segments = active ? ReadScenario(scenario!.Value) : Adopt(savedWindows, savedClips, sourceIndex);
        var segmentIndex = Unique(segments, s => s.Id);
        var actionIndex = Unique(segments.SelectMany(s => s.Actions.Select(a => (Action: a, Segment: s.Id))), a => a.Action.Id);
        var issues = new List<LegacyProjectIssue>();
        var windows = savedWindows.Select(window =>
        {
            var id = Required(window, "id");
            var attached = segmentIndex.ContainsKey(id);
            if (!attached) issues.Add(new("unlinked_window_segment", id));
            return new SegmentWindowContract { Id = id, SegmentId = attached ? id : null,
                StartFrame = Integer(window, "start_frame"), DurationFrames = Integer(window, "duration_frames") };
        }).ToArray();
        var clips = savedClips.Select(clip =>
        {
            var id = Required(clip, "id");
            var sourceId = Required(clip, "block_id");
            var windowId = Required(clip, "segment_id");
            var attached = actionIndex.TryGetValue(id, out var action) && action.Segment == windowId &&
                action.Action.Sources.Any(s => s.SourceId == sourceId);
            if (!attached) issues.Add(new("unlinked_clip_action", id));
            return new ClipContract
            {
                Id = id, ActionId = attached ? id : null, WindowId = windowId, SourceId = sourceId,
                TakeId = Text(clip, "take_id"), StartFrame = Integer(clip, "start_frame"),
                DurationFrames = Integer(clip, "duration_frames"), SourceInFrame = Number(clip, "source_in_frame", 0),
                SourceOutFrame = NullableNumber(clip, "source_out_frame"), Speed = Number(clip, "speed", 1),
                AnchorFrame = NullableInteger(clip, "anchor_frame"), GapProtected = Boolean(clip, "gap_protected"),
                MuteNativeAudio = Boolean(clip, "mute_native_audio")
            };
        }).ToArray();
        var project = new ProjectContract
        {
            Id = Required(original, "id"), Name = Text(original, "name") ?? "", Revision = Long(original, "revision"),
            Fps = Number(original, "fps", 24), Sources = sources, Segments = segments,
            Montage = new() { Windows = windows, Clips = clips, Properties = Extras(timeline, "segments", "clips", "scenario", "version") },
            Properties = Extras(original, "schema_version", "id", "name", "revision", "fps", "blocks", "timeline")
        };
        // Validate saved placements independently of draft timing/budget conflicts.
        ProjectContractJson.Validate(project);
        return new(project, original, active, issues.ToArray());
    }

    private static ScenarioSegmentContract[] ReadScenario(JsonElement scenario) => Rows(scenario, "segments").Select(segment => new ScenarioSegmentContract
    {
        Id = Required(segment, "id"), Name = Text(segment, "name") ?? "", Description = Text(segment, "description") ?? "",
        SegmentType = Text(segment, "segment_type"),
        PlannedWindow = new() { StartFrame = NullableInteger(segment, "start_frame"), DurationFrames = NullableInteger(segment, "duration_frames") },
        Actions = Rows(segment, "items").Select(item => new StoryActionContract
        {
            Id = Required(item, "id"), Title = Text(item, "title") ?? "", Description = Text(item, "description") ?? "",
            PlannedDurationFrames = NullableInteger(item, "duration_frames"), GapBeforeFrames = Integer(item, "gap_before_frames", 0),
            Sources = [new() { SourceId = Required(item, "block_id") }], AnchorFrame = NullableInteger(item, "anchor_frame"),
            GapProtected = Boolean(item, "gap_protected"), SoundNotes = Text(item, "sound_notes"), ContinuityNotes = Text(item, "continuity_notes")
        }).ToArray()
    }).ToArray();

    private static ScenarioSegmentContract[] Adopt(JsonElement[] windows, JsonElement[] clips, Dictionary<string, SourceContract> sources) => windows.Select(window =>
    {
        var id = Required(window, "id");
        var cursor = Integer(window, "start_frame");
        var actions = clips.Where(c => Required(c, "segment_id") == id)
            .OrderBy(c => Integer(c, "start_frame")).ThenBy(c => Required(c, "id"), StringComparer.Ordinal).Select(clip =>
            {
                var sourceId = Required(clip, "block_id");
                if (!sources.TryGetValue(sourceId, out var source)) throw new JsonException("Unknown legacy clip source.");
                var start = Integer(clip, "start_frame");
                var duration = Integer(clip, "duration_frames");
                var result = new StoryActionContract
                {
                    Id = Required(clip, "id"), Title = Text(clip, "story_title") is { Length: > 0 } title ? title :
                        source.Title.Length > 0 ? source.Title : source.Label,
                    PlannedDurationFrames = duration, GapBeforeFrames = checked(start - cursor), Sources = [new() { SourceId = sourceId }],
                    AnchorFrame = NullableInteger(clip, "anchor_frame"), GapProtected = Boolean(clip, "gap_protected")
                };
                cursor = checked(start + duration);
                return result;
            }).ToArray();
        return new ScenarioSegmentContract { Id = id, Name = Text(window, "name") ?? "", SegmentType = Text(window, "segment_type"),
            PlannedWindow = new() { StartFrame = Integer(window, "start_frame"), DurationFrames = Integer(window, "duration_frames") }, Actions = actions };
    }).ToArray();

    private static Dictionary<string, T> Unique<T>(IEnumerable<T> values, Func<T, string> key)
    {
        var result = new Dictionary<string, T>(StringComparer.Ordinal);
        foreach (var value in values)
            if (!result.TryAdd(key(value), value)) throw new JsonException("Duplicate legacy identity.");
        return result;
    }
    private static JsonElement? OptionalObject(JsonElement? parent, string key)
    {
        if (parent is not JsonElement obj || !obj.TryGetProperty(key, out var value) || value.ValueKind == JsonValueKind.Null) return null;
        Object(value);
        return value;
    }
    private static IEnumerable<JsonElement> Rows(JsonElement? parent, string key)
    {
        if (parent is not JsonElement obj || !obj.TryGetProperty(key, out var value)) return [];
        if (value.ValueKind != JsonValueKind.Array) throw new JsonException($"Expected array {key}.");
        return value.EnumerateArray().Select(value => { Object(value); return value; });
    }
    private static void Object(JsonElement value)
    {
        if (value.ValueKind != JsonValueKind.Object) throw new JsonException("Expected a JSON object.");
    }
    private static string Required(JsonElement obj, string key) => Text(obj, key) is { Length: > 0 } value ? value : throw new JsonException($"Missing {key}.");
    private static string? Text(JsonElement obj, string key)
    {
        if (!obj.TryGetProperty(key, out var value) || value.ValueKind == JsonValueKind.Null) return null;
        return value.ValueKind == JsonValueKind.String ? value.GetString() : throw new JsonException($"Expected string {key}.");
    }
    private static int Integer(JsonElement obj, string key, int? fallback = null) => NullableInteger(obj, key) ?? fallback ?? throw new JsonException($"Missing {key}.");
    private static int? NullableInteger(JsonElement obj, string key)
    {
        if (!obj.TryGetProperty(key, out var value) || value.ValueKind == JsonValueKind.Null) return null;
        return value.ValueKind == JsonValueKind.Number && value.TryGetInt32(out var number) ? number : throw new JsonException($"Expected integer {key}.");
    }
    private static long Long(JsonElement obj, string key) => obj.TryGetProperty(key, out var value) && value.ValueKind == JsonValueKind.Number && value.TryGetInt64(out var number) ? number : throw new JsonException($"Expected integer {key}.");
    private static double Number(JsonElement obj, string key, double fallback) => NullableNumber(obj, key) ?? fallback;
    private static double? NullableNumber(JsonElement obj, string key)
    {
        if (!obj.TryGetProperty(key, out var value) || value.ValueKind == JsonValueKind.Null) return null;
        return value.ValueKind == JsonValueKind.Number && value.TryGetDouble(out var number) ? number : throw new JsonException($"Expected number {key}.");
    }
    private static bool Boolean(JsonElement obj, string key)
    {
        if (!obj.TryGetProperty(key, out var value)) return false;
        return value.ValueKind is JsonValueKind.True or JsonValueKind.False ? value.GetBoolean() : throw new JsonException($"Expected Boolean {key}.");
    }
    private static IReadOnlyDictionary<string, JsonElement> Extras(JsonElement? obj, params string[] known) => obj is JsonElement value
        ? value.EnumerateObject().Where(p => !known.Contains(p.Name, StringComparer.Ordinal)).ToDictionary(p => p.Name, p => p.Value.Clone(), StringComparer.Ordinal)
        : new Dictionary<string, JsonElement>();
}
