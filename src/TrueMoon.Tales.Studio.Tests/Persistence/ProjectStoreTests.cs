using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using TrueMoon.Tales.Studio.Persistence;
using Xunit;

namespace TrueMoon.Tales.Studio.Tests.Persistence;

public sealed class ProjectStoreTests
{
    [Fact]
    public async Task Open_LegacyProject_IsReadOnlyAndSaveRequiresExplicitMigration()
    {
        using var fixture = new PersistenceFixture();
        var before = Directory.GetFileSystemEntries(fixture.ProjectDirectory).Order().ToArray();
        var store = fixture.Store();

        var baseline = await store.OpenAsync();

        Assert.Equal(1, baseline.SchemaVersion);
        Assert.Equal(11, baseline.Project.Revision);
        Assert.Equal(fixture.ProjectPath, baseline.FilePath);
        Assert.Equal(fixture.InitialBytes, await File.ReadAllBytesAsync(fixture.ProjectPath));
        Assert.Equal(before, Directory.GetFileSystemEntries(fixture.ProjectDirectory).Order());
        Assert.Empty(fixture.HistoryFiles);
        await Assert.ThrowsAsync<ProjectMigrationRequiredException>(() => store.SaveAsync(baseline, baseline.Project, 11, "author"));
        Assert.Equal(fixture.InitialBytes, await File.ReadAllBytesAsync(fixture.ProjectPath));
        Assert.False(File.Exists(fixture.JournalPath));
    }

