using System.Text.Json.Nodes;
using TrueMoon.Tales.Studio.Contracts.Projects;
using TrueMoon.Tales.Studio.Models;

namespace TrueMoon.Tales.Studio.Backend;

internal static class BackendTrackProjection
{
    internal static TrackSpan[] Spans(ProjectContract project)
    {
        var result = new List<TrackSpan>();
        var montage = project.Montage;
        if (Try(() => BackendTrackEdits.Object(montage, "music")) is { } music &&
            Try(() => Span(music, "music", "music", "Музыка", 0)) is { } soundtrack) result.Add(soundtrack);
        Add("sound_events", item =>
        {
            var clipId = (string?)item["clip_id"];
            var clip = clipId is null ? null : montage.Clips.FirstOrDefault(row => row.Id == clipId);
            if (clipId is not null && clip is null) return null;
            var owner = clip?.StartFrame ?? BackendTrackEdits.FindWindow(montage, (string?)item["segment_id"] ?? "").StartFrame;
            var start = (int?)item["offset_frames"] ?? -1;
            var duration = (int?)item["duration_frames"] ?? 0;
            if (clip is not null) duration = Math.Min(duration, clip.DurationFrames - start);
            var lane = clip is null ? "Звук сегмента" : "Звук клипа";
            return start >= 0 && duration > 0 && (long)owner + start + duration <= int.MaxValue
                ? Span(item, "sound_events", (string?)item["id"], lane, clip is null ? 1 : 2, owner + start, duration)
                : null;
        });
        Add("titles", row => Span(row, "titles", (string?)row["id"], "Титры", 3));
        Add("markers", row => Span(row, "markers", (string?)row["id"], "Маркеры", 4, (int?)row["frame"], 1));
        return result.ToArray();

        void Add(string key, Func<JsonObject, TrackSpan?> map)
        {
            if (Try(() => BackendTrackEdits.Rows(montage, key)) is not { } rows) return;
            foreach (var row in rows.OfType<JsonObject>())
                if (Try(() => map(row)) is { } span) result.Add(span);
        }
    }

    private static TrackSpan? Span(JsonObject row, string track, string? id, string lane, int index,
        int? start = null, int? duration = null)
    {
        start ??= (int?)row["start_frame"];
        duration ??= (int?)row["duration_frames"];
        return id is not { Length: > 0 } || start is not >= 0 || duration is not > 0 || (long)start.Value + duration > int.MaxValue
            ? null : new(track, id, (string?)row["name"] ?? lane, lane, index, start.Value, duration.Value, (bool?)row["muted"] ?? false);
    }

    private static T? Try<T>(Func<T?> read) where T : class
    {
        try { return read(); }
        catch (Exception error) when (error is ScenarioEditException or InvalidOperationException or FormatException or System.Text.Json.JsonException)
        { return null; }
    }
}
