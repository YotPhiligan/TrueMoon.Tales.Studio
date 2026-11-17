using System.Security.Cryptography;
using TrueMoon.Tales.Studio.Backend;
using TrueMoon.Tales.Studio.Persistence;
using TrueMoon.Tales.Studio.Tests.Persistence;
using Xunit;

namespace TrueMoon.Tales.Studio.Tests.Backend;

public sealed class BackendMediaContractTests
{
    private const string Asset = "video-a";
    private static readonly string Fingerprint = new('A', 64);

    [Theory]
    [InlineData(-1)]
    [InlineData(0)]
    [InlineData(23)]
    public void MediaEnvelope_AcceptsMatchingMetadataAndFirstOrLastFrame(int frame)
    {
        var reply = Reply(frame);
        reply.Fingerprint = Fingerprint.ToLowerInvariant();
        reply.PixelSha256 = reply.PixelSha256.ToLowerInvariant();
        BackendConnection.ValidateMedia(reply, Asset, frame, 12, Fingerprint);
        Assert.Equal(frame < 0 ? 0 : BackendMediaDecoder.FrameBytes, reply.Pixels.Length);
        Assert.Equal(frame, reply.FrameIndex);
        Assert.Equal(12, reply.Revision);
    }

    [Fact]
    public void MediaEnvelope_AcceptsMetadataAtMaximumSupportedSourceBounds()
    {
        var reply = Reply(-1);
        reply.Fps = 240;
        reply.FrameCount = BackendMediaDecoder.MaxFrames;
        reply.SourceWidth = 8192;
        reply.SourceHeight = 2048;
        BackendConnection.ValidateMedia(reply, Asset, -1, 12, Fingerprint);
    }

    public static IEnumerable<object[]> InvalidFields()
    {
        foreach (var frame in new[] { -1, 7 })
            foreach (var field in new[] { "asset", "null-asset", "revision", "fingerprint", "null-fingerprint", "fps-zero", "fps-nan", "fps-infinity", "fps-high",
                "count-zero", "count-high", "source-width-zero", "source-width-high", "source-height-zero", "source-height-high", "source-area",
                "width", "height", "frame-index", "null-pixels", "null-pixel-sha", "null-error" })
                yield return [field, frame];
    }

    [Theory]
    [MemberData(nameof(InvalidFields))]
    public void MediaEnvelope_RejectsEachIndependentIdentityMetadataAndPayloadMutation(string field, int frame)
    {
        var reply = Reply(frame);
        // Keep one maximal, valid source dimension in the area case, then mutate only height.
        if (field == "source-area") { reply.SourceWidth = 8192; reply.SourceHeight = 2048; }
        switch (field)
        {
            case "asset": reply.AssetId = "video-b"; break;
            case "null-asset": reply.AssetId = null!; break;
            case "revision": reply.Revision++; break;
            case "fingerprint": reply.Fingerprint = new('B', 64); break;
            case "null-fingerprint": reply.Fingerprint = null!; break;
            case "fps-zero": reply.Fps = 0; break;
            case "fps-nan": reply.Fps = double.NaN; break;
            case "fps-infinity": reply.Fps = double.PositiveInfinity; break;
            case "fps-high": reply.Fps = 240.01; break;
            case "count-zero": reply.FrameCount = 0; break;
            case "count-high": reply.FrameCount = BackendMediaDecoder.MaxFrames + 1; break;
            case "source-width-zero": reply.SourceWidth = 0; break;
            case "source-width-high": reply.SourceWidth = 8193; break;
            case "source-height-zero": reply.SourceHeight = 0; break;
            case "source-height-high": reply.SourceHeight = 8193; break;
            case "source-area": reply.SourceHeight = 2049; break;
            case "width": reply.Width--; break;
            case "height": reply.Height--; break;
            case "frame-index": reply.FrameIndex++; break;
            case "null-pixels": reply.Pixels = null!; break;
            case "null-pixel-sha": reply.PixelSha256 = null!; break;
            case "null-error": reply.ErrorCode = null!; break;
            default: throw new InvalidOperationException(field);
        }
        Assert.Throws<InvalidDataException>(() => BackendConnection.ValidateMedia(reply, Asset, frame, 12, Fingerprint));
    }

