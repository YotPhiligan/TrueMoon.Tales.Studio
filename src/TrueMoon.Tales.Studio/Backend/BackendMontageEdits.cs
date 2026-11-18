using System.Text.Json;
using System.Text.Json.Nodes;
using TrueMoon.Tales.Studio.Contracts.Projects;

namespace TrueMoon.Tales.Studio.Backend;

internal static class BackendMontageEdits
{
    internal static ProjectContract Split(ProjectContract project, SplitMontageClip edit)
    {
        var montage = project.Montage;
        var original = Find(montage, edit.ClipId);
        if (edit.Frame <= original.StartFrame || edit.Frame >= (long)original.StartFrame + original.DurationFrames)
            throw new ScenarioEditException("montage_split_range");
        var leftLength = edit.Frame - original.StartFrame;
        RequireSoundBoundary(montage, original.Id, leftLength);
        var boundary = original.SourceInFrame + leftLength * original.Speed;
        if (original.TakeId is not null && boundary >= (original.SourceOutFrame ??
            original.SourceInFrame + original.DurationFrames * original.Speed))
            throw new ScenarioEditException("montage_split_range");
        var left = original with { DurationFrames = leftLength, SourceOutFrame = original.TakeId is null ? null : boundary };
        var right = original with
        {
            Id = "clip-" + Guid.NewGuid().ToString("N"),
            StartFrame = edit.Frame,
            DurationFrames = original.DurationFrames - leftLength,
            SourceInFrame = original.TakeId is null ? 0 : boundary,
            AnchorFrame = null,
            GapProtected = false
        };
        var clips = montage.Clips.SelectMany(row => row.Id == original.Id ? new[] { left, right } : [row]).ToArray();
        return Place(project, original, montage with
        {
            Clips = clips,
            Properties = UpdateJoins(montage, original.Id, right.Id)
        });
    }

    internal static ProjectContract Remove(ProjectContract project, RemoveMontageClip edit)
    {
        var montage = project.Montage;
        var clip = Find(montage, edit.ClipId);
        if (clip.AnchorFrame is not null || clip.GapProtected)
            throw new ScenarioEditException("montage_protected_clip");
        RequireSoundBoundary(montage, clip.Id, null);
        return Place(project, clip, montage with
        {
            Clips = montage.Clips.Where(row => row.Id != clip.Id).ToArray(),
            Properties = UpdateJoins(montage, clip.Id, null)
        });
    }

    private static ClipContract Find(MontageContract montage, string id) =>
        montage.Clips.FirstOrDefault(clip => clip.Id == id) ?? throw new ScenarioEditException("invalid_scenario_edit");

    private static ProjectContract Place(ProjectContract project, ClipContract original, MontageContract montage) =>
        BackendScenarioEdit.MarkExplicit(project with { Montage = montage }, [original.WindowId]);

    private static void RequireSoundBoundary(MontageContract montage, string clip, int? retainedLength)
    {
        if (!montage.Properties.TryGetValue("sound_events", out var events) || events.ValueKind != JsonValueKind.Array) return;
        foreach (var row in events.EnumerateArray())
        {
            if (row.ValueKind != JsonValueKind.Object || !row.TryGetProperty("clip_id", out var owner) ||
                owner.ValueKind != JsonValueKind.String || owner.GetString() != clip) continue;
            if (retainedLength is { } length && row.TryGetProperty("offset_frames", out var offset) &&
                row.TryGetProperty("duration_frames", out var duration) && offset.ValueKind == JsonValueKind.Number &&
                duration.ValueKind == JsonValueKind.Number && offset.TryGetInt32(out var from) && duration.TryGetInt32(out var frames) &&
                from >= 0 && frames > 0 && (long)from + frames <= length) continue;
            throw new ScenarioEditException("montage_clip_sound");
        }
    }

    private static IReadOnlyDictionary<string, JsonElement> UpdateJoins(MontageContract montage, string oldId, string? rightHalf)
    {
        if (!montage.Properties.TryGetValue("transitions", out var value)) return montage.Properties;
        var rows = JsonNode.Parse(value.GetRawText()) as JsonArray ?? throw new ScenarioEditException("invalid_scenario_edit");
        foreach (var row in rows.ToArray())
        {
            if (row is not JsonObject join) throw new ScenarioEditException("invalid_scenario_edit");
            if (rightHalf is null)
            {
                if ((string?)join["left_clip_id"] == oldId || (string?)join["right_clip_id"] == oldId) rows.Remove(row);
            }
            else if ((string?)join["left_clip_id"] == oldId) join["left_clip_id"] = rightHalf;
        }
        return new Dictionary<string, JsonElement>(montage.Properties) { ["transitions"] = JsonSerializer.SerializeToElement(rows) };
    }
}
