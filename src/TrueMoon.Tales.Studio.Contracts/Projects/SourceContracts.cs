using System.Text.Json;

namespace TrueMoon.Tales.Studio.Contracts.Projects;

/// <summary>A reusable generation setup or imported material.</summary>
public sealed record SourceContract
{
    /// <summary>The original library identifier.</summary>
    public required string Id { get; init; }
    /// <summary>The production label.</summary>
    public string Label { get; init; } = "";
    /// <summary>The display title.</summary>
    public string Title { get; init; } = "";
    /// <summary>The available variants; take identities are scoped to this source.</summary>
    public TakeContract[] Takes { get; init; } = [];
    /// <summary>Source-specific preparation/import data, including prompt, references and settings.</summary>
    public IReadOnlyDictionary<string, JsonElement> Properties { get; init; } = new Dictionary<string, JsonElement>();
}

/// <summary>A material variant with retained provenance and metadata.</summary>
public sealed record TakeContract
{
    /// <summary>The identifier within the owning source.</summary>
    public required string Id { get; init; }
    /// <summary>The optional video asset; no file is opened by this contract.</summary>
    public string? VideoAssetId { get; init; }
    /// <summary>Unmapped variant data, including immutable generation metadata.</summary>
    public IReadOnlyDictionary<string, JsonElement> Properties { get; init; } = new Dictionary<string, JsonElement>();
}
