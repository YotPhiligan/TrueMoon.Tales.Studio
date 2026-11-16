namespace TrueMoon.Tales.Studio.Persistence;

/// <summary>The platform boundary for an atomic replacement of an existing local file.</summary>
public interface IAtomicFileReplacement
{
    /// <summary>Atomically replaces an existing file with a staged file on the same volume.</summary>
    /// <param name="stagedPath">The fully written sibling file.</param>
    /// <param name="projectPath">The existing destination.</param>
    void Replace(string stagedPath, string projectPath);
}

/// <summary>Local filesystem replacement using File.Replace, with no truncate/copy fallback.</summary>
public sealed class AtomicFileReplacement : IAtomicFileReplacement
{
    /// <inheritdoc />
    public void Replace(string stagedPath, string projectPath) => File.Replace(stagedPath, projectPath, null);
}
