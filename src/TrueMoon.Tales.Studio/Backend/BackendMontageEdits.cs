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
            Properties = UpdateSounds(montage, UpdateJoins(montage, original.Id, right.Id), original.Id, right.Id, leftLength)
        });
    }

    internal static ProjectContract Remove(ProjectContract project, RemoveMontageClip edit)
    {
        var montage = project.Montage;
        var clip = Find(montage, edit.ClipId);
        if (clip.AnchorFrame is not null || clip.GapProtected)
            throw new ScenarioEditException("montage_protected_clip");
        return Place(project, clip, montage with
        {
            Clips = montage.Clips.Where(row => row.Id != clip.Id).ToArray(),
            Properties = UpdateSounds(montage, UpdateJoins(montage, clip.Id, null), clip.Id, null, 0)
        });
    }

    private static ClipContract Find(MontageContract montage, string id) =>
        montage.Clips.FirstOrDefault(clip => clip.Id == id) ?? throw new ScenarioEditException("invalid_scenario_edit");

    private static ProjectContract Place(ProjectContract project, ClipContract original, MontageContract montage) =>
        BackendScenarioEdit.MarkExplicit(project with { Montage = montage }, [original.WindowId]);

    private static IReadOnlyDictionary<string, JsonElement> UpdateSounds(MontageContract montage,
        IReadOnlyDictionary<string, JsonElement> properties, string clip, string? right, int cut)
    {
        if (!montage.Properties.TryGetValue("sound_events", out var events) || events.ValueKind == JsonValueKind.Null)
            return properties;
        var rows = BackendTrackEdits.Rows(montage, "sound_events");
        var changed = false;
        foreach (var row in rows.OfType<JsonObject>().ToArray())
        {
            if ((string?)row["clip_id"] != clip) continue;
            if (right is null) { rows.Remove(row); changed = true; continue; }
            var from = (int?)row["offset_frames"] ?? throw InvalidSound();
            var frames = (int?)row["duration_frames"] ?? throw InvalidSound();
            if (from < 0 || frames < 1) throw InvalidSound();
            if (from >= cut)
            {
                row["clip_id"] = right;
                row["offset_frames"] = from - cut;
            }
            else if ((long)from + frames > cut)
            {
                if (rows.Count == BackendTrackEdits.MaxSounds) throw InvalidSound();
                var head = cut - from;
                var source = (double?)row["source_in_frame"] ?? throw InvalidSound();
                var tail = row.DeepClone().AsObject();
                tail["id"] = "sound-" + Guid.NewGuid().ToString("N");
                tail["clip_id"] = right;
                tail["offset_frames"] = 0;
                tail["duration_frames"] = frames - head;
                tail["source_in_frame"] = source + head;
                row["duration_frames"] = head;
                rows.Add(tail);
            }
            else continue;
            changed = true;
        }
        return changed
            ? new Dictionary<string, JsonElement>(properties) { ["sound_events"] = JsonSerializer.SerializeToElement(rows) }
            : properties;
    }

    private static ScenarioEditException InvalidSound() => new("montage_clip_sound");

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
