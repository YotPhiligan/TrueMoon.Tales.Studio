using System.Text.Json;
using System.Text.Json.Nodes;
using TrueMoon.Tales.Studio.Backend.Export;
using TrueMoon.Tales.Studio.Contracts.Projects;

namespace TrueMoon.Tales.Studio.Backend;

internal static class BackendScenarioEdit
{
    private const string ExplicitWindows = "studio_explicit_windows";

    internal static async Task<ProjectContract> ApplyAsync(ProjectContract project, ScenarioCommand command,
        Func<string, CancellationToken, Task<BackendVideoInfo>> probe, CancellationToken token) => command switch
    {
        SaveSegment edit => Segment(project, edit),
        SaveAction edit => Action(project, edit),
        LinkSource edit => Link(project, edit),
        ImportVideo edit => await ImportAsync(project, edit, probe, token).ConfigureAwait(false),
        RelinkVideo edit => await RelinkAsync(project, edit, probe, token).ConfigureAwait(false),
        PlaceClip edit => await PlaceAsync(project, edit, probe, token).ConfigureAwait(false),
        AssignTake edit => await AssignAsync(project, edit, probe, token).ConfigureAwait(false),
        SplitMontageClip edit => BackendMontageEdits.Split(project, edit),
        RemoveMontageClip edit => BackendMontageEdits.Remove(project, edit),
        SetBlackTransition edit => BackendBlackTransitions.Apply(project, edit),
        _ => throw Invalid()
    };

    internal static bool UsesLegacyLayout(ProjectContract project, string windowId) => !ManualWindows(project).Contains(windowId);

    private static string[] ManualWindows(ProjectContract project) =>
        project.Montage.Properties.TryGetValue(ExplicitWindows, out var value) && value.ValueKind == JsonValueKind.Array
            ? value.EnumerateArray().Where(item => item.ValueKind == JsonValueKind.String).Select(item => item.GetString()!).ToArray() : [];

    internal static ProjectContract MarkExplicit(ProjectContract project, IEnumerable<string> windowIds)
    {
        var old = ManualWindows(project);
        var ids = old.Union(windowIds, StringComparer.Ordinal).ToArray();
        if (ids.Length == old.Length) return project;
        var metadata = new Dictionary<string, JsonElement>(project.Montage.Properties) { [ExplicitWindows] = JsonSerializer.SerializeToElement(ids) };
        return project with { Montage = project.Montage with { Properties = metadata } };
    }

    private static ProjectContract Segment(ProjectContract project, SaveSegment edit)
    {
        var name = Text(edit.Name, 160, required: true);
        var description = Text(edit.Description, 2000, preserveWhitespace: true);
        if (edit.SegmentId is { } id)
        {
            var previous = FindSegment(project, id);
            return previous.Name == name && previous.Description == description ? project :
                ReplaceSegment(project, previous with { Name = name, Description = description });
        }
        if (edit.Start < 0 || edit.Duration <= 0 || (long)edit.Start + edit.Duration > int.MaxValue ||
            project.Montage.Windows.Any(window => Overlaps(edit.Start, edit.Duration, window.StartFrame, window.DurationFrames)))
            throw new ScenarioEditException("scenario_window_conflict");
        var segment = new ScenarioSegmentContract
        {
            Id = Id("segment"), Name = name, Description = description,
            PlannedWindow = new() { StartFrame = edit.Start, DurationFrames = edit.Duration }
        };
        var created = new SegmentWindowContract { Id = Id("window"), SegmentId = segment.Id, StartFrame = edit.Start, DurationFrames = edit.Duration };
        var draft = project with
        {
            Segments = [.. project.Segments, segment],
            Montage = project.Montage with { Windows = [.. project.Montage.Windows, created] }
        };
        return MarkExplicit(draft, [created.Id]);
    }

