using System.Text.Json;
using TrueMoon.Tales.Studio.Backend.Export;
using TrueMoon.Tales.Studio.Contracts.Projects;

namespace TrueMoon.Tales.Studio.Backend;

internal sealed record FrameNoteAnchor(string WindowId, string? ClipId, string? ActionId, string? TakeId, string? AssetId,
    int OriginalFrame, int Offset, int SourceFrame, double SourceFps, string Sha256)
{
    internal bool Matches(BackendMediaReply frame) => AssetId is null
        ? string.Equals(frame.SourceSha256, Sha256, StringComparison.OrdinalIgnoreCase)
        : frame.AssetId == AssetId && frame.FrameIndex == SourceFrame && frame.Fps == SourceFps &&
            string.Equals(frame.PixelSha256, Sha256, StringComparison.OrdinalIgnoreCase);
}

internal sealed record FrameNote(string Id, FrameNoteAnchor Anchor, string Text);
internal sealed record FrameNoteLocation(int Frame, int Offset, string? SegmentId);

internal static class BackendFrameNotes
{
    private const string Key = "studio_frame_notes_v1";
    internal const int MaxTextLength = 1600;

    internal static FrameNote[] Read(ProjectContract project)
    {
        if (!project.Properties.TryGetValue(Key, out var raw)) return [];
        var notes = raw.Deserialize<FrameNote[]>(BackendAdaptation.Json) ?? throw new InvalidDataException("Invalid frame notes.");
        if (notes.Any(row => row is not { Text.Length: > 0 and <= MaxTextLength, Anchor: not null } || !Id(row.Id) || !Valid(row.Anchor)) ||
            notes.Select(row => row.Id).Distinct(StringComparer.Ordinal).Count() != notes.Length)
            throw new InvalidDataException("Invalid frame note.");
        return notes;
    }

    internal static async Task<ProjectContract> ApplyAsync(ProjectContract project, SaveFrameNote edit,
        Func<FrameNoteAnchor, FrameNoteLocation, Task<BackendMediaReply>> observe)
    {
        if (edit.Text is not { Length: > 0 and <= MaxTextLength } || string.IsNullOrWhiteSpace(edit.Text) ||
            edit.Text.Any(character => char.IsControl(character) && character is not ('\r' or '\n' or '\t')))
            throw new ScenarioEditException("invalid_frame_note");
        var text = edit.Text.ReplaceLineEndings("\n").Trim();
        var notes = Read(project);
        if (edit.Id is { } id)
        {
            var index = Array.FindIndex(notes, note => note.Id == id);
            if (index < 0 || edit.Anchor is not null) throw new ScenarioEditException("invalid_frame_note");
            if (text == notes[index].Text) return project;
            notes[index] = notes[index] with { Text = text };
        }
        else
        {
            if (edit.Anchor is not { } anchor || !Valid(anchor) ||
                Locate(project, anchor) is not { } location || location.Frame != anchor.OriginalFrame || location.Offset != anchor.Offset)
                throw new ScenarioEditException("frame_note_unavailable");
            var current = await observe(anchor, location).ConfigureAwait(false);
            if (current.ErrorCode == "revision_conflict") throw new ScenarioEditException(current.ErrorCode);
            if (current.ErrorCode == "media_busy") throw new ScenarioEditException(current.ErrorCode);
            if (current.ErrorCode.Length != 0 || !anchor.Matches(current))
                throw new ScenarioEditException("frame_note_unavailable");
            notes = [.. notes, new("note-" + Guid.NewGuid().ToString("N"), anchor with { Sha256 = anchor.Sha256.ToUpperInvariant() }, text)];
        }
        var properties = new Dictionary<string, JsonElement>(project.Properties)
        { [Key] = JsonSerializer.SerializeToElement(notes, BackendAdaptation.Json) };
        return project with { Properties = properties };
    }

    internal static FrameNoteLocation? Locate(ProjectContract project, FrameNoteAnchor anchor)
    {
        var window = project.Montage.Windows.SingleOrDefault(row => row.Id == anchor.WindowId);
        if (window is null) return null;
        var clips = project.Montage.Clips.Where(row => row.WindowId == window.Id).ToArray();
        var clip = anchor.ClipId is { } id ? clips.SingleOrDefault(row => row.Id == id) : null;
        if (anchor.ClipId is not null && clip is null) return null;
        var take = clip?.TakeId is { } selected
            ? project.Sources.SingleOrDefault(row => row.Id == clip.SourceId)?.Takes.SingleOrDefault(row => row.Id == selected) : null;
        if (anchor.AssetId is { } video)
        {
            if (clip is null || clip.TakeId != anchor.TakeId || take?.VideoAssetId != video) return null;
            var offset = VideoOffset(clip, project.Fps, anchor);
            return offset is { } at ? Position(window, clip.StartFrame, clip.DurationFrames, at) : null;
        }
        if (take?.VideoAssetId is not null || window.SegmentId is not { } segmentId || anchor.ActionId is not { } action ||
            !BackendAdaptation.Read(project).Stills.Any(still => still.SegmentId == segmentId && still.ActionId == action)) return null;
        if (clip is not null)
            return clip.ActionId == action ? Position(window, clip.StartFrame, clip.DurationFrames, anchor.Offset) : null;
        var segment = project.Segments.Single(row => row.Id == segmentId);
        var plan = BackendAnimatic.PlannedShots(window, segment.Actions, clips).SingleOrDefault(row => row.Action.Id == action);
        return plan is { Start: { } start, Duration: { } duration } ? Position(window, start, duration, anchor.Offset) : null;
    }

    // Find a project frame which still shows the selected native frame, including fractional trim and retiming.
    private static int? VideoOffset(ClipContract clip, double fps, FrameNoteAnchor anchor)
    {
        double At(int offset) => Math.Floor((clip.SourceInFrame + offset * clip.Speed) * anchor.SourceFps / fps);
        if (anchor.Offset < clip.DurationFrames && At(anchor.Offset) == anchor.SourceFrame) return anchor.Offset;
        var first = 0;
        var last = clip.DurationFrames;
        while (first < last)
        {
            var middle = first + (last - first) / 2;
            if (At(middle) < anchor.SourceFrame) first = middle + 1;
            else last = middle;
        }
        return first < clip.DurationFrames && At(first) == anchor.SourceFrame ? first : null;
    }

    private static FrameNoteLocation? Position(SegmentWindowContract window, int start, int duration, int offset)
    {
        var frame = (long)start + offset;
        return offset >= 0 && offset < duration && frame >= window.StartFrame && frame <= int.MaxValue &&
            frame < (long)window.StartFrame + window.DurationFrames ? new((int)frame, offset, window.SegmentId) : null;
    }

    private static bool Valid(FrameNoteAnchor anchor) => Id(anchor.WindowId) &&
        anchor.OriginalFrame >= 0 && anchor.Offset >= 0 && BackendConnection.IsHash(anchor.Sha256) &&
        (anchor.AssetId is null
            ? Id(anchor.ActionId) && (anchor.ClipId is null || Id(anchor.ClipId)) &&
                anchor.TakeId is null && anchor.SourceFrame == 0 && anchor.SourceFps == 0
            : Id(anchor.ClipId) && Id(anchor.AssetId) && Id(anchor.TakeId) && anchor.ActionId is null &&
                anchor.SourceFrame is >= 0 and < 10_000_000 && double.IsFinite(anchor.SourceFps) && anchor.SourceFps is > 0 and <= 240);

    private static bool Id(string? value) => !string.IsNullOrWhiteSpace(value) && value.Length <= 160;
}