    [Fact]
    public async Task ReadHistory_WithoutWriteGrant_VerifiesIdentityAndNeverUsesArchiveAsBaseline()
    {
        using var fixture = new PersistenceFixture();
        var writer = fixture.Store();
        var original = await writer.OpenAsync();
        var migrated = await writer.MigrateAsync(original, 11, "author");
        var current = await writer.SaveAsync(migrated, migrated.Project with { Name = "Current" }, 12, "author");
        var reader = fixture.Store(writable: false);
        var archive = await reader.ReadHistoryAsync(current.Project.Id, 12);

        Assert.Equal(migrated.Fingerprint, archive.Fingerprint);
        Assert.Null(archive.FilePath);
        await Assert.ThrowsAsync<InvalidDataException>(() => reader.ReadHistoryAsync("another-project", 12));
        await Assert.ThrowsAsync<FileNotFoundException>(() => reader.ReadHistoryAsync(current.Project.Id, 0));
        var duplicate = Path.Combine(fixture.HistoryDirectory, $"r000012-{Guid.NewGuid():N}.json");
        File.Copy(Assert.Single(fixture.HistoryFiles, row => Path.GetFileName(row).StartsWith("r000012-")), duplicate);
        try { await Assert.ThrowsAsync<InvalidDataException>(() => reader.ReadHistoryAsync(current.Project.Id, 12)); }
        finally { File.Delete(duplicate); }
        Assert.Equal(current.Fingerprint, (await reader.OpenAsync()).Fingerprint);
        Assert.Equal(2, fixture.HistoryFiles.Length);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Migrate_BothFileLayouts_PreserveExactLegacyBytesIdsAndAudit(bool legacyLayout)
    {
        using var fixture = new PersistenceFixture(legacyLayout);
        var now = new DateTimeOffset(2026, 11, 10, 3, 4, 5, TimeSpan.Zero);
        var store = fixture.Store(clock: new FixedClock(now));
        var baseline = await store.OpenAsync();

        var migrated = await store.MigrateAsync(baseline, 11, "agent:test");

        Assert.Equal(2, migrated.SchemaVersion);
        Assert.Equal(12, migrated.Project.Revision);
        Assert.Equal("project-synthetic", migrated.Project.Id);
        Assert.Equal(fixture.ProjectPath, migrated.FilePath);
        Assert.Equal("clip-main", migrated.Project.Montage.Clips[0].Id);
        Assert.Equal("clip-main", migrated.Project.Montage.Clips[0].ActionId);
        Assert.Equal(12, migrated.Project.Montage.Clips[0].StartFrame);
        Assert.Equal("Z:/not-an-allowed-write-root", migrated.Project.Properties["root"].GetString());
        var history = Assert.Single(fixture.HistoryFiles);
        Assert.StartsWith("r000011-", Path.GetFileName(history));
        Assert.Equal(fixture.InitialBytes, await File.ReadAllBytesAsync(history));
        Assert.True(JsonNode.DeepEquals(JsonNode.Parse(baseline.OriginalDocument.GetRawText()),
            JsonNode.Parse(migrated.OriginalDocument.GetProperty("legacy_document").GetRawText())));
        var audit = migrated.OriginalDocument.GetProperty("last_write");
        Assert.Equal("agent:test", audit.GetProperty("actor").GetString());
        Assert.Equal("migrate", audit.GetProperty("operation").GetString());
        Assert.Equal(now, audit.GetProperty("at").GetDateTimeOffset());
        Assert.False(File.Exists(fixture.JournalPath));
        var other = legacyLayout ? Path.Combine(fixture.ProjectDirectory, "Studio.project.json") : Path.Combine(fixture.ProjectDirectory, "Production", "Studio.project.json");
        Assert.False(File.Exists(other));
        Assert.Equal(migrated.Fingerprint, (await store.OpenAsync()).Fingerprint);
    }

    [Fact]
    public async Task Save_ReopenMultiSourceAction_PreservesOpaqueMetadataAndImmutableHistory()
    {
        using var fixture = new PersistenceFixture();
        var store = fixture.Store();
        var baseline = await store.OpenAsync();
        var migrated = await store.MigrateAsync(baseline, 11, "author");
        var firstHistory = Assert.Single(fixture.HistoryFiles);
        var firstHistoryBytes = await File.ReadAllBytesAsync(firstHistory);
        await File.WriteAllBytesAsync(fixture.ProjectPath, PersistenceFixture.Annotate(await File.ReadAllBytesAsync(fixture.ProjectPath)));
        var annotated = await store.OpenAsync();
        var previous = await File.ReadAllBytesAsync(fixture.ProjectPath);

        var saved = await store.SaveAsync(annotated, PersistenceFixture.Expanded(annotated.Project), 12, "agent:edit");
        var reopened = await fixture.Store().OpenAsync();

        Assert.Equal(13, reopened.Project.Revision);
        Assert.Equal(saved.Fingerprint, reopened.Fingerprint);
        Assert.Equal("Сборка A–B–A", reopened.Project.Name);
        var clips = reopened.Project.Montage.Clips.Where(c => c.ActionId == "clip-main").OrderBy(c => c.StartFrame).ToArray();
        Assert.Equal(["source-a", "source-b", "source-a"], clips.Select(c => c.SourceId));
        Assert.Equal(["clip-main", "clip-reaction", "clip-main-tail"], clips.Select(c => c.Id));
        Assert.Equal([12, 48, 84], clips.Select(c => c.StartFrame));
        var stored = reopened.OriginalDocument.GetProperty("project");
        Assert.Equal("envelope", reopened.OriginalDocument.GetProperty("opaque_envelope").GetString());
        Assert.Equal("source-a", PersistenceFixture.ById(stored.GetProperty("sources"), "source-a").GetProperty("opaque_source").GetString());
        var segment = PersistenceFixture.ById(stored.GetProperty("segments"), "segment-main");
        Assert.Equal("clip-main", PersistenceFixture.ById(segment.GetProperty("actions"), "clip-main").GetProperty("opaque_action").GetString());
        Assert.Equal(2, fixture.HistoryFiles.Length);
        Assert.Equal(firstHistoryBytes, await File.ReadAllBytesAsync(firstHistory));
        var nextHistory = fixture.HistoryFiles.Single(p => p != firstHistory);
        Assert.StartsWith("r000012-", Path.GetFileName(nextHistory));
        Assert.Equal(previous, await File.ReadAllBytesAsync(nextHistory));
        Assert.True(JsonNode.DeepEquals(JsonNode.Parse(migrated.OriginalDocument.GetProperty("legacy_document").GetRawText()),
            JsonNode.Parse(reopened.OriginalDocument.GetProperty("legacy_document").GetRawText())));
        Assert.Equal("save", reopened.OriginalDocument.GetProperty("last_write").GetProperty("operation").GetString());
        Assert.Equal("agent:edit", reopened.OriginalDocument.GetProperty("last_write").GetProperty("actor").GetString());
        Assert.Equal("audit", reopened.OriginalDocument.GetProperty("last_write").GetProperty("opaque_audit").GetString());
    }

    [Fact]
    public async Task Save_StaleRevision_DoesNotOverwriteProjectOrHistory()
    {
        using var fixture = new PersistenceFixture();
        var store = fixture.Store();
        var migrated = await store.MigrateAsync(await store.OpenAsync(), 11, "author");
        var saved = await store.SaveAsync(migrated, migrated.Project with { Name = "Первая правка" }, 12, "author");
        var bytes = await File.ReadAllBytesAsync(fixture.ProjectPath);
        var history = fixture.HistoryFiles.Order().ToArray();

        var conflict = await Assert.ThrowsAsync<ProjectRevisionConflictException>(() => store.SaveAsync(migrated, migrated.Project with { Name = "Устаревший черновик" }, 12, "agent"));

        Assert.Equal(12, conflict.ExpectedRevision);
        Assert.Equal(13, conflict.ActualRevision);
        Assert.Equal(saved.Fingerprint, (await store.OpenAsync()).Fingerprint);
        Assert.Equal(bytes, await File.ReadAllBytesAsync(fixture.ProjectPath));
        Assert.Equal(history, fixture.HistoryFiles.Order());
        Assert.False(File.Exists(fixture.JournalPath));
    }

    [Fact]
    public async Task Save_SameRevisionFingerprintTamper_DoesNotOverwriteExternalEdit()
    {
        using var fixture = new PersistenceFixture();
        var store = fixture.Store();
        var migrated = await store.MigrateAsync(await store.OpenAsync(), 11, "author");
        var bytes = Encoding.UTF8.GetBytes(Encoding.UTF8.GetString(await File.ReadAllBytesAsync(fixture.ProjectPath)) + "\n ");
        await File.WriteAllBytesAsync(fixture.ProjectPath, bytes);
        var history = fixture.HistoryFiles.Order().ToArray();

        var conflict = await Assert.ThrowsAsync<ProjectRevisionConflictException>(() => store.SaveAsync(migrated, migrated.Project with { Name = "Новая правка" }, 12, "agent"));

        Assert.Equal(12, conflict.ExpectedRevision);
        Assert.Equal(12, conflict.ActualRevision);
        Assert.Equal(bytes, await File.ReadAllBytesAsync(fixture.ProjectPath));
        Assert.Equal(history, fixture.HistoryFiles.Order());
        Assert.False(File.Exists(fixture.JournalPath));
    }

    [Fact]
    public async Task Save_ForeignBaselineAndChangedIdentity_AreRejectedBeforeCommit()
    {
        using var fixture = new PersistenceFixture();
        using var other = new PersistenceFixture();
        var store = fixture.Store();
        var foreign = await other.Store().OpenAsync();
        await Assert.ThrowsAsync<InvalidOperationException>(() => store.MigrateAsync(foreign, 11, "author"));
        var migrated = await store.MigrateAsync(await store.OpenAsync(), 11, "author");
        var bytes = await File.ReadAllBytesAsync(fixture.ProjectPath);
        var history = fixture.HistoryFiles.Order().ToArray();

        await Assert.ThrowsAsync<InvalidOperationException>(() => store.SaveAsync(migrated, migrated.Project with { Id = "different-project" }, 12, "author"));

        Assert.Equal(bytes, await File.ReadAllBytesAsync(fixture.ProjectPath));
        Assert.Equal(history, fixture.HistoryFiles.Order());
        Assert.Equal(other.InitialBytes, await File.ReadAllBytesAsync(other.ProjectPath));
        Assert.Empty(other.HistoryFiles);
    }

    [Fact]
    public async Task Open_AmbiguousFileLocations_AreRejectedWithoutMutation()
    {
        using var fixture = new PersistenceFixture();
        var otherPath = Path.Combine(fixture.ProjectDirectory, "Production", "Studio.project.json");
        Directory.CreateDirectory(Path.GetDirectoryName(otherPath)!);
        await File.WriteAllBytesAsync(otherPath, fixture.InitialBytes);

        await Assert.ThrowsAsync<InvalidDataException>(() => fixture.Store().OpenAsync());

        Assert.Equal(fixture.InitialBytes, await File.ReadAllBytesAsync(fixture.ProjectPath));
        Assert.Equal(fixture.InitialBytes, await File.ReadAllBytesAsync(otherPath));
        Assert.Empty(fixture.HistoryFiles);
    }

    [Theory]
    [InlineData("lookalike_grant")]
    [InlineData("protected_root")]
    [InlineData("protected_file")]
    [InlineData("protected_history")]
    public async Task PathPolicy_DeniesUnapprovedWritesAndProtectedPaths(string policy)
    {
        using var fixture = new PersistenceFixture();
        var options = new ProjectStoreOptions
        {
            ProjectDirectory = fixture.ProjectDirectory,
            AllowedWriteRoots = policy == "no_grant" ? [] : policy == "lookalike_grant" ? [fixture.ProjectDirectory + "-other"] : [fixture.ProjectDirectory],
            ProtectedRoots = policy switch
            {
                "protected_root" => [fixture.ProjectDirectory],
                "protected_file" => [fixture.ProjectPath],
                "protected_history" => [fixture.HistoryDirectory],
                _ => []
            }
        };

        await Assert.ThrowsAsync<UnauthorizedAccessException>(async () =>
        {
            var store = new ProjectStore(options);
            var baseline = await store.OpenAsync();
            await store.MigrateAsync(baseline, 11, "author");
        });

        Assert.Equal(fixture.InitialBytes, await File.ReadAllBytesAsync(fixture.ProjectPath));
        Assert.Empty(fixture.HistoryFiles);
        Assert.False(File.Exists(fixture.JournalPath));
    }

    [Fact]
    public async Task ReadOnlyStore_CanOpenButCannotMigrate()
    {
        using var fixture = new PersistenceFixture();
        var store = fixture.Store(writable: false);
        var baseline = await store.OpenAsync();

        await Assert.ThrowsAsync<UnauthorizedAccessException>(() => store.MigrateAsync(baseline, 11, "author"));

        Assert.Equal(1, baseline.SchemaVersion);
        Assert.Equal(fixture.InitialBytes, await File.ReadAllBytesAsync(fixture.ProjectPath));
        Assert.Empty(fixture.HistoryFiles);
        var migrated = await fixture.Store().MigrateAsync(baseline, 11, "author");
        var bytes = await File.ReadAllBytesAsync(fixture.ProjectPath);
        await Assert.ThrowsAsync<UnauthorizedAccessException>(() => store.SaveAsync(migrated, migrated.Project, 12, "author"));
        Assert.Equal(bytes, await File.ReadAllBytesAsync(fixture.ProjectPath));
        Assert.Single(fixture.HistoryFiles);
    }

    [Fact]
    public async Task PreCanceledOperations_DoNotWriteOrCreateHistory()
    {
        using var fixture = new PersistenceFixture();
        var store = fixture.Store();
        var baseline = await store.OpenAsync();
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => store.OpenAsync(cancellation.Token));
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => store.MigrateAsync(baseline, 11, "author", cancellation.Token));
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => store.RecoverAsync(cancellation.Token));
        Assert.Equal(fixture.InitialBytes, await File.ReadAllBytesAsync(fixture.ProjectPath));
        Assert.Empty(fixture.HistoryFiles);
        var migrated = await store.MigrateAsync(baseline, 11, "author");
        var bytes = await File.ReadAllBytesAsync(fixture.ProjectPath);
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => store.SaveAsync(migrated, migrated.Project, 12, "author", cancellation.Token));
        Assert.Equal(bytes, await File.ReadAllBytesAsync(fixture.ProjectPath));
        Assert.Single(fixture.HistoryFiles);
    }

    [Fact]
    public async Task CancellationAfterDurableIntent_CompletesCommitInsteadOfClaimingRollback()
    {
        using var fixture = new PersistenceFixture();
        using var cancellation = new CancellationTokenSource();
        var store = fixture.Store(replacement: new CancelReplacement(cancellation));
        var baseline = await store.OpenAsync();

        var saved = await store.MigrateAsync(baseline, 11, "author", cancellation.Token);

        Assert.True(cancellation.IsCancellationRequested);
        Assert.Equal(12, saved.Project.Revision);
        Assert.Equal(saved.Fingerprint, (await fixture.Store().OpenAsync()).Fingerprint);
        Assert.Equal(fixture.InitialBytes, await File.ReadAllBytesAsync(Assert.Single(fixture.HistoryFiles)));
        Assert.False(File.Exists(fixture.JournalPath));
    }

    [ReparsePointFact]
    public async Task PathPolicy_RejectsLinkedRootAndHistoryWithoutFollowingTargets()
    {
        using var fixture = new PersistenceFixture();
        var linkedRoot = Path.Combine(fixture.Root, "linked-project");
        Directory.CreateSymbolicLink(linkedRoot, fixture.ProjectDirectory);
        try
        {
            Assert.Throws<UnauthorizedAccessException>(() => new ProjectStore(new()
                { ProjectDirectory = linkedRoot, AllowedWriteRoots = [fixture.Root] }));
        }
        finally { Directory.Delete(linkedRoot); }
        var target = Path.Combine(fixture.Root, "history-target");
        Directory.CreateDirectory(target);
        var marker = Path.Combine(target, "preserve.txt");
        await File.WriteAllTextAsync(marker, "owned sentinel");
        Directory.CreateDirectory(Path.GetDirectoryName(fixture.HistoryDirectory)!);
        Directory.CreateSymbolicLink(fixture.HistoryDirectory, target);
        try
        {
            var store = fixture.Store();
            var baseline = await store.OpenAsync();
            await Assert.ThrowsAsync<UnauthorizedAccessException>(() => store.MigrateAsync(baseline, 11, "author"));
            Assert.Equal("owned sentinel", await File.ReadAllTextAsync(marker));
            Assert.Equal(fixture.InitialBytes, await File.ReadAllBytesAsync(fixture.ProjectPath));
        }
        finally { Directory.Delete(fixture.HistoryDirectory); }
    }

    [Fact]
    public async Task Commit_CancellationBeforeDurableIntent_CleansPendingAndReleasesLease()
    {
        using var fixture = new PersistenceFixture();
        using var cancellation = new CancellationTokenSource();
        var store = fixture.Store(clock: new CancelClock(cancellation));
        var baseline = await store.OpenAsync();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => store.MigrateAsync(baseline, 11, "author", cancellation.Token));

        Assert.Equal(fixture.InitialBytes, await File.ReadAllBytesAsync(fixture.ProjectPath));
        Assert.Empty(fixture.HistoryFiles);
        Assert.False(File.Exists(fixture.JournalPath));
        Assert.Empty(Directory.GetFiles(fixture.ProjectDirectory, "*.pending", SearchOption.AllDirectories));
        Assert.Equal(12, (await fixture.Store().MigrateAsync(baseline, 11, "author")).Project.Revision);
    }

    [Fact]
    public async Task ReplacementFailure_RequiresExplicitRecoveryAndRollsForwardOnce()
    {
        using var fixture = new PersistenceFixture();
        var store = fixture.Store(replacement: new FailReplacement(false));
        var baseline = await store.OpenAsync();

        await Assert.ThrowsAnyAsync<IOException>(() => store.MigrateAsync(baseline, 11, "author"));

        Assert.Equal(fixture.InitialBytes, await File.ReadAllBytesAsync(fixture.ProjectPath));
        Assert.True(File.Exists(fixture.JournalPath));
        await Assert.ThrowsAsync<ProjectRecoveryRequiredException>(() => fixture.Store().OpenAsync());
        await Assert.ThrowsAsync<UnauthorizedAccessException>(() => fixture.Store(writable: false).RecoverAsync());
        var recovered = await fixture.Store().RecoverAsync();
        Assert.Equal(2, recovered.SchemaVersion);
        Assert.Equal(12, recovered.Project.Revision);
        Assert.False(File.Exists(fixture.JournalPath));
        var history = Assert.Single(fixture.HistoryFiles);
        Assert.Equal(fixture.InitialBytes, await File.ReadAllBytesAsync(history));
        var repeated = await fixture.Store().RecoverAsync();
        Assert.Equal(recovered.Fingerprint, repeated.Fingerprint);
        Assert.Single(fixture.HistoryFiles);
    }

    [Fact]
    public async Task InstalledReplacementThenFailure_RecoveryDoesNotRepeatRevisionOrHistory()
    {
        using var fixture = new PersistenceFixture();
        var baseline = await fixture.Store().OpenAsync();
        await Assert.ThrowsAnyAsync<IOException>(() => fixture.Store(replacement: new FailReplacement(true)).MigrateAsync(baseline, 11, "author"));
        var installed = await File.ReadAllBytesAsync(fixture.ProjectPath);
        Assert.Equal(12, ProjectFileCodec.Read(installed).Project.Revision);
        Assert.True(File.Exists(fixture.JournalPath));

        var recovered = await fixture.Store().RecoverAsync();

        Assert.Equal(12, recovered.Project.Revision);
        Assert.Equal(installed, await File.ReadAllBytesAsync(fixture.ProjectPath));
        Assert.Equal(fixture.InitialBytes, await File.ReadAllBytesAsync(Assert.Single(fixture.HistoryFiles)));
        Assert.False(File.Exists(fixture.JournalPath));
        Assert.Equal(recovered.Fingerprint, (await fixture.Store().RecoverAsync()).Fingerprint);
        Assert.Single(fixture.HistoryFiles);
    }

    [Theory]
    [InlineData("project")]
    [InlineData("history")]
    [InlineData("staged")]
    [InlineData("journal_path")]
    public async Task Recovery_ExternalModificationOrUnsafeJournal_IsRefused(string changed)
    {
        using var fixture = new PersistenceFixture();
        var baseline = await fixture.Store().OpenAsync();
        await Assert.ThrowsAnyAsync<IOException>(() => fixture.Store(replacement: new FailReplacement(false)).MigrateAsync(baseline, 11, "author"));
        if (changed == "project") await File.AppendAllTextAsync(fixture.ProjectPath, "\n ");
        else if (changed == "history") await File.WriteAllBytesAsync(Assert.Single(fixture.HistoryFiles), [1, 2, 3]);
        else if (changed == "staged") await File.AppendAllTextAsync(Assert.Single(Directory.GetFiles(fixture.ProjectDirectory, ".Studio.project.*.pending")), "\n ");
        else
        {
            var journal = JsonNode.Parse(await File.ReadAllBytesAsync(fixture.JournalPath))!;
            journal["project_file"] = "../escaped.json";
            await File.WriteAllTextAsync(fixture.JournalPath, journal.ToJsonString());
        }
        var bytes = await File.ReadAllBytesAsync(fixture.ProjectPath);
        var history = await File.ReadAllBytesAsync(Assert.Single(fixture.HistoryFiles));

        if (changed == "journal_path") await Assert.ThrowsAsync<InvalidDataException>(() => fixture.Store().RecoverAsync());
        else await Assert.ThrowsAsync<ProjectRecoveryConflictException>(() => fixture.Store().RecoverAsync());

        Assert.Equal(bytes, await File.ReadAllBytesAsync(fixture.ProjectPath));
        Assert.Equal(history, await File.ReadAllBytesAsync(Assert.Single(fixture.HistoryFiles)));
        Assert.True(File.Exists(fixture.JournalPath));
        Assert.False(File.Exists(Path.Combine(fixture.Root, "escaped.json")));
    }

    [Fact]
    public async Task CrossInstanceLease_ProtectsOneWriterAndRejectsStaleLoser()
    {
        using var fixture = new PersistenceFixture();
        using var clock = new BlockingClock();
        var first = fixture.Store(clock: clock);
        var second = fixture.Store();
        var firstBaseline = await first.OpenAsync();
        var secondBaseline = await second.OpenAsync();
        var writing = Task.Run(() => first.MigrateAsync(firstBaseline, 11, "first"));
        ProjectFileDocument? winner = null;
        try
        {
            await clock.Entered.Task.WaitAsync(TimeSpan.FromSeconds(10));
            await Assert.ThrowsAsync<ProjectStoreBusyException>(() => second.MigrateAsync(secondBaseline, 11, "second"));
        }
        finally
        {
            clock.Release.Set();
            winner = await writing.WaitAsync(TimeSpan.FromSeconds(10));
        }

        Assert.Equal(12, winner.Project.Revision);
        Assert.Equal("first", winner.OriginalDocument.GetProperty("last_write").GetProperty("actor").GetString());
        var conflict = await Assert.ThrowsAsync<ProjectRevisionConflictException>(() => second.MigrateAsync(secondBaseline, 11, "second"));
        Assert.Equal(12, conflict.ActualRevision);
        Assert.Single(fixture.HistoryFiles);
    }

    private sealed class FixedClock(DateTimeOffset time) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => time;
    }

    private sealed class CancelClock(CancellationTokenSource cancellation) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow()
        {
            cancellation.Cancel();
            return DateTimeOffset.UnixEpoch;
        }
    }

    private sealed class BlockingClock : TimeProvider, IDisposable
    {
        internal TaskCompletionSource Entered { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        internal ManualResetEventSlim Release { get; } = new(false);
        public override DateTimeOffset GetUtcNow()
        {
            Entered.TrySetResult();
            if (!Release.Wait(TimeSpan.FromSeconds(15))) throw new TimeoutException("Test writer gate was not released.");
            return DateTimeOffset.UnixEpoch;
        }
        public void Dispose() => Release.Dispose();
    }

    private sealed class FailReplacement(bool installFirst) : IAtomicFileReplacement
    {
        public void Replace(string stagedPath, string projectPath)
        {
            if (installFirst) File.Replace(stagedPath, projectPath, null);
            throw new IOException("Injected replacement boundary failure.");
        }
    }

    private sealed class CancelReplacement(CancellationTokenSource cancellation) : IAtomicFileReplacement
    {
        public void Replace(string stagedPath, string projectPath)
        {
            cancellation.Cancel();
            File.Replace(stagedPath, projectPath, null);
        }
    }
}

public sealed class ReparsePointFactAttribute : FactAttribute
{
    public ReparsePointFactAttribute()
    {
        try
        {
            using var probe = new PersistenceFixture();
            var link = Path.Combine(probe.Root, "probe-link");
            Directory.CreateSymbolicLink(link, probe.ProjectDirectory);
            Directory.Delete(link);
        }
        catch (UnauthorizedAccessException) { Skip = "OS symbolic-link creation privilege is unavailable"; }
        catch (IOException error) when ((error.HResult & 0xffff) == 1314) { Skip = "OS symbolic-link creation privilege is unavailable (1314)"; }
        catch (PlatformNotSupportedException) { Skip = "OS symbolic-link creation is unsupported"; }
    }
}