    private static ProjectContract Action(ProjectContract project, SaveAction edit)
    {
        var segment = FindSegment(project, edit.SegmentId);
        var title = Text(edit.Title, 160, required: true);
        var description = Text(edit.Description, 2000, preserveWhitespace: true);
        var sound = edit.SoundNotes is { } note ? Text(note, 2000, preserveWhitespace: true) : null;
        if (string.IsNullOrWhiteSpace(sound)) sound = null;
        if (edit.PlannedDuration is <= 0) throw Invalid();
        if (edit.ActionId is { } actionId)
        {
            var previous = FindAction(segment, actionId);
            var next = previous with { Title = title, Description = description, PlannedDurationFrames = edit.PlannedDuration,
                SoundNotes = edit.SoundNotes is null ? previous.SoundNotes : sound };
            return previous == next ? project : ReplaceAction(project, segment, next);
        }
        var action = new StoryActionContract { Id = Id("action"), Title = title, Description = description,
            PlannedDurationFrames = edit.PlannedDuration, SoundNotes = sound };
        var draft = ReplaceSegment(project, segment with { Actions = [.. segment.Actions, action] });
        return MarkExplicit(draft, project.Montage.Windows.Where(window => window.SegmentId == segment.Id).Select(window => window.Id));
    }

    internal static ProjectContract MoveAction(ProjectContract project, MoveScenarioAction edit, bool legacyLayout)
    {
        var segment = FindSegment(project, edit.SegmentId);
        var action = FindAction(segment, edit.ActionId);
        if (edit.BeforeActionId == action.Id) return project;
        var ordered = segment.Actions.Where(row => row.Id != action.Id).ToList();
        var index = edit.BeforeActionId is { } before ? ordered.FindIndex(row => row.Id == before) : ordered.Count;
        if (index < 0) throw Invalid();
        ordered.Insert(index, action);
        if (segment.Actions.SequenceEqual(ordered)) return project;

        var reordered = ordered.ToArray();
        var windows = project.Montage.Windows.Where(window => window.SegmentId == segment.Id).ToArray();
        foreach (var window in windows)
        {
            var clips = project.Montage.Clips.Where(clip => clip.WindowId == window.Id).ToArray();
            var unplaced = BackendAnimatic.PlannedShots(window, reordered, clips).Where(shot => shot.Start is null)
                .Select(shot => shot.Action.Id).ToHashSet(StringComparer.Ordinal);
            if (unplaced.Count != 0 && BackendAnimatic.PlannedShots(window, segment.Actions, clips)
                .Any(shot => shot.Start is not null && unplaced.Contains(shot.Action.Id)))
                throw new ScenarioEditException("scenario_order_timing");
        }
        var draft = ReplaceSegment(project, segment with { Actions = reordered });
        return legacyLayout ? MarkExplicit(draft, windows.Select(window => window.Id)) : draft;
    }

    private static ProjectContract Link(ProjectContract project, LinkSource edit)
    {
        var segment = FindSegment(project, edit.SegmentId);
        var action = FindAction(segment, edit.ActionId);
        var source = FindSource(project, edit.SourceId);
        var purpose = Text(edit.Purpose, 160);
        var link = new ActionSourceContract { SourceId = source.Id, Purpose = purpose.Length == 0 ? null : purpose };
        var old = action.Sources.FirstOrDefault(row => row.SourceId == source.Id);
        if (old == link) return project;
        return ReplaceAction(project, segment, action with { Sources = old is null ? [.. action.Sources, link] :
            action.Sources.Select(row => row.SourceId == source.Id ? link : row).ToArray() });
    }

