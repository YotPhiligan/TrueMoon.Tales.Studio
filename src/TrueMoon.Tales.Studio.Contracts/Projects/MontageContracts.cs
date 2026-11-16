using System.Text.Json;

namespace TrueMoon.Tales.Studio.Contracts.Projects;

/// <summary>The saved montage, separate from a potentially conflicting scenario draft.</summary>
public sealed record MontageContract
{
    /// <summary>The saved placement windows.</summary>
    public SegmentWindowContract[] Windows { get; init; } = [];
    /// <summary>The saved clip placements in original storage order.</summary>
    public ClipContract[] Clips { get; init; } = [];
    /// <summary>Other montage data, including sound, titles, markers and history.</summary>
    public IReadOnlyDictionary<string, JsonElement> Properties { get; init; } = new Dictionary<string, JsonElement>();
}

/// <summary>A montage window referencing a semantic segment when attached.</summary>
public sealed record SegmentWindowContract
{
    /// <summary>The window identity, retained even if its draft segment was removed.</summary>
    public required string Id { get; init; }
    /// <summary>The semantic segment, or null for a detached saved legacy window.</summary>
    public string? SegmentId { get; init; }
    /// <summary>The absolute start in project frames.</summary>
    public int StartFrame { get; init; }
    /// <summary>The saved window length in project frames.</summary>
    public int DurationFrames { get; init; }
}

/// <summary>One montage use of a material, independently identified from its story action.</summary>
public sealed record ClipContract
{
    /// <summary>The stable placement identity.</summary>
    public required string Id { get; init; }
    /// <summary>The associated action, or null for unassigned/detached saved montage.</summary>
    public string? ActionId { get; init; }
    /// <summary>The saved montage window.</summary>
    public required string WindowId { get; init; }
    /// <summary>The owning library source of the selected take.</summary>
    public required string SourceId { get; init; }
    /// <summary>The selected take, or null for a placeholder.</summary>
    public string? TakeId { get; init; }
    /// <summary>The absolute start in project frames.</summary>
    public int StartFrame { get; init; }
    /// <summary>The actual montage duration in project frames.</summary>
    public int DurationFrames { get; init; }
    /// <summary>The inclusive source entry in project-frame units; may be fractional.</summary>
    public double SourceInFrame { get; init; }
    /// <summary>The optional exclusive source exit in project-frame units.</summary>
    public double? SourceOutFrame { get; init; }
    /// <summary>The playback speed.</summary>
    public double Speed { get; init; } = 1;
    /// <summary>The optional pinned montage start.</summary>
    public int? AnchorFrame { get; init; }
    /// <summary>Whether the preceding gap is protected.</summary>
    public bool GapProtected { get; init; }
    /// <summary>Whether the source's native audio is muted.</summary>
    public bool MuteNativeAudio { get; init; }
}
