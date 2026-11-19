using System.Text.Json;
using System.Text.Json.Nodes;
using TrueMoon.Tales.Studio.Contracts.Projects;
using TrueMoon.Tales.Studio.Models;

namespace TrueMoon.Tales.Studio.Backend;

internal sealed record MontageTitle(string Name, string Text, int StartFrame, int DurationFrames, int FadeInFrames,
    int FadeOutFrames, string FontId, int FontSize, string Color, string Align, string Position, string Background, bool Outline);

internal static class BackendTrackEdits
{
    internal static readonly JsonSerializerOptions Json = new() { PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower };
    internal static readonly string[] Fonts = ["arial", "arial-bold", "georgia", "times", "consolas", "segoe", "dejavu"];
    internal const int MaxSounds = 64;
    private const int MaxTitles = 64;
    private const int MaxMarkers = 1024;

    internal static async Task<ProjectContract> ApplyAsync(ProjectContract project, MontageTrackCommand edit,
        Func<string, int, CancellationToken, Task<double>> probe, CancellationToken token)
    {
        var montage = project.Montage;
        switch (edit)
        {
            case SaveMontageMusic music:
            {
                Timing(music.StartFrame, music.DurationFrames, music.SourceInFrame, music.Gain, 0);
                var audio = await AudioAsync(project, music.Input, 0, music.SourceInFrame, music.DurationFrames, probe, token);
                var row = Object(montage, "music") ?? new JsonObject { ["id"] = "music-" + Guid.NewGuid().ToString("N") };
                return Property(audio.Project, "music", Merge(row, new AudioMusic(audio.Id,
                    music.StartFrame, music.DurationFrames, music.SourceInFrame, music.Gain, music.Muted), audio.Seconds));
            }
            case SaveMontageSound sound:
            {
                Timing(sound.OffsetFrames, sound.DurationFrames, sound.SourceInFrame, sound.Gain, sound.Stream);
                var name = Name(sound.Name);
                if (sound.Lane is not ("voice" or "sfx" or "ambience")) throw Invalid();
                var clip = sound.ClipId is null ? null : montage.Clips.SingleOrDefault(row => row.Id == sound.ClipId) ?? throw Invalid();
                var window = FindWindow(montage, clip?.WindowId ?? sound.SegmentId);
                if (clip is not null && FindWindow(montage, sound.SegmentId).Id != window.Id) throw Invalid();
                if ((long)sound.OffsetFrames + sound.DurationFrames > (clip?.DurationFrames ?? window.DurationFrames)) throw Invalid();
                var rows = Rows(montage, "sound_events");
                var row = Item(rows, sound.Id, "sound-", MaxSounds);
                var audio = await AudioAsync(project, sound.Input, sound.Stream, sound.SourceInFrame, sound.DurationFrames, probe, token);
                Merge(row, new AudioEvent(audio.Id, window.Id, clip?.Id, sound.OffsetFrames,
                    sound.DurationFrames, sound.SourceInFrame, sound.Stream, sound.Gain, sound.Muted), audio.Seconds);
                row["name"] = name; row["lane"] = sound.Lane;
                return Property(audio.Project, "sound_events", rows);
            }
            case SaveMontageTitle title:
            {
                var value = NormalizeTitle(title.Title ?? throw Invalid());
                var rows = Rows(montage, "titles");
                Merge(Item(rows, title.Id, "title-", MaxTitles), value);
                _ = ReadTitles(rows);
                return Property(project, "titles", rows);
            }
            case SaveMontageMarker marker:
            {
                if (marker.Frame < 0 || !montage.Windows.Any(window => marker.Frame >= window.StartFrame &&
                    marker.Frame < (long)window.StartFrame + window.DurationFrames)) throw Invalid();
                var rows = Rows(montage, "markers");
                Merge(Item(rows, marker.Id, "marker-", MaxMarkers), new { Name = Name(marker.Name), marker.Frame });
                return Property(project, "markers", rows);
            }
            case RemoveMontageTrackItem { Track: "music", Id: "music" }:
                return Object(montage, "music") is null ? project : Property(project, "music", null);
            case RemoveMontageTrackItem { Track: "sound_events" or "titles" or "markers" } remove:
            {
                var rows = Rows(montage, remove.Track);
                rows.Remove(Find(rows, remove.Id));
                return Property(project, remove.Track, rows);
            }
            case SetMontageClipAudio native:
                return !native.Enabled && !montage.Properties.ContainsKey("clip_audio") ? project :
                    Property(project, "clip_audio", JsonValue.Create(native.Enabled));
            default: throw Invalid();
        }
    }

