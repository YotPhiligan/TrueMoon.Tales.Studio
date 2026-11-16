namespace TrueMoon.Tales.Studio.Persistence;

/// <summary>The baseline revision or exact file content no longer matches.</summary>
/// <param name="expected">The caller's revision.</param>
/// <param name="actual">The currently stored revision.</param>
public sealed class ProjectRevisionConflictException(long expected, long actual)
    : InvalidOperationException($"Project changed: expected revision {expected}, current revision {actual}.")
{
    /// <summary>The expected revision.</summary>
    public long ExpectedRevision { get; } = expected;
    /// <summary>The currently stored revision; may equal expected if only the fingerprint changed.</summary>
    public long ActualRevision { get; } = actual;
}

/// <summary>Legacy storage requires a separate explicit migration operation.</summary>
public sealed class ProjectMigrationRequiredException() : InvalidOperationException("Explicit schema 1 migration is required before saving.");

/// <summary>A durable pending commit requires an explicit recovery operation.</summary>
public sealed class ProjectRecoveryRequiredException() : IOException("A pending project transaction requires explicit recovery.");

/// <summary>A different writer owns the lease, or exclusive ownership could not be obtained.</summary>
/// <param name="inner">The platform lease error.</param>
public sealed class ProjectStoreBusyException(Exception inner) : IOException("Cannot obtain the exclusive project writer lease.", inner);

/// <summary>Pending transaction evidence no longer matches the project or its staged files.</summary>
public sealed class ProjectRecoveryConflictException() : IOException("Project recovery evidence conflicts with the current files; no replacement was performed.");
