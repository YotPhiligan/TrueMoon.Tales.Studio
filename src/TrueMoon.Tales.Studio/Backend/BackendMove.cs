using TrueMoon.Tales.Studio.Contracts.Projects;
using TrueMoon.Tales.Studio.Editing;

namespace TrueMoon.Tales.Studio.Backend;

internal sealed class InvalidMoveException() : InvalidOperationException("The clip move does not fit the existing project layout.");

internal static class BackendMove
{
    internal static ProjectContract Apply(ProjectContract project, bool scenarioActive, string clipId, int requestedStart)
    {
        var clip = project.Montage.Clips.FirstOrDefault(row => row.Id == clipId) ?? throw new InvalidMoveException();
        var window = project.Montage.Windows.Single(row => row.Id == clip.WindowId);
        var legacyLayout = scenarioActive && BackendScenarioEdit.UsesLegacyLayout(project, window.Id);
        if (requestedStart < window.StartFrame || (long)requestedStart > (long)window.StartFrame + window.DurationFrames - (legacyLayout ? 0 : clip.DurationFrames))
            throw new InvalidMoveException();
        var snapshot = BackendSceneMapping.Map(project, scenarioActive, clipId);
        var move = new TimelineMove(snapshot).Move(checked(requestedStart - clip.StartFrame));
        if (!move.Valid) throw new InvalidMoveException();
        if (!move.Changed) return project;
        var positions = move.Clips.ToDictionary(row => row.Id, row => row.StartFrame);
        var result = project with
        {
            Montage = project.Montage with { Clips = project.Montage.Clips.Select(row => positions.TryGetValue(row.Id, out var start) ? row with { StartFrame = start } : row).ToArray() }
        };
        if (legacyLayout)
        {
            var ids = move.Clips.Select(row => project.Montage.Clips.Single(clip => clip.Id == row.Id).ActionId!).ToArray();
            result = result with { Segments = result.Segments.Select(segment => segment.Id == window.SegmentId
                ? segment with { Actions = ids.Select(id => segment.Actions.Single(action => action.Id == id)).ToArray() } : segment).ToArray() };
        }
        return result;
    }
}
