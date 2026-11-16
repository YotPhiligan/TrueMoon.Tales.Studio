using TrueMoon.Tales.Studio.Models;

namespace TrueMoon.Tales.Studio.Editing;

internal sealed record MoveDraft(int RequestedStart, int TimelineStart, Clip[] Clips, bool Changed, bool Valid);

// Predicts update_clip from the immutable pointer-down snapshot, including scenario reflow.
internal sealed class TimelineMove(SceneSnapshot scene)
{
    private Clip[] Clips => scene.SegmentClips ?? scene.Choices.Select(row => row.Clip).ToArray();
    public bool CanMove => scene.Clip.AnchorFrame is null && scene.ScenarioConsistent &&
        (!scene.ScenarioActive || scene.ScenarioItems is not null && Matches(scene.Segment, scene.ScenarioItems, Clips));

    public MoveDraft Move(int delta)
    {
        var original = Clips;
        var minimum = scene.Segment.StartFrame;
        var maximum = (long)minimum + scene.Segment.DurationFrames - (scene.ScenarioActive ? 0 : scene.Clip.DurationFrames);
        if (!CanMove || scene.Clip.DurationFrames > scene.Segment.DurationFrames || maximum < minimum)
            return new(scene.Clip.StartFrame, scene.Clip.StartFrame, original, false, false);
        var requested = (int)Math.Clamp((long)scene.Clip.StartFrame + delta, minimum, maximum);
        if (scene.ScenarioActive)
        {
            var items = scene.ScenarioItems!;
            // An unchanged gesture must preserve the original order, including touching boundaries.
            if (requested == scene.Clip.StartFrame) return new(requested, requested, original, false, true);
            var remaining = items.Where(item => item.Id != scene.Clip.Id).ToList();
            var index = original.Count(clip => clip.Id != scene.Clip.Id && clip.StartFrame < requested);
            remaining.Insert(index, items.Single(item => item.Id == scene.Clip.Id));
            if (!TryProject(scene.Segment, remaining.ToArray(), original, out var projected))
                return Ghost(requested);
            var changed = !remaining.Select(item => item.Id).SequenceEqual(items.Select(item => item.Id));
            return new(requested, projected.Single(clip => clip.Id == scene.Clip.Id).StartFrame, projected, changed, true);
        }
        var draft = original.Select(clip => clip.Id == scene.Clip.Id ? clip with { StartFrame = requested } : clip).ToArray();
        var valid = !draft.Any(clip => clip.Id != scene.Clip.Id &&
            (long)clip.StartFrame < (long)requested + scene.Clip.DurationFrames && (long)clip.StartFrame + clip.DurationFrames > requested);
        return new(requested, requested, draft, requested != scene.Clip.StartFrame, valid);
    }

    private MoveDraft Ghost(int requested) => new(requested, requested,
        Clips.Select(clip => clip.Id == scene.Clip.Id ? clip with { StartFrame = requested } : clip).ToArray(), true, false);

    internal static bool Matches(Segment window, ScenarioPlacement[] items, Clip[] clips) =>
        TryProject(window, items, clips, out var projected) && projected.All(clip => clips.Any(original => original == clip));

    private static bool TryProject(Segment window, ScenarioPlacement[] items, Clip[] clips, out Clip[] projected)
    {
        projected = [];
        if (items.Length != clips.Length || items.Select(item => item.Id).Distinct().Count() != items.Length) return false;
        var rows = new List<Clip>();
        long cursor = window.StartFrame, limit = cursor + window.DurationFrames;
        foreach (var item in items)
        {
            var clip = clips.FirstOrDefault(clip => clip.Id == item.Id);
            if (clip is null || item.DurationFrames is not > 0 || item.DurationFrames != clip.DurationFrames || item.GapBeforeFrames < 0 ||
                item.BlockId != clip.BlockId || item.TakeId != clip.TakeId || item.SourceInFrame != clip.SourceInFrame ||
                (item.Speed ?? 1) != (clip.Speed ?? 1) || item.SourceOutFrame != clip.SourceOutFrame || item.AnchorFrame != clip.AnchorFrame ||
                item.MuteNativeAudio != clip.MuteNativeAudio) return false;
            cursor += item.GapBeforeFrames;
            if (item.AnchorFrame is { } anchor)
            {
                if (cursor > anchor || anchor < window.StartFrame) return false;
                cursor = anchor;
            }
            if (cursor < window.StartFrame || cursor + clip.DurationFrames > limit || cursor > int.MaxValue) return false;
            rows.Add(clip with { StartFrame = (int)cursor });
            cursor += clip.DurationFrames;
        }
        projected = rows.ToArray();
        return true;
    }
}