    internal static JsonArray Rows(MontageContract montage, string key) => montage.Properties.TryGetValue(key, out var raw) &&
        raw.ValueKind != JsonValueKind.Null ? JsonNode.Parse(raw.GetRawText()) as JsonArray ?? throw Invalid() : new();

    internal static JsonObject? Object(MontageContract montage, string key) => montage.Properties.TryGetValue(key, out var raw) &&
        raw.ValueKind != JsonValueKind.Null ? JsonNode.Parse(raw.GetRawText()) as JsonObject ?? throw Invalid() : null;

    internal static (string Id, MontageTitle Title)[] ReadTitles(JsonArray rows)
    {
        if (rows.Count > MaxTitles) throw Invalid();
        var ids = new HashSet<string>(StringComparer.Ordinal);
        var titles = rows.Select(node =>
        {
            var row = node as JsonObject ?? throw Invalid();
            var id = (string?)row["id"];
            if (id is not { Length: > 0 and <= 80 } ||
                id.Any(ch => !char.IsAsciiLetterOrDigit(ch) && ch is not ('_' or '-')) || !ids.Add(id)) throw Invalid();
            var title = NormalizeTitle(row.Deserialize<MontageTitle>(Json) ?? throw Invalid());
            return (id, title);
        }).ToArray();
        var count = 0;
        foreach (var (_, delta) in titles.SelectMany(row => new[] { (row.title.StartFrame, 1),
            (row.title.StartFrame + row.title.DurationFrames, -1) }).Order())
            if ((count += delta) > 4) throw Invalid();
        return titles;
    }

    internal static SegmentWindowContract FindWindow(MontageContract montage, string id)
    {
        var direct = montage.Windows.FirstOrDefault(row => row.Id == id);
        if (direct is not null) return direct;
        var matches = montage.Windows.Where(row => row.SegmentId == id).Take(2).ToArray();
        return matches.Length == 1 ? matches[0] : throw Invalid();
    }

    private static MontageTitle NormalizeTitle(MontageTitle title)
    {
        Timing(title.StartFrame, title.DurationFrames, 0, 1, 0);
        var text = title.Text?.ReplaceLineEndings("\n").Replace("\t", "    ");
        if (string.IsNullOrWhiteSpace(text) || text.Length > 1000 || text.Count(ch => ch == '\n') > 11 ||
            text.Any(ch => char.IsControl(ch) && ch != '\n') || title.FadeInFrames < 0 || title.FadeOutFrames < 0 ||
            (long)title.FadeInFrames + title.FadeOutFrames > title.DurationFrames || title.FontSize is < 8 or > 256 ||
            !Fonts.Contains(title.FontId, StringComparer.Ordinal) || title.Color is not { Length: 7 } || title.Color[0] != '#' ||
            !title.Color.AsSpan(1).ContainsOnlyHex() || title.Align is not ("left" or "center" or "right") ||
            title.Position is not ("upper" or "center" or "lower") || title.Background is not ("transparent" or "black"))
            throw Invalid();
        return title with { Name = Name(title.Name), Text = text };
    }

    private static bool ContainsOnlyHex(this ReadOnlySpan<char> chars)
    {
        foreach (var value in chars)
            if (!Uri.IsHexDigit(value)) return false;
        return true;
    }

