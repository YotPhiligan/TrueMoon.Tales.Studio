using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;
using TrueMoon.Tales.Studio.Contracts.Projects;
using TrueMoon.Tales.Studio.Contracts.Serialization;

namespace TrueMoon.Tales.Studio.Persistence;

/// <summary>A scoped project writer with optimistic concurrency, immutable history and explicit recovery.</summary>
public sealed class ProjectStore
{
    /// <summary>Reserved prefix for incomplete publications. Catalogs ignore these directories.</summary>
    public const string CreationStagingPrefix = ".studio-create-";
    private const string FileName = "Studio.project.json";
    private const string JournalName = ".Studio.project.transaction.json";
    private const string LeaseName = ".Studio.project.lock";
    private static readonly JsonSerializerOptions JournalOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower,
        UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow
    };
    private readonly ProjectPathPolicy _paths;
    private readonly IAtomicFileReplacement _replacement;
    private readonly TimeProvider _time;

    /// <summary>Creates a store using local atomic replacement and the system clock.</summary>
    /// <param name="options">Trusted project location and explicit filesystem grants.</param>
    public ProjectStore(ProjectStoreOptions options) : this(options, new AtomicFileReplacement(), TimeProvider.System) { }

    /// <summary>Creates a store with explicit platform/clock boundaries.</summary>
    /// <param name="options">Trusted project location and explicit filesystem grants.</param>
    /// <param name="replacement">The atomic replacement implementation.</param>
    /// <param name="timeProvider">The clock for audit timestamps.</param>
    public ProjectStore(ProjectStoreOptions options, IAtomicFileReplacement replacement, TimeProvider timeProvider)
    {
        ArgumentNullException.ThrowIfNull(replacement);
        ArgumentNullException.ThrowIfNull(timeProvider);
        _paths = new(options);
        _replacement = replacement;
        _time = timeProvider;
    }

    /// <summary>Publishes a new empty schema 2 project as a child of this store's configured directory.</summary>
    /// <param name="directoryName">A portable directory name with no path components.</param>
    /// <param name="project">The initial project at revision one.</param>
    /// <param name="cancellationToken">Cancellation before the directory is published.</param>
    /// <returns>The initial document with its final file path and exact-byte fingerprint.</returns>
    public async Task<ProjectFileDocument> CreateChildAsync(string directoryName, ProjectContract project,
        CancellationToken cancellationToken = default)
    {
        ProjectContractJson.Validate(project);
        if (directoryName is not { Length: > 0 and <= 80 } ||
            !Regex.IsMatch(directoryName, @"\Aproject-[a-z0-9_-]+\z", RegexOptions.CultureInvariant))
            throw new ArgumentException("Invalid project directory name.", nameof(directoryName));
        if (project.Revision != 1 || project.Segments.Length != 0 || project.Sources.Length != 0 ||
            project.Montage.Windows.Length != 0 || project.Montage.Clips.Length != 0)
            throw new InvalidDataException("A new project must start empty at revision one.");
        cancellationToken.ThrowIfCancellationRequested();
        _paths.Check(_paths.Root, true);
        var target = _paths.At(directoryName);
        var file = _paths.At(directoryName, FileName);
        _paths.Check(target, true);
        if (Path.Exists(target)) throw new IOException("Каталог нового проекта уже занят.");
        var bytes = ProjectFileCodec.SerializeNew(project);
        var result = ProjectFileCodec.Read(bytes).AtPath(file);
        var staging = _paths.At(CreationStagingPrefix + Guid.NewGuid().ToString("N"));
        var pending = _paths.At(staging, FileName);
        _paths.CreateDirectory(staging);
        var created = new List<string>(1);
        var published = false;
        try
        {
            await WriteNewAsync(pending, bytes, cancellationToken, created).ConfigureAwait(false);
            cancellationToken.ThrowIfCancellationRequested();
            _paths.Check(target, true);
            if (Path.Exists(target)) throw new IOException("Каталог нового проекта уже занят.");
            _paths.Check(pending);
            Directory.Move(staging, target);
            published = true;
            return result;
        }
        finally
        {
            if (!published)
            {
                foreach (var path in created) DeleteOwnPending(path);
                _paths.Check(staging, true);
                Directory.Delete(staging);
            }
        }
    }

    /// <summary>Reads the unique current/legacy location without migrating, recovering or creating files.</summary>
    /// <param name="cancellationToken">Cancellation of the read.</param>
    /// <returns>A baseline for a later explicit write.</returns>
    public async Task<ProjectFileDocument> OpenAsync(CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        RequireNoPendingCommit();
        var path = Locate();
        var bytes = await ReadBytesAsync(path, cancellationToken).ConfigureAwait(false);
        // A writer may have published its intent during this read; do not expose uncertain state.
        RequireNoPendingCommit();
        return ProjectFileCodec.Read(bytes).AtPath(path);
    }

    /// <summary>Checks path grants for a writer session and project commits without claiming a lease or writing.</summary>
    public void VerifyWriteAccess()
    {
        var file = Locate();
        CheckWriteAccess(file);
        // Atomic publication stages project bytes beside the document and the journal in the project root.
        _paths.Check(Path.GetDirectoryName(file)!, true);
        _paths.Check(_paths.Root, true);
        ProjectWriterLease.CheckAccess(_paths);
    }

    /// <summary>Reports whether both supported project document locations are present.</summary>
    /// <returns>Whether opening requires resolving the ambiguity outside the store.</returns>
    public bool HasLocationConflict()
    {
        var locations = FindLocations();
        return locations.HasRoot && locations.HasLegacy;
    }

    /// <summary>Explicitly migrates schema 1, with one revision advance and an exact-byte history snapshot.</summary>
    /// <param name="baseline">A document opened by this project location.</param>
    /// <param name="expectedRevision">The revision that the caller observed.</param>
    /// <param name="actor">The author of this operation.</param>
    /// <param name="cancellationToken">Cancellation before durable commit intent.</param>
    /// <returns>The committed schema 2 baseline.</returns>
    public Task<ProjectFileDocument> MigrateAsync(ProjectFileDocument baseline, long expectedRevision, string actor,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(baseline);
        if (baseline.SchemaVersion != 1) throw new InvalidOperationException("The project is already migrated.");
        return CommitAsync(baseline, null, expectedRevision, actor, cancellationToken);
    }

    /// <summary>Saves a schema 2 draft; stale revisions/content never overwrite the current file.</summary>
    /// <param name="baseline">The opened document and exact-content token.</param>
    /// <param name="project">The desired state at the expected revision.</param>
    /// <param name="expectedRevision">The revision that the caller observed.</param>
    /// <param name="actor">The author of this operation.</param>
    /// <param name="cancellationToken">Cancellation before durable commit intent.</param>
    /// <returns>The committed baseline with revision advanced by one.</returns>
    public Task<ProjectFileDocument> SaveAsync(ProjectFileDocument baseline, ProjectContract project, long expectedRevision,
        string actor, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(baseline);
        ArgumentNullException.ThrowIfNull(project);
        if (baseline.SchemaVersion != 2) throw new ProjectMigrationRequiredException();
        // Freeze caller-owned arrays/metadata before the first await.
        var frozen = ProjectContractJson.Deserialize(ProjectContractJson.Serialize(project));
        if (frozen.Revision != expectedRevision) throw new InvalidOperationException("Draft revision differs from expected_revision.");
        return CommitAsync(baseline, frozen, expectedRevision, actor, cancellationToken);
    }

    /// <summary>Restores a previously observed snapshot as a new revision of the same project.</summary>
    /// <param name="baseline">Current opened project and exact-content token.</param>
    /// <param name="snapshot">Historical snapshot owned by the writer at the same project location.</param>
    /// <param name="expectedRevision">Revision of the current baseline.</param>
    /// <param name="actor">Author of the new history entry.</param>
    /// <param name="cancellationToken">Cancellation before durable commit intent.</param>
    /// <returns>The new revision with the historical document content.</returns>
    public Task<ProjectFileDocument> RestoreSnapshotAsync(ProjectFileDocument baseline, ProjectFileDocument snapshot,
        long expectedRevision, string actor, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(baseline);
        ArgumentNullException.ThrowIfNull(snapshot);
        if (baseline.SchemaVersion != 2 || snapshot.SchemaVersion != 2) throw new ProjectMigrationRequiredException();
        if (snapshot.FilePath is null || !string.Equals(baseline.FilePath, snapshot.FilePath, ProjectPathPolicy.Comparison) ||
            baseline.Project.Id != snapshot.Project.Id)
            throw new InvalidOperationException("Snapshot belongs to another project.");
        // Decode the owned envelope, not caller-mutable arrays in the detached DTO.
        var frozen = ProjectFileCodec.Read(snapshot.OriginalDocument.GetRawText());
        return CommitAsync(baseline, frozen.Project, expectedRevision, actor, cancellationToken, frozen);
    }

    /// <summary>Explicitly completes a published commit, or recognizes an already installed replacement.</summary>
    /// <param name="cancellationToken">Cancellation before exclusive recovery starts.</param>
    /// <returns>The recovered baseline; unrelated content is never replaced.</returns>
    public Task<ProjectFileDocument> RecoverAsync(CancellationToken cancellationToken = default) =>
        RecoverCoreAsync(null, cancellationToken);

    /// <summary>Reads the fingerprint of the pending intent without taking a lease or recovering it.</summary>
    /// <param name="token">Cancellation of the read.</param>
    /// <returns>The stored project identification and SHA256 of the exact pending intent.</returns>
    public async Task<(string Id, string Name, string Fingerprint)> ReadRecoveryIdentityAsync(CancellationToken token = default)
    {
        var journal = await ReadBytesAsync(_paths.At(JournalName), token).ConfigureAwait(false);
        var current = ProjectFileCodec.Read(await ReadBytesAsync(Locate(), token).ConfigureAwait(false));
        return (current.Project.Id, current.Project.Name, Hash(journal));
    }

    /// <summary>Recovers only the pending intent that the caller observed.</summary>
    /// <param name="expectedFingerprint">The observed SHA256 of the pending journal.</param>
    /// <param name="token">Cancellation before exclusive recovery starts.</param>
    /// <returns>The recovered baseline.</returns>
    public Task<ProjectFileDocument> RecoverObservedAsync(string expectedFingerprint, CancellationToken token = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(expectedFingerprint);
        return RecoverCoreAsync(expectedFingerprint, token);
    }

    private async Task<ProjectFileDocument> RecoverCoreAsync(string? expectedFingerprint, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var journalPath = _paths.At(JournalName);
        if (!File.Exists(journalPath))
        {
            if (expectedFingerprint is not null) throw new ProjectRecoveryConflictException();
            return await OpenAsync(cancellationToken).ConfigureAwait(false);
        }
        CheckWriteAccess(Locate());
        using var lease = AcquireLease();
        cancellationToken.ThrowIfCancellationRequested();
        var bytes = await ReadBytesAsync(journalPath, cancellationToken).ConfigureAwait(false);
        if (expectedFingerprint is not null && !string.Equals(Hash(bytes), expectedFingerprint, StringComparison.OrdinalIgnoreCase))
            throw new ProjectRecoveryConflictException();
        var journal = JsonSerializer.Deserialize<Journal>(bytes, JournalOptions) ?? throw new JsonException("Missing commit journal.");
        ValidateJournal(journal);
        var path = ProjectPath(journal);
        if (!string.Equals(path, Locate(), ProjectPathPolicy.Comparison)) throw new ProjectRecoveryConflictException();
        // Once recovery owns a durable intent, finish it independently of transport cancellation.
        return await FinishAsync(journal, bytes).ConfigureAwait(false);
    }

    private async Task<ProjectFileDocument> CommitAsync(ProjectFileDocument baseline, ProjectContract? draft,
        long expected, string actor, CancellationToken cancellationToken, ProjectFileDocument? restore = null)
    {
        cancellationToken.ThrowIfCancellationRequested();
        ArgumentException.ThrowIfNullOrWhiteSpace(actor);
        if (actor.Length > 160) throw new ArgumentException("Actor is too long.", nameof(actor));
        if (expected < 0 || expected == long.MaxValue) throw new ArgumentOutOfRangeException(nameof(expected));
        RequireNoPendingCommit();
        var path = Locate();
        if (baseline.FilePath is null || !string.Equals(path, baseline.FilePath, ProjectPathPolicy.Comparison))
            throw new InvalidOperationException("Baseline belongs to another project location.");
        CheckWriteAccess(path);
        using var lease = AcquireLease();
        RequireNoPendingCommit();
        var previousBytes = await ReadBytesAsync(path, cancellationToken).ConfigureAwait(false);
        var current = ProjectFileCodec.Read(previousBytes);
        if (current.Project.Revision != expected || baseline.Project.Revision != expected || baseline.Fingerprint != current.Fingerprint)
            throw new ProjectRevisionConflictException(expected, current.Project.Revision);
        if (current.SchemaVersion != baseline.SchemaVersion || current.Project.Id != baseline.Project.Id)
            throw new ProjectRevisionConflictException(expected, current.Project.Revision);
        var next = (draft ?? current.Project) with { Revision = expected + 1 };
        var encoded = draft is null ? ProjectFileCodec.SerializeMigration(current, next) : ProjectFileCodec.Serialize(restore ?? current, next);
        var transactionId = Guid.NewGuid().ToString("N");
        var envelope = JsonNode.Parse(encoded)!.AsObject();
        var audit = envelope["last_write"] switch
        {
            JsonObject existingAudit => existingAudit,
            null => new JsonObject(),
            _ => throw new JsonException("Stored last_write must be an object.")
        };
        audit["actor"] = actor;
        audit["at"] = _time.GetUtcNow().ToString("O");
        audit["operation"] = restore is not null ? "restore" : draft is null ? "migrate" : "save";
        audit["revision"] = next.Revision;
        audit["transaction_id"] = transactionId;
        if (envelope["last_write"] is null) envelope["last_write"] = audit;
        encoded = JsonSerializer.SerializeToUtf8Bytes(envelope);
        var journal = new Journal(1, path == _paths.At(FileName) ? FileName : "Production/" + FileName,
            transactionId, current.Project.Id, expected, current.Fingerprint, Hash(encoded));
        var projectTemp = ProjectTemp(journal);
        var historyTemp = HistoryTemp(journal);
        var journalTemp = _paths.At($".Studio.project.{transactionId}.journal.pending");
        var published = false;
        var created = new List<string>();
        try
        {
            _paths.CreateDirectory(_paths.At("Production", "History"));
            await WriteNewAsync(projectTemp, encoded, cancellationToken, created).ConfigureAwait(false);
            await WriteNewAsync(historyTemp, previousBytes, cancellationToken, created).ConfigureAwait(false);
            var journalBytes = JsonSerializer.SerializeToUtf8Bytes(journal, JournalOptions);
            await WriteNewAsync(journalTemp, journalBytes, cancellationToken, created).ConfigureAwait(false);
            if (Hash(await ReadBytesAsync(path, cancellationToken).ConfigureAwait(false)) != current.Fingerprint)
                throw new ProjectRevisionConflictException(expected, current.Project.Revision);
            cancellationToken.ThrowIfCancellationRequested();
            _paths.Check(_paths.At(JournalName), true);
            File.Move(journalTemp, _paths.At(JournalName), false);
            published = true;
            return await FinishAsync(journal, journalBytes).ConfigureAwait(false);
        }
        finally
        {
            if (!published)
            {
                foreach (var owned in created) DeleteOwnPending(owned);
            }
        }
    }

    private async Task<ProjectFileDocument> FinishAsync(Journal journal, byte[] journalBytes)
    {
        ValidateJournal(journal);
        var path = ProjectPath(journal);
        var currentBytes = await ReadBytesAsync(path, CancellationToken.None).ConfigureAwait(false);
        var currentHash = Hash(currentBytes);
        if (currentHash != journal.OldHash && currentHash != journal.NewHash) throw new ProjectRecoveryConflictException();
        var projectTemp = ProjectTemp(journal);
        if (currentHash == journal.OldHash && !File.Exists(projectTemp)) throw new ProjectRecoveryConflictException();
        var nextBytes = currentHash == journal.NewHash ? currentBytes : await ReadBytesAsync(projectTemp, CancellationToken.None).ConfigureAwait(false);
        if (Hash(nextBytes) != journal.NewHash) throw new ProjectRecoveryConflictException();
        var next = ProjectFileCodec.Read(nextBytes);
        if (next.SchemaVersion != 2 || next.Project.Id != journal.ProjectId || next.Project.Revision != journal.PreviousRevision + 1)
            throw new ProjectRecoveryConflictException();
        var history = HistoryPath(journal);
        var historyTemp = HistoryTemp(journal);
        var evidence = File.Exists(history) ? history : historyTemp;
        if (!File.Exists(evidence)) throw new ProjectRecoveryConflictException();
        if (Hash(await ReadBytesAsync(evidence, CancellationToken.None).ConfigureAwait(false)) != journal.OldHash)
            throw new ProjectRecoveryConflictException();
        foreach (var (pending, hash) in new[] { (projectTemp, journal.NewHash), (historyTemp, journal.OldHash) })
            if (File.Exists(pending) && Hash(await ReadBytesAsync(pending, CancellationToken.None).ConfigureAwait(false)) != hash)
                throw new ProjectRecoveryConflictException();
        if (!File.Exists(history))
        {
            _paths.Check(history, true);
            _paths.Check(historyTemp, true);
            File.Move(historyTemp, history, false);
        }
        if (currentHash == journal.OldHash)
        {
            if (Hash(await ReadBytesAsync(path, CancellationToken.None).ConfigureAwait(false)) != journal.OldHash)
                throw new ProjectRecoveryConflictException();
            _paths.Check(projectTemp, true);
            _paths.Check(path, true);
            _replacement.Replace(projectTemp, path);
        }
        if (Hash(await ReadBytesAsync(path, CancellationToken.None).ConfigureAwait(false)) != journal.NewHash ||
            Hash(await ReadBytesAsync(_paths.At(JournalName), CancellationToken.None).ConfigureAwait(false)) != Hash(journalBytes))
            throw new ProjectRecoveryConflictException();
        DeleteOwnPending(projectTemp);
        DeleteOwnPending(historyTemp);
        _paths.Check(_paths.At(JournalName), true);
        File.Delete(_paths.At(JournalName));
        return next.AtPath(path);
    }

    private string Locate()
    {
        var (root, legacy, hasRoot, hasLegacy) = FindLocations();
        if (hasRoot && hasLegacy) throw new InvalidDataException("Both root and Production project files exist.");
        return hasRoot ? root : hasLegacy ? legacy : throw new FileNotFoundException("Project file was not found.", root);
    }

    private (string Root, string Legacy, bool HasRoot, bool HasLegacy) FindLocations()
    {
        var root = _paths.At(FileName);
        var legacy = _paths.At("Production", FileName);
        return (root, legacy, File.Exists(root), File.Exists(legacy));
    }

    private void RequireNoPendingCommit()
    {
        if (File.Exists(_paths.At(JournalName))) throw new ProjectRecoveryRequiredException();
    }
    private void CheckWriteAccess(string projectPath)
    {
        foreach (var path in new[] { projectPath, _paths.At(LeaseName), _paths.At(JournalName), _paths.At("Production", "History") }) _paths.Check(path, true);
    }
    private FileStream AcquireLease()
    {
        var path = _paths.At(LeaseName);
        _paths.Check(path, true);
        try { return new FileStream(path, FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None); }
        catch (IOException error) { throw new ProjectStoreBusyException(error); }
    }
    private async Task<byte[]> ReadBytesAsync(string path, CancellationToken token)
    {
        _paths.Check(path);
        await using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read | FileShare.Delete,
            4096, FileOptions.Asynchronous | FileOptions.SequentialScan);
        using var result = new MemoryStream();
        await stream.CopyToAsync(result, token).ConfigureAwait(false);
        return result.ToArray();
    }
    private async Task WriteNewAsync(string path, byte[] bytes, CancellationToken token, ICollection<string> created)
    {
        _paths.Check(path, true);
        await using var stream = new FileStream(path, FileMode.CreateNew, FileAccess.Write, FileShare.None,
            4096, FileOptions.Asynchronous | FileOptions.WriteThrough);
        created.Add(path);
        await stream.WriteAsync(bytes, token).ConfigureAwait(false);
        await stream.FlushAsync(token).ConfigureAwait(false);
        stream.Flush(flushToDisk: true);
    }
    private void DeleteOwnPending(string path)
    {
        _paths.Check(path, true);
        File.Delete(path);
    }
    private string ProjectPath(Journal journal) => journal.ProjectFile == FileName ? _paths.At(FileName) : _paths.At("Production", FileName);
    private string ProjectTemp(Journal journal) => Path.Combine(Path.GetDirectoryName(ProjectPath(journal))!, $".Studio.project.{journal.TransactionId}.pending");
    private string HistoryPath(Journal journal) => _paths.At("Production", "History", $"r{journal.PreviousRevision:D6}-{journal.TransactionId}.json");
    private string HistoryTemp(Journal journal) => _paths.At("Production", "History", $".r{journal.PreviousRevision:D6}-{journal.TransactionId}.pending");
    private static string Hash(byte[] bytes) => Convert.ToHexString(SHA256.HashData(bytes));
    private static void ValidateJournal(Journal journal)
    {
        if (journal.Version != 1 || journal.ProjectFile is not (FileName or "Production/Studio.project.json") ||
            !Regex.IsMatch(journal.TransactionId ?? "", "\\A[0-9a-f]{32}\\z") || string.IsNullOrWhiteSpace(journal.ProjectId) ||
            journal.PreviousRevision < 0 || journal.PreviousRevision == long.MaxValue ||
            !Regex.IsMatch(journal.OldHash ?? "", "\\A[0-9A-F]{64}\\z") || !Regex.IsMatch(journal.NewHash ?? "", "\\A[0-9A-F]{64}\\z") || journal.OldHash == journal.NewHash)
            throw new InvalidDataException("Invalid project transaction journal.");
    }
    private sealed record Journal(int Version, string ProjectFile, string TransactionId, string ProjectId,
        long PreviousRevision, string OldHash, string NewHash);
}
