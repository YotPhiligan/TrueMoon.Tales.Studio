namespace TrueMoon.Tales.Studio.Persistence;

/// <summary>Trusted host configuration; project JSON never grants filesystem access.</summary>
public sealed record ProjectStoreOptions
{
    /// <summary>The explicitly approved project directory to read.</summary>
    public required string ProjectDirectory { get; init; }
    /// <summary>An explicitly selected .studio filename within the project directory.</summary>
    public string? ProjectFileName { get; init; }
    /// <summary>Explicit directory grants for managed writes; empty means read-only.</summary>
    public string[] AllowedWriteRoots { get; init; } = [];
    /// <summary>Directories or files denied for both reads and writes, supplied by workspace policy.</summary>
    public string[] ProtectedRoots { get; init; } = [];
}