    private static async Task<(ProjectContract Project, string Id, double Seconds)> AudioAsync(ProjectContract project, string input,
        int stream, double sourceIn, int duration, Func<string, int, CancellationToken, Task<double>> probe, CancellationToken token)
    {
        if (input is not { Length: > 0 and <= 4096 } || string.IsNullOrWhiteSpace(input)) throw Invalid();
        var assets = project.Properties.TryGetValue("assets", out var raw)
            ? JsonNode.Parse(raw.GetRawText()) as JsonObject ?? throw Invalid() : new JsonObject();
        var id = input;
        var row = assets[input] as JsonObject;
        if (row is null && input.StartsWith('@'))
        {
            var found = assets.FirstOrDefault(entry => entry.Value is JsonObject item &&
                (string?)item["path"] == input && IsAudioAsset(item));
            id = found.Key ?? "audio-" + Guid.NewGuid().ToString("N");
            row = found.Value as JsonObject;
        }
        if (row is null && id == input || row is not null && !IsAudioAsset(row)) throw Invalid();
        var path = row is not null ? (string?)row["path"] ?? throw Invalid() : input;
        var seconds = await probe(path, stream, token);
        if (!double.IsFinite(seconds) || seconds <= 0 || sourceIn + duration > Math.Floor(seconds * project.Fps + 1e-6))
            throw new ScenarioEditException("montage_audio_source");
        if (row is not null) return (project, id, seconds);
        assets[id] = new JsonObject { ["kind"] = "audio", ["path"] = path };
        var properties = new Dictionary<string, JsonElement>(project.Properties) { ["assets"] = JsonSerializer.SerializeToElement(assets) };
        return (project with { Properties = properties }, id, seconds);
    }

    private static bool IsAudioAsset(JsonObject asset) => (string?)asset["kind"] is null or "audio" or "video";

    private static void Timing(int start, int duration, double source, double gain, int stream)
    {
        if (start < 0 || duration < 1 || (long)start + duration > int.MaxValue || !double.IsFinite(source) || source < 0 ||
            !double.IsFinite(gain) || gain is < 0 or > 1 || stream is < 0 or > 63) throw Invalid();
    }

    private static string Name(string name) => name is { Length: > 0 and <= 160 } && !string.IsNullOrWhiteSpace(name)
        ? name.Trim() : throw Invalid();

    private static JsonObject Find(JsonArray rows, string id) => string.IsNullOrEmpty(id) ? throw Invalid() :
        rows.OfType<JsonObject>().SingleOrDefault(row => (string?)row["id"] == id) ?? throw Invalid();

    private static JsonObject Item(JsonArray rows, string? id, string prefix, int limit)
    {
        if (id is not null) return Find(rows, id);
        if (rows.Count >= limit) throw Invalid();
        var created = new JsonObject { ["id"] = prefix + Guid.NewGuid().ToString("N") };
        rows.Add(created);
        return created;
    }

    private static JsonObject Merge(JsonObject? row, object value, double? seconds = null)
    {
        row ??= new();
        foreach (var (key, field) in JsonSerializer.SerializeToNode(value, Json)!.AsObject())
            row[key] = field?.DeepClone();
        if (seconds is not null) row["source_seconds"] = seconds.Value;
        return row;
    }

    private static ProjectContract Property(ProjectContract project, string key, JsonNode? value)
    {
        if (project.Montage.Properties.TryGetValue(key, out var previous) && JsonNode.DeepEquals(JsonNode.Parse(previous.GetRawText()), value))
            return project;
        var properties = new Dictionary<string, JsonElement>(project.Montage.Properties) { [key] = JsonSerializer.SerializeToElement(value) };
        return project with { Montage = project.Montage with { Properties = properties } };
    }

    private static ScenarioEditException Invalid() => new("invalid_montage_track");
}
