using System.Text.Json;
using System.Text.Json.Serialization;
using TrueMoon.Tales.Studio.Contracts.Projects;

namespace TrueMoon.Tales.Studio.Contracts.Serialization;

/// <summary>JSON transport encoding and structural validation for project contracts.</summary>
public static class ProjectContractJson
{
    private static readonly JsonSerializerOptions Options = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower,
        UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow
    };

    /// <summary>Encodes a validated transport snapshot, not a legacy project file.</summary>
    /// <param name="project">The snapshot to encode.</param>
    /// <returns>The versioned contract JSON.</returns>
    public static string Serialize(ProjectContract project)
    {
        Validate(project);
        return JsonSerializer.Serialize(project, Options);
    }

    /// <summary>Decodes an explicitly versioned contract; legacy files use LegacyProjectReader.</summary>
    /// <param name="json">The contract JSON.</param>
    /// <returns>The structurally valid snapshot.</returns>
    public static ProjectContract Deserialize(string json)
    {
        ArgumentNullException.ThrowIfNull(json);
        using var document = JsonDocument.Parse(json);
        if (document.RootElement.ValueKind != JsonValueKind.Object ||
            !document.RootElement.TryGetProperty("contract_version", out var version) || version.ValueKind != JsonValueKind.Number || !version.TryGetInt32(out var number))
            throw new JsonException("An explicit contract_version is required.");
        if (number != 1) throw new NotSupportedException($"Unsupported contract version {number}.");
        var project = document.RootElement.Deserialize<ProjectContract>(Options) ?? throw new JsonException("Missing project contract.");
        Validate(project);
        return project;
    }

    /// <summary>Checks identities/references and numeric structure, preserving unresolved timing intentions.</summary>
    /// <param name="project">The snapshot to validate.</param>
    public static void Validate(ProjectContract project)
    {
        ArgumentNullException.ThrowIfNull(project);
        if (project.Version != 1) throw new NotSupportedException($"Unsupported contract version {project.Version}.");
        Id(project.Id);
        Require(project.Revision >= 0 && double.IsFinite(project.Fps) && project.Fps > 0, "Invalid revision or time base.");
        Require(project.Segments is not null && project.Sources is not null && project.Montage is not null && project.Properties is not null, "Null project collections.");
        var sources = Index(project.Sources!, x => x.Id);
        foreach (var source in sources.Values)
        {
            Require(source.Takes is not null && source.Properties is not null, "Null source collections.");
            foreach (var take in Index(source.Takes!, x => x.Id).Values)
            {
                if (take.VideoAssetId is not null) Id(take.VideoAssetId);
                Require(take.Properties is not null, "Null take metadata.");
            }
        }
        var segments = Index(project.Segments!, x => x.Id);
        var actions = new Dictionary<string, (StoryActionContract Action, string Segment)>(StringComparer.Ordinal);
        foreach (var segment in segments.Values)
        {
            Require(segment.PlannedWindow is not null && segment.Actions is not null, "Null scenario data.");
            Nonnegative(segment.PlannedWindow!.StartFrame);
            Positive(segment.PlannedWindow.DurationFrames);
            foreach (var action in Index(segment.Actions!, x => x.Id).Values)
            {
                Require(actions.TryAdd(action.Id, (action, segment.Id)), "Duplicate action ID.");
                Positive(action.PlannedDurationFrames);
                Nonnegative(action.GapBeforeFrames);
                Nonnegative(action.AnchorFrame);
                Require(action.Sources is not null, "Null action sources.");
                foreach (var binding in Index(action.Sources!, x => x.SourceId).Values)
                    Require(sources.ContainsKey(binding.SourceId), "Unknown action source.");
            }
        }
        Require(project.Montage!.Windows is not null && project.Montage.Clips is not null && project.Montage.Properties is not null, "Null montage data.");
        var windows = Index(project.Montage.Windows!, x => x.Id);
        foreach (var window in windows.Values)
        {
            if (window.SegmentId is not null) Require(segments.ContainsKey(window.SegmentId), "Unknown window segment.");
            Nonnegative(window.StartFrame);
            Require(window.DurationFrames > 0, "Invalid window duration.");
        }
        foreach (var clip in Index(project.Montage.Clips!, x => x.Id).Values)
        {
            Id(clip.WindowId);
            Id(clip.SourceId);
            Require(windows.TryGetValue(clip.WindowId, out var window), "Unknown clip window.");
            Require(sources.TryGetValue(clip.SourceId, out var source), "Unknown clip source.");
            if (clip.TakeId is not null) Require(source!.Takes.Any(t => t.Id == clip.TakeId), "Take belongs to another source or is missing.");
            if (clip.ActionId is not null)
            {
                Require(actions.TryGetValue(clip.ActionId, out var action), "Unknown clip action.");
                Require(window!.SegmentId == action.Segment && action.Action.Sources.Any(s => s.SourceId == clip.SourceId), "Clip is not linked to its action's segment/source.");
            }
            Nonnegative(clip.StartFrame);
            Nonnegative(clip.AnchorFrame);
            Require(clip.DurationFrames > 0 && clip.StartFrame >= window!.StartFrame &&
                (long)clip.StartFrame + clip.DurationFrames <= (long)window.StartFrame + window.DurationFrames, "Clip outside its saved window.");
            Require(double.IsFinite(clip.SourceInFrame) && clip.SourceInFrame >= 0 && double.IsFinite(clip.Speed) && clip.Speed is >= .25 and <= 4, "Invalid source timing.");
            if (clip.SourceOutFrame is double end) Require(double.IsFinite(end) && end > clip.SourceInFrame, "Invalid source exit.");
            if (clip.TakeId is null) Require(clip.SourceInFrame == 0 && clip.SourceOutFrame is null && clip.Speed == 1, "Placeholder has video timing.");
        }
    }

    private static Dictionary<string, T> Index<T>(IEnumerable<T> rows, Func<T, string> key) where T : class
    {
        var result = new Dictionary<string, T>(StringComparer.Ordinal);
        foreach (var row in rows)
        {
            Require(row is not null, "Null collection item.");
            var id = key(row!);
            Id(id);
            Require(result.TryAdd(id, row!), "Duplicate identity.");
        }
        return result;
    }
    private static void Id(string? id) => Require(!string.IsNullOrWhiteSpace(id), "Missing identity.");
    private static void Nonnegative(int? value) => Require(value is null or >= 0, "Negative frame position.");
    private static void Positive(int? value) => Require(value is null or > 0, "Invalid planned duration.");
    private static void Require(bool condition, string message)
    {
        if (!condition) throw new JsonException(message);
    }
}
