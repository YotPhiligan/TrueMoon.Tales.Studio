using System.Text.Json;
using System.Text.Json.Nodes;
using TrueMoon.Mithril;
using TrueMoon.Tales.Studio.Backend;
using TrueMoon.Tales.Studio.Contracts.Serialization;
using TrueMoon.Tales.Studio.Persistence;
using Xunit;

namespace TrueMoon.Tales.Studio.Tests.Backend;

public sealed class BackendWriteTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Recovery_IsExplicit_AndFinishesDurableIntentOnceWithoutReplayingMove(bool installed)
    {
        using var fixture = new BackendWriteFixture();
        var store = fixture.Files.Store(replacement: new FailReplacement(installed));
        var baseline = await store.OpenAsync();
        await Assert.ThrowsAsync<IOException>(() => store.MigrateAsync(baseline, 11, "failure-fixture"));
        using var service = fixture.Service();
        Assert.Equal("recovery_required", (await service.OpenProjectAsync(fixture.Options.SessionKey)).ErrorCode);
        var recovered = await service.RecoverProjectAsync(fixture.Options.SessionKey);
        Assert.Empty(recovered.ErrorCode); Assert.Equal(12, ProjectContractJson.Deserialize(recovered.ProjectJson).Revision);
        var bytes = File.ReadAllBytes(fixture.Files.ProjectPath);
        var repeated = await service.RecoverProjectAsync(fixture.Options.SessionKey);
        Assert.Empty(repeated.ErrorCode); Assert.Equal(recovered.Fingerprint, repeated.Fingerprint);
        Assert.Equal(bytes, File.ReadAllBytes(fixture.Files.ProjectPath)); Assert.Single(fixture.Files.HistoryFiles);
        Assert.False(File.Exists(fixture.Files.JournalPath));
    }

    private sealed class FailReplacement(bool installed) : IAtomicFileReplacement
    {
        public void Replace(string source, string destination)
        {
            if (installed) new AtomicFileReplacement().Replace(source, destination);
            throw new IOException("simulated failed replacement");
        }
    }

    [Fact]
    public async Task Handoff_IsExplicit_MigrationAndMovePreserveExactHistory_AndNoopDoesNotWrite()
    {
        using var fixture = new BackendWriteFixture();
        using var service = fixture.Service();
        var key = fixture.Options.SessionKey;
        var original = File.ReadAllBytes(fixture.Files.ProjectPath);
        var opened = await service.OpenProjectAsync(key);
        Assert.Empty(fixture.Files.HistoryFiles);
        Assert.False(File.Exists(Path.Combine(fixture.Files.ProjectDirectory, ".Studio.project.writer.lock")));
        Assert.Equal("writer_required", (await service.MoveClipAsync(key, "clip-main", 14, 11, opened.Fingerprint)).ErrorCode);
        Assert.Empty((await service.AcquireWriterAsync(key, 11, opened.Fingerprint)).ErrorCode);
        Assert.Equal(original, File.ReadAllBytes(fixture.Files.ProjectPath));
        Assert.Equal("migration_required", (await service.MoveClipAsync(key, "clip-main", 14, 11, opened.Fingerprint)).ErrorCode);
        var migrated = await service.MigrateProjectAsync(key, 11, opened.Fingerprint);
        Assert.Empty(migrated.ErrorCode); Assert.Equal(2, migrated.SchemaVersion);
        Assert.Equal(original, File.ReadAllBytes(Assert.Single(fixture.Files.HistoryFiles)));
        var previous = File.ReadAllBytes(fixture.Files.ProjectPath);
        var before = ProjectContractJson.Deserialize(migrated.ProjectJson);
        var moved = await service.MoveClipAsync(key, "clip-main", 14, 12, migrated.Fingerprint);
        Assert.Empty(moved.ErrorCode);
        var after = ProjectContractJson.Deserialize(moved.ProjectJson);
        var expected = before with { Revision = 13, Montage = before.Montage with { Clips = before.Montage.Clips
            .Select(row => row.Id == "clip-main" ? row with { StartFrame = 14 } : row).ToArray() } };
        Assert.Equal(ProjectContractJson.Serialize(expected), ProjectContractJson.Serialize(after));
        Assert.Equal(2, fixture.Files.HistoryFiles.Length);
        Assert.Contains(fixture.Files.HistoryFiles, path => File.ReadAllBytes(path).SequenceEqual(previous));
        using var storage = JsonDocument.Parse(File.ReadAllBytes(fixture.Files.ProjectPath));
        Assert.Equal(9007199254740993L, storage.RootElement.GetProperty("legacy_document").GetProperty("opaque_root").GetProperty("precise_integer").GetInt64());
        var savedBytes = File.ReadAllBytes(fixture.Files.ProjectPath);
        var noop = await service.MoveClipAsync(key, "clip-main", 14, 13, moved.Fingerprint);
        Assert.Equal(moved.Fingerprint, noop.Fingerprint); Assert.Empty(noop.ErrorCode);
        Assert.Equal(savedBytes, File.ReadAllBytes(fixture.Files.ProjectPath)); Assert.Equal(2, fixture.Files.HistoryFiles.Length);
    }

    [Theory]
    [InlineData("revision")]
    [InlineData("bytes")]
    [InlineData("unknown")]
    [InlineData("before")]
    [InlineData("after")]
    public async Task Move_RejectsStaleOrInvalidRequestsWithoutWriting(string mode)
    {
        using var fixture = new BackendWriteFixture(); using var service = fixture.Service();
        var migrated = await fixture.MigrateAsync(service);
        if (mode == "bytes") File.AppendAllText(fixture.Files.ProjectPath, " ");
        var before = File.ReadAllBytes(fixture.Files.ProjectPath);
        var reply = await service.MoveClipAsync(fixture.Options.SessionKey, mode == "unknown" ? "absent" : "clip-main",
            mode == "before" ? -1 : mode == "after" ? 217 : 14, mode == "revision" ? 11 : 12, migrated.Fingerprint);
        Assert.Equal(mode is "revision" or "bytes" ? "revision_conflict" : "invalid_move", reply.ErrorCode);
        Assert.Equal(before, File.ReadAllBytes(fixture.Files.ProjectPath)); Assert.Single(fixture.Files.HistoryFiles);
    }

    [Fact]
    public async Task Writers_AreExclusive_UntilOwnerDisposes_AndReadOnlyCannotClaim()
    {
        using var fixture = new BackendWriteFixture();
        using var first = fixture.Service(); using var second = fixture.Service();
        var opened = await first.OpenProjectAsync(fixture.Options.SessionKey);
        Assert.Empty((await first.AcquireWriterAsync(fixture.Options.SessionKey, 11, opened.Fingerprint)).ErrorCode);
        Assert.Equal("writer_busy", (await second.AcquireWriterAsync(fixture.Options.SessionKey, 11, opened.Fingerprint)).ErrorCode);
        using var reader = new StudioBackendService(fixture.Options with { WriteEnabled = false }, fixture.Files.Store(false), new UnitLaunchSettings(1, 1, "read"));
        Assert.Equal("write_not_enabled", (await reader.AcquireWriterAsync(fixture.Options.SessionKey, 11, opened.Fingerprint)).ErrorCode);
        first.Dispose();
        Assert.Empty((await second.AcquireWriterAsync(fixture.Options.SessionKey, 11, opened.Fingerprint)).ErrorCode);
        Assert.Empty(fixture.Files.HistoryFiles);
    }

    [Fact]
    public async Task Acquire_RejectsStaleBaselineAndMissingGrants_AndDoesNotKeepFailedClaim()
    {
        using var fixture = new BackendWriteFixture(); using var service = fixture.Service();
        var opened = await service.OpenProjectAsync(fixture.Options.SessionKey);
        Assert.Equal("revision_conflict", (await service.AcquireWriterAsync(fixture.Options.SessionKey, 10, opened.Fingerprint)).ErrorCode);
        using (ProjectWriterLease.Acquire(BackendProjectAccess.Read(fixture.Options))) { }
        var config = JsonNode.Parse(File.ReadAllText(fixture.Base.ConfigPath))!;
        config["project_write_roots"] = new JsonArray(); File.WriteAllText(fixture.Base.ConfigPath, config.ToJsonString());
        Assert.Equal("access_denied", (await service.AcquireWriterAsync(fixture.Options.SessionKey, 11, opened.Fingerprint)).ErrorCode);
        Assert.Empty(fixture.Files.HistoryFiles);
        await Assert.ThrowsAsync<UnauthorizedAccessException>(() => service.MoveClipAsync("wrong", "clip-main", 14, 11, opened.Fingerprint));
    }
}