    [Theory]
    [InlineData("length-short")]
    [InlineData("length-long")]
    [InlineData("pixel-sha")]
    [InlineData("pixel-content")]
    [InlineData("past-end")]
    public void FrameEnvelope_RejectsByteExtentFingerprintAndOutOfRangeFrame(string field)
    {
        var reply = Reply(7);
        switch (field)
        {
            case "length-short": reply.Pixels = reply.Pixels[..^1]; break;
            case "length-long": reply.Pixels = [.. reply.Pixels, 1]; break;
            case "pixel-sha": reply.PixelSha256 = new('0', 64); break;
            case "pixel-content": reply.Pixels[0] ^= 0xFF; break;
            case "past-end": reply.FrameCount = 7; break;
            default: throw new InvalidOperationException(field);
        }
        Assert.Throws<InvalidDataException>(() => BackendConnection.ValidateMedia(reply, Asset, 7, 12, Fingerprint));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void MetadataEnvelope_RejectsPixelsOrPixelFingerprint(bool payload)
    {
        var reply = Reply(-1);
        if (payload) reply.Pixels = [1];
        else reply.PixelSha256 = new('A', 64);
        Assert.Throws<InvalidDataException>(() => BackendConnection.ValidateMedia(reply, Asset, -1, 12, Fingerprint));
    }

    [Fact]
    public void MediaEnvelope_RejectsNullReply()
    {
        Assert.Throws<InvalidDataException>(() => BackendConnection.ValidateMedia(null!, Asset, 0, 12, Fingerprint));
    }

    [Theory]
    [InlineData("revision_conflict")]
    [InlineData("decoder_unavailable")]
    [InlineData("media_not_found")]
    public void MediaEnvelope_PreservesServerFailureCodeBeforeInspectingSuccessFields(string code)
    {
        var error = Assert.Throws<BackendCommandException>(() => BackendConnection.ValidateMedia(new() { ErrorCode = code }, Asset, 0, 12, Fingerprint));
        Assert.Equal(code, error.Code);
    }

    [Fact]
    public async Task MediaReads_RequireCapabilityEvenForNegativeFrameAndUnavailableDecoder()
    {
        using var fixture = new BackendFixture();
        using var service = fixture.Service();
        var before = Snapshot.Take(fixture.Files);
        await Assert.ThrowsAsync<UnauthorizedAccessException>(() => service.MediaInfoAsync("wrong-key", Asset, 11, Fingerprint));
        await Assert.ThrowsAsync<UnauthorizedAccessException>(() => service.FrameAsync("wrong-key", Asset, 0, 11, Fingerprint));
        await Assert.ThrowsAsync<UnauthorizedAccessException>(() => service.FrameAsync("wrong-key", Asset, -1, 11, Fingerprint));
        before.AssertUnchanged(fixture.Files);
    }

    [Fact]
    public async Task NegativeFrame_IsInvalidWithoutProjectOrConfigurationIo()
    {
        using var fixture = new BackendFixture();
        using var service = fixture.Service();
        File.Delete(fixture.Files.ProjectPath);
        File.Delete(fixture.ConfigPath);
        // Invalid baseline and missing inputs would fail differently if the request reached storage or a decoder.
        var reply = await service.FrameAsync(fixture.Options.SessionKey, Asset, -1, -1, "invalid-sha");
        Assert.Equal("invalid_frame", reply.ErrorCode);
        Assert.Empty(reply.Pixels);
        Assert.False(File.Exists(fixture.Files.ProjectPath));
        Assert.False(File.Exists(fixture.ConfigPath));
        Assert.Empty(fixture.Files.HistoryFiles);
        Assert.False(File.Exists(fixture.Files.JournalPath));
    }

    [Theory]
    [InlineData("revision", false)]
    [InlineData("revision", true)]
    [InlineData("sha", false)]
    [InlineData("sha", true)]
    public async Task MediaReads_RefuseRevisionAndShaIndependentlyBeforeDecoder(string mismatch, bool frame)
    {
        using var fixture = new BackendFixture();
        var store = fixture.Files.Store();
        var original = await store.OpenAsync();
        var baseline = await store.MigrateAsync(original, original.Project.Revision, "contract-test");
        using var service = fixture.Service();
        var before = Snapshot.Take(fixture.Files);
        var revision = baseline.Project.Revision + (mismatch == "revision" ? 1 : 0);
        var fingerprint = mismatch == "sha" ? WrongSha(baseline.Fingerprint) : baseline.Fingerprint;
        var reply = frame
            ? await service.FrameAsync(fixture.Options.SessionKey, Asset, 0, revision, fingerprint)
            : await service.MediaInfoAsync(fixture.Options.SessionKey, Asset, revision, fingerprint);
        Assert.Equal("revision_conflict", reply.ErrorCode);
        Assert.Empty(reply.Pixels);
        before.AssertUnchanged(fixture.Files);
    }

    [Fact]
    public async Task MediaReads_SchemaOneRequiresExplicitMigration_WithoutChangingBytesOrHistory()
    {
        using var fixture = new BackendFixture();
        var baseline = await fixture.Files.Store(writable: false).OpenAsync();
        using var service = fixture.Service();
        var before = Snapshot.Take(fixture.Files);
        var info = await service.MediaInfoAsync(fixture.Options.SessionKey, Asset, baseline.Project.Revision, baseline.Fingerprint);
        var frame = await service.FrameAsync(fixture.Options.SessionKey, Asset, 0, baseline.Project.Revision, baseline.Fingerprint);
        Assert.Equal("migration_required", info.ErrorCode);
        Assert.Equal("migration_required", frame.ErrorCode);
        Assert.Empty(fixture.Files.HistoryFiles);
        before.AssertUnchanged(fixture.Files);
        Assert.Equal(1, (await fixture.Files.Store(writable: false).OpenAsync()).SchemaVersion);
    }

    [Fact]
    public async Task MediaReads_DoNotRequireOrClaimWriter_AndUnavailableDecoderPreservesBaseline()
    {
        using var fixture = new BackendFixture();
        var store = fixture.Files.Store();
        var original = await store.OpenAsync();
        var baseline = await store.MigrateAsync(original, original.Project.Revision, "contract-test");
        using var service = fixture.Service();
        Assert.False(fixture.Options.WriteEnabled);
        var before = Snapshot.Take(fixture.Files);
        // Another owner can hold the writer while these read-only requests run.
        using (ProjectWriterLease.Acquire(new() { ProjectDirectory = fixture.Files.ProjectDirectory, AllowedWriteRoots = [fixture.Files.ProjectDirectory] }))
        {
            var info = await service.MediaInfoAsync(fixture.Options.SessionKey, Asset, baseline.Project.Revision, baseline.Fingerprint);
            var frame = await service.FrameAsync(fixture.Options.SessionKey, Asset, 0, baseline.Project.Revision, baseline.Fingerprint);
            Assert.Equal("decoder_unavailable", info.ErrorCode);
            Assert.Equal("decoder_unavailable", frame.ErrorCode);
            Assert.Equal("write_not_enabled", (await service.AcquireWriterAsync(fixture.Options.SessionKey, baseline.Project.Revision, baseline.Fingerprint)).ErrorCode);
        }
        before.AssertUnchanged(fixture.Files);
        var after = await store.OpenAsync();
        Assert.Equal(baseline.Project.Revision, after.Project.Revision);
        Assert.Equal(baseline.Fingerprint, after.Fingerprint);
    }

    private static string WrongSha(string fingerprint) => (char.ToUpperInvariant(fingerprint[0]) == 'A' ? "B" : "A") + fingerprint[1..];
    private static BackendMediaReply Reply(int frame)
    {
        var pixels = frame < 0 ? [] : Enumerable.Range(0, BackendMediaDecoder.FrameBytes).Select(index => (byte)index).ToArray();
        return new()
        {
            AssetId = Asset, Revision = 12, Fingerprint = Fingerprint,
            Fps = 24, FrameCount = 24, SourceWidth = 1280, SourceHeight = 720,
            Width = 480, Height = 270, FrameIndex = frame, Pixels = pixels,
            PixelSha256 = frame < 0 ? "" : Convert.ToHexString(SHA256.HashData(pixels))
        };
    }

    private sealed record Snapshot(byte[] Project, Dictionary<string, byte[]> History)
    {
        internal static Snapshot Take(PersistenceFixture files) => new(File.ReadAllBytes(files.ProjectPath),
            files.HistoryFiles.ToDictionary(path => Path.GetFileName(path), File.ReadAllBytes));
        internal void AssertUnchanged(PersistenceFixture files)
        {
            Assert.Equal(Project, File.ReadAllBytes(files.ProjectPath));
            var actual = files.HistoryFiles.ToDictionary(path => Path.GetFileName(path), File.ReadAllBytes);
            Assert.Equal(History.Keys.OrderBy(key => key), actual.Keys.OrderBy(key => key));
            foreach (var pair in History) Assert.Equal(pair.Value, actual[pair.Key]);
            Assert.False(File.Exists(files.JournalPath));
        }
    }
}
