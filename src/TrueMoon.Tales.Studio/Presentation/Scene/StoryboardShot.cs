using TrueMoon.Tales.Studio.Backend;
using TrueMoon.Tales.Studio.Backend.Export;
using TrueMoon.Tales.Studio.Contracts.Projects;
using TrueMoon.Tales.Studio.Models;

namespace TrueMoon.Tales.Studio.Presentation.Scene;

internal sealed record StoryboardPosition(Segment Window, int Frame, int Duration, string Title, string? ClipId,
    ClipContract[]? WindowClips = null, int PreviewOffset = 0)
{
    internal int CursorFrame => Frame + PreviewOffset;
}

internal sealed record StoryboardShot(string SegmentName, string? SegmentId, string? ActionId, string Title, string Description,
    StoryboardPosition? Position, int? PlannedDuration, string? Video, double SourceIn, bool Still, ChapterFragment? Text, double Speed = 1)
{
    internal string Medium => Video is not null ? "Видео" : Still ? "Стоп-кадр" : "Карточка";
    internal (ReferenceEntry Entry, ReferenceVersion Version, bool Action)[] AssignedReferences { get; init; } = [];
    internal TakeReferenceInput[] TakeReferences { get; init; } = [];

    internal static StoryboardShot[] Read(ProjectContract project)
    {
        var notes = BackendAdaptation.Read(project);
        var references = BackendReferenceLibrary.Read(project);
        var segments = project.Segments.ToDictionary(segment => segment.Id, StringComparer.Ordinal);
        var sources = project.Sources.ToDictionary(source => source.Id, StringComparer.Ordinal);
        var windows = project.Montage.Windows.ToLookup(window => window.SegmentId);
        var clipsByWindow = project.Montage.Clips.ToLookup(clip => clip.WindowId);
        var stills = notes.Stills.Select(row => (row.SegmentId, row.ActionId)).ToHashSet();
        var texts = notes.Texts.ToDictionary(row => (row.SegmentId, row.ActionId), row => row.Fragment);
        var rows = new List<StoryboardShot>();
        foreach (var window in project.Montage.Windows.OrderBy(row => row.StartFrame))
        {
            var segment = window.SegmentId is { } id ? segments[id] : null;
            var actions = segment?.Actions ?? [];
            var actionsById = actions.ToDictionary(row => row.Id, StringComparer.Ordinal);
            var clips = clipsByWindow[window.Id].ToArray();
            var name = segment?.Name ?? window.Id;
            var axis = new Segment(window.Id, name, window.StartFrame, window.DurationFrames);
            var shots = new List<StoryboardShot>();
            foreach (var clip in clips)
            {
                var source = sources[clip.SourceId];
                var action = clip.ActionId is { } actionId ? actionsById[actionId] : null;
                var take = source.Takes.FirstOrDefault(row => row.Id == clip.TakeId);
                var title = action?.Title ?? source.Title;
                var position = new StoryboardPosition(axis, clip.StartFrame, clip.DurationFrames, title, clip.Id);
                shots.Add(Card(segment, action, title, position, take, clip.SourceInFrame, clip.Speed));
            }
            foreach (var shot in BackendAnimatic.PlannedShots(window, actions, clips))
            {
                var position = shot.Start is { } start && shot.Duration is { } length
                    ? new StoryboardPosition(axis, start, length, shot.Action.Title, null, clips) : null;
                shots.Add(Card(segment, shot.Action, shot.Action.Title, position));
            }
            rows.AddRange(shots.OrderBy(shot => shot.Position?.Frame ?? long.MaxValue));
        }
        foreach (var segment in project.Segments.Where(segment => !windows.Contains(segment.Id)))
            foreach (var action in segment.Actions)
                rows.Add(Card(segment, action, action.Title, null));
        return rows.ToArray();

        StoryboardShot Card(ScenarioSegmentContract? segment, StoryActionContract? action, string title,
            StoryboardPosition? position, TakeContract? take = null, double sourceIn = 0, double speed = 1)
        {
            var id = segment?.Id;
            var actionId = action?.Id;
            var still = id is not null && actionId is not null && stills.Contains((id, actionId));
            var text = id is null ? null : texts.GetValueOrDefault((id, actionId)) ?? texts.GetValueOrDefault((id, null));
            return new(segment?.Name ?? position?.Window.Name ?? "", id, actionId, title, action?.Description ?? "",
                position, action?.PlannedDurationFrames, take?.VideoAssetId, sourceIn, still, text, speed)
            {
                AssignedReferences = id is null ? [] : references.For(id, actionId).ToArray(),
                TakeReferences = BackendReferenceLibrary.Inputs(take)
            };
        }
    }
}
