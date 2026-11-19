using TrueMoon.Tales.Studio.Contracts.Projects;

namespace TrueMoon.Tales.Studio.Backend.Export;

internal sealed record AnimaticCard(int StartFrame, int DurationFrames, string Text, string? Still);
internal sealed record PlannedShot(StoryActionContract Action, int? Start, int? Duration);

internal static class BackendAnimatic
{
    internal static AnimaticCard[] Cards(ProjectContract project)
    {
        var notes = BackendAdaptation.Read(project);
        var stills = notes.Stills.ToDictionary(row => (row.SegmentId, row.ActionId), row => row.Reference);
        var segments = project.Segments.ToDictionary(row => row.Id, StringComparer.Ordinal);
        var sources = project.Sources.ToDictionary(row => row.Id, StringComparer.Ordinal);
        var cards = new List<AnimaticCard>();
        var clipsByWindow = project.Montage.Clips.ToLookup(row => row.WindowId);
        foreach (var window in project.Montage.Windows.OrderBy(row => row.StartFrame))
        {
            var clips = clipsByWindow[window.Id].OrderBy(row => row.StartFrame).ToArray();
            var actions = window.SegmentId is { } segmentId ? segments[segmentId].Actions : [];
            var byAction = actions.ToDictionary(row => row.Id, StringComparer.Ordinal);
            foreach (var clip in clips)
            {
                var source = sources[clip.SourceId];
                if (HasVideo(source, clip.TakeId)) continue;
                var action = clip.ActionId is { } id ? byAction[id] : null;
                AddCard(clip.StartFrame, clip.DurationFrames, action, source.Title, window.SegmentId);
            }

            foreach (var shot in PlannedShots(window, actions, clips))
            {
                if (shot.Start is not { } start || shot.Duration is not { } length)
                    throw new BackendExportException("animatic_timing");
                AddCard(start, length, shot.Action, "", window.SegmentId);
            }
        }
        if (cards.Count > 500) throw new BackendExportException("animatic_timing");
        return cards.OrderBy(row => row.StartFrame).ToArray();

        void AddCard(int start, int duration, StoryActionContract? action, string fallback, string? segmentId)
        {
            var title = ShortText(action?.Title ?? fallback, 65);
            var description = ShortText(action?.Description ?? "", 110);
            var reference = action is not null && segmentId is not null ? stills.GetValueOrDefault((segmentId, action.Id)) : null;
            var text = string.Join('\n', new[] { title.Length == 0 ? "Монтажный план" : title, description }.Where(line => line.Length != 0));
            cards.Add(new(start, duration, text, reference));
        }
    }

    internal static IEnumerable<PlannedShot> PlannedShots(SegmentWindowContract window, StoryActionContract[] actions, ClipContract[] clips)
    {
        long cursor = window.StartFrame;
        var end = cursor + window.DurationFrames;
        var actionClips = clips.ToLookup(row => row.ActionId);
        var unresolved = false;
        foreach (var action in actions)
        {
            if (actionClips.Contains(action.Id))
            {
                cursor = Math.Max(cursor, actionClips[action.Id].Max(clip => (long)clip.StartFrame + clip.DurationFrames));
                continue;
            }
            var earliest = cursor + action.GapBeforeFrames;
            var start = action.AnchorFrame ?? earliest;
            var duration = action.PlannedDurationFrames;
            if (unresolved || duration is not { } length || start < earliest || start > int.MaxValue || start + length > end ||
                clips.Any(clip => clip.StartFrame < start + length && (long)clip.StartFrame + clip.DurationFrames > start))
            {
                unresolved = true;
                yield return new(action, null, duration);
                continue;
            }
            yield return new(action, (int)start, duration);
            cursor = start + length;
        }
    }

    internal static bool HasVideo(SourceContract source, string? takeId) =>
        source.Takes.Any(take => take.Id == takeId && !string.IsNullOrWhiteSpace(take.VideoAssetId));

    private static string ShortText(string value, int maximum)
    {
        var compact = string.Join(' ', value.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries));
        if (compact.Length <= maximum) return compact;
        var end = char.IsHighSurrogate(compact[maximum - 1]) ? maximum - 1 : maximum;
        return compact[..end] + "…";
    }
}
