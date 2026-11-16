using System.Text.Json;
using System.Text.Json.Serialization;

namespace TrueMoon.Tales.Studio.Contracts.Projects;

/// <summary>A versioned, transport-neutral snapshot of story intentions and saved montage.</summary>
public sealed record ProjectContract
{
    /// <summary>The DTO version, independent of the legacy project's schema_version.</summary>
    [JsonPropertyName("contract_version")]
    public int Version { get; init; } = 1;
    /// <summary>The stable project identifier.</summary>
    public required string Id { get; init; }
    /// <summary>The display name.</summary>
    public string Name { get; init; } = "";
    /// <summary>The revision observed when this snapshot was read.</summary>
    public long Revision { get; init; }
    /// <summary>The project time base in frames per second.</summary>
    public double Fps { get; init; } = 24;
    /// <summary>Ordered semantic segments, including unplaced or conflicting intentions.</summary>
    public ScenarioSegmentContract[] Segments { get; init; } = [];
    /// <summary>Reusable library sources, including unused sources.</summary>
    public SourceContract[] Sources { get; init; } = [];
    /// <summary>The separately saved, last-valid montage.</summary>
    public MontageContract Montage { get; init; } = new();
    /// <summary>Unmapped project metadata; paths are retained as data, never resolved.</summary>
    public IReadOnlyDictionary<string, JsonElement> Properties { get; init; } = new Dictionary<string, JsonElement>();
}
