namespace TrueMoon.Tales.Studio.Contracts.Projects;

/// <summary>A semantic segment and its ordered screen actions.</summary>
public sealed record ScenarioSegmentContract
{
    /// <summary>The stable semantic identifier.</summary>
    public required string Id { get; init; }
    /// <summary>The segment name.</summary>
    public string Name { get; init; } = "";
    /// <summary>The intended content.</summary>
    public string Description { get; init; } = "";
    /// <summary>The optional semantic type.</summary>
    public string? SegmentType { get; init; }
    /// <summary>The intended window; unknown times stay null.</summary>
    public PlannedWindowContract PlannedWindow { get; init; } = new();
    /// <summary>Actions in scenario order, independent of the saved montage.</summary>
    public StoryActionContract[] Actions { get; init; } = [];
}

/// <summary>An intended timing budget, which may differ from the saved montage window.</summary>
public sealed record PlannedWindowContract
{
    /// <summary>The planned start, or null if not placed.</summary>
    public int? StartFrame { get; init; }
    /// <summary>The planned duration, or null if unknown.</summary>
    public int? DurationFrames { get; init; }
}

/// <summary>A whole screen action, realizable with any number of sources and clips.</summary>
public sealed record StoryActionContract
{
    /// <summary>The stable action identity; equality with any clip ID is not required.</summary>
    public required string Id { get; init; }
    /// <summary>The action title.</summary>
    public string Title { get; init; } = "";
    /// <summary>The intended screen event.</summary>
    public string Description { get; init; } = "";
    /// <summary>The planned duration, separate from media length and montage duration.</summary>
    public int? PlannedDurationFrames { get; init; }
    /// <summary>Reusable candidate sources; an empty collection is valid.</summary>
    public ActionSourceContract[] Sources { get; init; } = [];
    /// <summary>The explicit planned pause before the action.</summary>
    public int GapBeforeFrames { get; init; }
    /// <summary>The optional planned anchor.</summary>
    public int? AnchorFrame { get; init; }
    /// <summary>Whether the planned gap is protected.</summary>
    public bool GapProtected { get; init; }
    /// <summary>Author's sound intention.</summary>
    public string? SoundNotes { get; init; }
    /// <summary>Author's continuity intention.</summary>
    public string? ContinuityNotes { get; init; }
}

/// <summary>The use of a reusable source by an action, without selecting a montage take.</summary>
public sealed record ActionSourceContract
{
    /// <summary>The referenced library source.</summary>
    public required string SourceId { get; init; }
    /// <summary>The optional purpose, for example a reaction or an insert.</summary>
    public string? Purpose { get; init; }
}
