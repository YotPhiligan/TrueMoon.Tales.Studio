using TrueMoon.Tales.Studio.Contracts.Projects;
using TrueMoon.Tales.Studio.Editing;

namespace TrueMoon.Tales.Studio.Backend;

internal sealed class InvalidTrimException() : InvalidOperationException("The clip trim does not fit the source or saved montage layout.");

internal static class BackendTrim
{
    internal static ProjectContract Apply(ProjectContract project, bool scenarioActive, string clipId,
        double sourceStart, double sourceEnd, int timelineStart, double capacity)
    {
        var clip = project.Montage.Clips.FirstOrDefault(row => row.Id == clipId) ?? throw new InvalidTrimException();
        var window = project.Montage.Windows.Single(row => row.Id == clip.WindowId);
        // Editing shared action metadata across multiple montage windows is a separate authoring contract.
        if (scenarioActive && project.Montage.Windows.Count(row => row.SegmentId == window.SegmentId) != 1)
            throw new InvalidTrimException();
        var snapshot = BackendSceneMapping.Map(project, scenarioActive, clipId);
        var trim = TimelineTrimLayout.Project(snapshot, capacity, sourceStart, sourceEnd, timelineStart);
        if (!trim.Valid) throw new InvalidTrimException();
        if (!trim.Changed) return project;
        var updates = trim.Clips.ToDictionary(row => row.Id);
        var result = project with
        {
            Montage = project.Montage with
            {
                Clips = project.Montage.Clips.Select(row => updates.TryGetValue(row.Id, out var edited)
                    ? row with { StartFrame = edited.StartFrame, DurationFrames = edited.DurationFrames,
                        SourceInFrame = edited.SourceInFrame, SourceOutFrame = edited.SourceOutFrame } : row).ToArray()
            }
        };
        if (scenarioActive)
        {
            var gap = trim.ScenarioItems!.Single(row => row.Id == clipId).GapBeforeFrames;
            result = result with
            {
                Segments = project.Segments.Select(segment => segment.Id == window.SegmentId
                    ? segment with { Actions = segment.Actions.Select(action => action.Id == clip.ActionId
                        ? action with { GapBeforeFrames = gap } : action).ToArray() } : segment).ToArray()
            };
        }
        return result;
    }
}