    private static async Task<ProjectContract> ImportAsync(ProjectContract project, ImportVideo edit,
        Func<string, CancellationToken, Task<BackendVideoInfo>> probe, CancellationToken token)
    {
        var source = edit.SourceId is { } id ? FindSource(project, id)
            : new SourceContract { Id = Id("source"), Title = Text(edit.Title, 160, required: true) };
        var reference = Text(edit.Reference, 4096, required: true);
        var takeName = Text(edit.TakeName, 160, required: true);
        await probe(reference, token).ConfigureAwait(false);
        var assetId = Id("video");
        var take = new TakeContract
        {
            Id = Id("take"), VideoAssetId = assetId,
            Properties = new Dictionary<string, JsonElement> { ["name"] = JsonSerializer.SerializeToElement(takeName) }
        };
        var next = source with { Takes = [.. source.Takes, take] };
        return SetReference(project with
        {
            Sources = edit.SourceId is null ? [.. project.Sources, next] :
                project.Sources.Select(row => row.Id == source.Id ? next : row).ToArray()
        }, assetId, reference);
    }

    private static async Task<ProjectContract> RelinkAsync(ProjectContract project, RelinkVideo edit,
        Func<string, CancellationToken, Task<BackendVideoInfo>> probe, CancellationToken token)
    {
        var assetId = VideoAsset(FindSource(project, edit.SourceId), edit.TakeId);
        var reference = Text(edit.Reference, 4096, required: true);
        var info = await probe(reference, token).ConfigureAwait(false);
        var uses = project.Sources.SelectMany(source => source.Takes.Where(take => take.VideoAssetId == assetId)
            .Select(take => (source.Id, take.Id))).ToHashSet();
        foreach (var clip in project.Montage.Clips.Where(clip => clip.TakeId is { } id && uses.Contains((clip.SourceId, id))))
            CheckExtent(project, clip, info);
        return TryReference(project, assetId) == reference ? project : SetReference(project, assetId, reference);
    }

    private static async Task<ProjectContract> PlaceAsync(ProjectContract project, PlaceClip edit,
        Func<string, CancellationToken, Task<BackendVideoInfo>> probe, CancellationToken token)
    {
        var action = FindAction(FindSegment(project, edit.SegmentId), edit.ActionId);
        if (!action.Sources.Any(row => row.SourceId == edit.SourceId)) throw Invalid();
        var windows = project.Montage.Windows.Where(window => window.SegmentId == edit.SegmentId).ToArray();
        if (windows.Length != 1) throw new ScenarioEditException("scenario_window_conflict");
        var window = windows[0];
        if (edit.Start < window.StartFrame || edit.Duration <= 0 || edit.SourceIn < 0 ||
            (long)edit.Start + edit.Duration > (long)window.StartFrame + window.DurationFrames)
            throw new ScenarioEditException("scenario_window_conflict");
        if (project.Montage.Clips.Any(clip => clip.WindowId == window.Id &&
            Overlaps(edit.Start, edit.Duration, clip.StartFrame, clip.DurationFrames)))
            throw new ScenarioEditException("scenario_clip_overlap");
        var asset = VideoAsset(FindSource(project, edit.SourceId), edit.TakeId);
        var clip = new ClipContract
        {
            Id = Id("clip"), ActionId = action.Id, WindowId = window.Id, SourceId = edit.SourceId, TakeId = edit.TakeId,
            StartFrame = edit.Start, DurationFrames = edit.Duration, SourceInFrame = edit.SourceIn,
            SourceOutFrame = (double)edit.SourceIn + edit.Duration
        };
        CheckExtent(project, clip, await probe(Reference(project, asset), token).ConfigureAwait(false));
        return MarkExplicit(project with { Montage = project.Montage with { Clips = [.. project.Montage.Clips, clip] } }, [window.Id]);
    }

    private static async Task<ProjectContract> AssignAsync(ProjectContract project, AssignTake edit,
        Func<string, CancellationToken, Task<BackendVideoInfo>> probe, CancellationToken token)
    {
        var current = project.Montage.Clips.FirstOrDefault(clip => clip.Id == edit.ClipId) ?? throw Invalid();
        if (current.TakeId == edit.TakeId) return project;
        var asset = VideoAsset(FindSource(project, current.SourceId), edit.TakeId);
        var next = current with { TakeId = edit.TakeId };
        CheckExtent(project, next, await probe(Reference(project, asset), token).ConfigureAwait(false));
        return project with { Montage = project.Montage with
        { Clips = project.Montage.Clips.Select(clip => clip.Id == next.Id ? next : clip).ToArray() } };
    }

