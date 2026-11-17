using TrueMoon.Tales.Studio.Contracts.Projects;
using TrueMoon.Tales.Studio.Editing;
using TrueMoon.Tales.Studio.Models;

namespace TrueMoon.Tales.Studio.Backend;

internal static class BackendSceneMapping
{
    internal static SceneSnapshot Map(ProjectContract project, bool active, string? clipId)
    {
        if (project.Revision > int.MaxValue) throw new InvalidDataException("Revision exceeds the current scene UI range.");
        var choices = project.Montage.Clips.Select(clip =>
        {
            var window = project.Montage.Windows.Single(row => row.Id == clip.WindowId);
            var semantic = project.Segments.FirstOrDefault(row => row.Id == window.SegmentId);
            var action = semantic?.Actions.FirstOrDefault(row => row.Id == clip.ActionId);
            var source = project.Sources.Single(row => row.Id == clip.SourceId);
            var takes = source.Takes.Where(take => !string.IsNullOrEmpty(take.VideoAssetId)).Select(take =>
            {
                var extent = project.Montage.Clips.Where(row => row.TakeId == take.Id)
                    .Select(row => row.SourceOutFrame ?? row.SourceInFrame + row.DurationFrames * row.Speed).DefaultIfEmpty(1).Max();
                if (!double.IsFinite(extent) || extent > int.MaxValue) throw new InvalidDataException("Clip source extent exceeds the current scene UI range.");
                var frames = checked((int)Math.Ceiling(Math.Max(1, extent)));
                return new Take(take.Id, take.VideoAssetId!, new(project.Fps, frames / project.Fps, frames), take.Id);
            }).ToArray();
            return new ClipChoice(new(clip.Id, clip.WindowId, clip.SourceId, clip.TakeId, action?.Title,
                clip.StartFrame, clip.DurationFrames, clip.SourceInFrame, clip.SourceOutFrame, clip.Speed, clip.AnchorFrame, clip.MuteNativeAudio, clip.GapProtected),
                new(window.Id, semantic?.Name ?? window.Id, window.StartFrame, window.DurationFrames), action?.Title ?? source.Title, takes);
        }).ToArray();
        var selected = clipId is null ? choices.FirstOrDefault(row => row.Takes.Any(take => take.Id == row.Clip.TakeId))
            : choices.FirstOrDefault(row => row.Clip.Id == clipId);
        if (selected is null) throw new InvalidDataException("Нет доступного клипа с назначенным видеодублем.");
        ScenarioPlacement[] Items(SegmentWindowContract window)
        {
            var actions = project.Segments.FirstOrDefault(row => row.Id == window.SegmentId)?.Actions ?? [];
            return actions.Select(action =>
            {
                var uses = project.Montage.Clips.Where(row => row.WindowId == window.Id && row.ActionId == action.Id).ToArray();
                // Existing editing projection is 1:1. Multiple uses stay readable but cannot be reordered here.
                var use = uses.Length == 1 ? uses[0] : null;
                return new ScenarioPlacement(use?.Id ?? "", use?.SourceId ?? "", use?.TakeId, use?.DurationFrames,
                    action.GapBeforeFrames, use?.SourceInFrame ?? 0, use?.SourceOutFrame, use?.Speed, action.AnchorFrame, use?.MuteNativeAudio ?? false, action.GapProtected);
            }).ToArray();
        }
        var consistent = !active || project.Montage.Windows.All(window =>
            TimelineMove.Matches(new(window.Id, "", window.StartFrame, window.DurationFrames), Items(window),
                choices.Where(row => row.Clip.SegmentId == window.Id).Select(row => row.Clip).ToArray()));
        var current = project.Montage.Windows.Single(row => row.Id == selected.Segment.Id);
        return new(project.Name, checked((int)project.Revision), project.Fps, selected.Segment, selected.Clip,
            selected.Title, selected.Takes, choices, choices.Where(row => row.Clip.SegmentId == current.Id).Select(row => row.Clip).ToArray(),
            [], active ? Items(current) : null, active, consistent);
    }
}
