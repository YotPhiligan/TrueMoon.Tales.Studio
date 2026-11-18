using System.Text.Json;
using System.Text.Json.Nodes;
using TrueMoon.Tales.Studio.Contracts.Projects;

namespace TrueMoon.Tales.Studio.Backend;

internal sealed record BlackTransition(string LeftClipId, string RightClipId, int FadeOutFrames, int PauseFrames, int FadeInFrames,
    int OutStartFrame, int BlackStartFrame, int RightStartFrame, int InEndFrame);

internal static class BackendBlackTransitions
{
    internal static void Validate(MontageContract montage) => Compile(montage);

    internal static BlackTransition[] Compile(MontageContract montage)
    {
        if (!montage.Properties.TryGetValue("transitions", out var values)) return [];
        if (values.ValueKind != JsonValueKind.Array || values.GetArrayLength() > 2000) throw Invalid();
        if (values.GetArrayLength() == 0) return [];
        var clips = montage.Clips.OrderBy(row => row.StartFrame).ThenBy(row => row.Id, StringComparer.Ordinal).ToArray();
        var neighbors = clips.Zip(clips.Skip(1)).ToDictionary(pair => (pair.First.Id, pair.Second.Id));
        var seen = new HashSet<string>(StringComparer.Ordinal);
        var result = new List<BlackTransition>();
        foreach (var row in values.EnumerateArray())
        {
            var left = Identifier(row, "left_clip_id");
            var right = Identifier(row, "right_clip_id");
            if (!neighbors.TryGetValue((left, right), out var pair) || !seen.Add(right)) throw Invalid();
            var fadeOut = Frames(row, "fade_out_frames");
            var pause = Frames(row, "pause_frames");
            var fadeIn = Frames(row, "fade_in_frames");
            var gap = (long)pair.Second.StartFrame - pair.First.StartFrame - pair.First.DurationFrames;
            if (gap < 0 || pause < gap || (long)fadeOut + pause - gap > pair.First.DurationFrames ||
                fadeIn > pair.Second.DurationFrames || (long)pair.Second.StartFrame + fadeIn > int.MaxValue)
                throw Invalid();
            var black = pair.Second.StartFrame - pause;
            result.Add(new(left, right, fadeOut, pause, fadeIn, black - fadeOut, black, pair.Second.StartFrame,
                pair.Second.StartFrame + fadeIn));
        }
        var incoming = result.ToDictionary(row => row.RightClipId, StringComparer.Ordinal);
        if (result.Any(row => incoming.TryGetValue(row.LeftClipId, out var previous) && previous.InEndFrame > row.OutStartFrame))
            throw Invalid();
        return result.OrderBy(row => row.OutStartFrame).ToArray();
    }

    internal static ProjectContract Apply(ProjectContract project, SetBlackTransition edit)
    {
        var montage = project.Montage;
        var previous = Compile(montage).FirstOrDefault(row => row.RightClipId == edit.RightId);
        var remove = edit.FadeOut == 0 && edit.Pause == 0 && edit.FadeIn == 0;
        if (remove && previous is not null && previous.LeftClipId != edit.LeftId) throw Invalid();
        if (remove && previous is null || previous is not null && previous.LeftClipId == edit.LeftId &&
            previous.FadeOutFrames == edit.FadeOut && previous.PauseFrames == edit.Pause && previous.FadeInFrames == edit.FadeIn)
            return project;
        var rows = montage.Properties.TryGetValue("transitions", out var raw)
            ? JsonNode.Parse(raw.GetRawText())!.AsArray() : new JsonArray();
        var current = rows.OfType<JsonObject>().FirstOrDefault(row => (string?)row["right_clip_id"] == edit.RightId);
        if (remove) rows.Remove(current);
        else
        {
            var entry = current ?? new JsonObject();
            entry["left_clip_id"] = edit.LeftId;
            entry["right_clip_id"] = edit.RightId;
            entry["fade_out_frames"] = edit.FadeOut;
            entry["pause_frames"] = edit.Pause;
            entry["fade_in_frames"] = edit.FadeIn;
            if (current is null) rows.Add(entry);
        }
        var properties = new Dictionary<string, JsonElement>(montage.Properties) { ["transitions"] = JsonSerializer.SerializeToElement(rows) };
        var updated = montage with { Properties = properties };
        Validate(updated);
        return project with { Montage = updated };
    }

    private static int Frames(JsonElement row, string name) =>
        row.TryGetProperty(name, out var field) && field.ValueKind == JsonValueKind.Number && field.TryGetInt32(out var value) && value >= 0
            ? value : throw Invalid();
    private static string Identifier(JsonElement row, string name) =>
        row.ValueKind == JsonValueKind.Object && row.TryGetProperty(name, out var field) && field.ValueKind == JsonValueKind.String &&
        field.GetString() is { Length: > 0 } value ? value : throw Invalid();
    private static ScenarioEditException Invalid() => new("montage_transition_conflict");
}