    private static void CheckExtent(ProjectContract project, ClipContract clip, BackendVideoInfo info)
    {
        var end = clip.SourceOutFrame ?? clip.SourceInFrame + clip.DurationFrames * clip.Speed;
        if (end > Math.Floor(info.FrameCount / info.Fps * project.Fps + 1e-6))
            throw new ScenarioEditException("scenario_source_range");
    }

    internal static ProjectContract SetReference(ProjectContract project, string assetId, string reference)
    {
        var assets = project.Properties.TryGetValue("assets", out var current)
            ? JsonNode.Parse(current.GetRawText()) as JsonObject ?? throw Invalid() : new JsonObject();
        if (assets[assetId] is null) assets[assetId] = new JsonObject();
        if (assets[assetId] is not JsonObject asset) throw Invalid();
        asset["path"] = reference;
        return project with
        {
            Properties = new Dictionary<string, JsonElement>(project.Properties)
            {
                ["assets"] = JsonSerializer.SerializeToElement(assets)
            }
        };
    }

    internal static string? TryReference(ProjectContract project, string assetId) =>
        project.Properties.TryGetValue("assets", out var assets) && assets.ValueKind == JsonValueKind.Object &&
        assets.TryGetProperty(assetId, out var asset) && asset.ValueKind == JsonValueKind.Object &&
        asset.TryGetProperty("path", out var value) && value.ValueKind == JsonValueKind.String ? value.GetString() : null;

    internal static string TakeName(TakeContract take) =>
        take.Properties.TryGetValue("name", out var name) && name.ValueKind == JsonValueKind.String && !string.IsNullOrWhiteSpace(name.GetString())
            ? name.GetString()! : take.Id;

    private static string Reference(ProjectContract project, string asset) =>
        TryReference(project, asset) ?? throw new ScenarioEditException("media_not_found");
    private static string VideoAsset(SourceContract source, string takeId) =>
        source.Takes.FirstOrDefault(take => take.Id == takeId)?.VideoAssetId ?? throw Invalid();
    private static ScenarioSegmentContract FindSegment(ProjectContract project, string id) =>
        project.Segments.FirstOrDefault(row => row.Id == id) ?? throw Invalid();
    private static StoryActionContract FindAction(ScenarioSegmentContract segment, string id) =>
        segment.Actions.FirstOrDefault(row => row.Id == id) ?? throw Invalid();
    private static SourceContract FindSource(ProjectContract project, string id) =>
        project.Sources.FirstOrDefault(row => row.Id == id) ?? throw Invalid();
    private static ProjectContract ReplaceSegment(ProjectContract project, ScenarioSegmentContract segment) =>
        project with { Segments = project.Segments.Select(row => row.Id == segment.Id ? segment : row).ToArray() };
    private static ProjectContract ReplaceAction(ProjectContract project, ScenarioSegmentContract segment, StoryActionContract action) =>
        ReplaceSegment(project, segment with { Actions = segment.Actions.Select(row => row.Id == action.Id ? action : row).ToArray() });
    private static bool Overlaps(int start, int length, int other, int otherLength) =>
        start < (long)other + otherLength && other < (long)start + length;
    private static string Text(string? value, int limit, bool required = false, bool preserveWhitespace = false)
    {
        var text = (preserveWhitespace ? value : value?.Trim()) ?? "";
        return text.Length <= limit && (!required || text.Length != 0) && !text.Contains('\0') ? text : throw Invalid();
    }
    private static string Id(string type) => type + "-" + Guid.NewGuid().ToString("N");
    private static ScenarioEditException Invalid() => new("invalid_scenario_edit");
}
