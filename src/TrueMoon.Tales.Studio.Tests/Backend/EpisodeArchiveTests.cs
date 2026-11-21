using System.IO.Compression;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Nodes;
using TrueMoon.Tales.Studio.Backend;
using TrueMoon.Tales.Studio.Backend.Export;
using TrueMoon.Tales.Studio.Persistence;
using Xunit;

namespace TrueMoon.Tales.Studio.Tests.Backend;

public sealed class EpisodeArchiveTests
{
    [Fact]
    public async Task EpisodeArchive_CliRoundTrip_PreservesSnapshotHistoryAndPortableMedia()
    {
        using var files = new ArchiveFixture();
        var snapshot = await files.PrepareAsync();
        var before = File.ReadAllBytes(files.ProjectFile);
        var history = Directory.GetFiles(files.History).ToDictionary(file => Path.GetFileName(file), File.ReadAllBytes);
        var receipt = await Cli<EpisodeArchiveReceipt>("--archive-episode", files.Project, "--config", files.Config,
            "--revision", snapshot.Project.Revision.ToString(System.Globalization.CultureInfo.InvariantCulture), "--sha256", snapshot.Fingerprint);
        Assert.Equal(snapshot.Fingerprint, receipt.ProjectSha256);
        Assert.Equal((1, 3), (receipt.History, receipt.Media));
        Assert.Equal(Sha(receipt.File), receipt.Sha256);
        var restored = await Cli<EpisodeRestoreReceipt>("--restore-episode", Path.GetFileName(receipt.File), "--config", files.Config, "--sha256", receipt.Sha256);
        Assert.Equal((receipt.Revision, receipt.ProjectSha256, 1, 3), (restored.Revision, restored.ProjectSha256, restored.History, restored.Media));
        Assert.Equal([restored.Directory], Directory.GetDirectories(files.Destination));
        Assert.Equal(before, File.ReadAllBytes(Path.Combine(restored.Directory, "Project.studio")));
        Assert.Equal(before, File.ReadAllBytes(files.ProjectFile));
        foreach (var (name, bytes) in history)
        {
            Assert.Equal(bytes, File.ReadAllBytes(Path.Combine(restored.Directory, "Production", "History", name)));
            Assert.Equal(bytes, File.ReadAllBytes(Path.Combine(files.History, name)));
        }
        var restoredOptions = BackendOptions.Create(restored.Directory, restored.Config) with { WriteEnabled = true };
        var reopened = await new ProjectStore(BackendProjectAccess.Read(restoredOptions)).OpenAsync();
        Assert.Equal(snapshot.Project.Montage.Clips.Select(row => (row.Id, row.TakeId, row.StartFrame, row.DurationFrames)),
            reopened.Project.Montage.Clips.Select(row => (row.Id, row.TakeId, row.StartFrame, row.DurationFrames)));
        var resolver = new BackendMediaResolver(restoredOptions);
        foreach (var name in new[] { "clip-a.mp4", "clip-b.mp4", "chapter.md" })
        {
            var path = resolver.Resolve($"@media/{name}");
            Assert.Equal(Path.Combine(restored.Directory, "Media"), Path.GetDirectoryName(path));
            Assert.Equal(Sha(Path.Combine(files.Materials, name)), Sha(path));
        }
        Assert.Throws<UnauthorizedAccessException>(() => resolver.Resolve(Path.Combine(files.Materials, "clip-a.mp4")));
        var another = await BackendEpisodeArchive.RestoreAsync(files.Config, Path.GetFileName(receipt.File), receipt.Sha256, CancellationToken.None);
        Assert.NotEqual(restored.Directory, another.Directory);
        Assert.Equal(before, File.ReadAllBytes(Path.Combine(restored.Directory, "Project.studio")));

        static async Task<T> Cli<T>(params string[] args) => JsonSerializer.Deserialize<T>(
            await BackendTrimFixture.RunAsync(BackendWriteFixture.Executable(), args, 64 * 1024), BackendAdaptation.Json)!;
    }

    [Fact]
    public async Task EpisodeArchive_RejectsStaleProjectAndChangedPinnedMaterialBeforePublishing()
    {
        using var files = new ArchiveFixture();
        var snapshot = await files.PrepareAsync();
        await Assert.ThrowsAsync<ProjectRevisionConflictException>(() => BackendEpisodeArchive.CreateAsync(files.Options,
            snapshot.Project.Revision, new('0', 64), CancellationToken.None));
        Assert.Empty(Directory.GetFileSystemEntries(files.Archives));
        File.WriteAllText(Path.Combine(files.Materials, "chapter.md"), "Другой снимок главы");
        await Assert.ThrowsAsync<InvalidDataException>(() => BackendEpisodeArchive.CreateAsync(files.Options,
            snapshot.Project.Revision, snapshot.Fingerprint, CancellationToken.None));
        Assert.Empty(Directory.GetFileSystemEntries(files.Archives));
        Assert.Equal(snapshot.Fingerprint, Sha(files.ProjectFile));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task EpisodeArchive_RejectsChangedPayloadOrUnexpectedEntryWithoutLeavingARestore(bool unexpected)
    {
        using var files = new ArchiveFixture();
        var snapshot = await files.PrepareAsync();
        var receipt = await BackendEpisodeArchive.CreateAsync(files.Options, snapshot.Project.Revision, snapshot.Fingerprint, CancellationToken.None);
        using (var zip = ZipFile.Open(receipt.File, ZipArchiveMode.Update))
        {
            if (unexpected)
            {
                using var item = zip.CreateEntry("Extra.json").Open();
                item.WriteByte(42);
            }
            else
            {
                var entry = zip.Entries.First(row => row.FullName.StartsWith("Media/", StringComparison.Ordinal));
                using var item = entry.Open();
                item.Position = 0;
                item.WriteByte(99);
            }
        }
        await Assert.ThrowsAsync<InvalidDataException>(() => BackendEpisodeArchive.RestoreAsync(files.Config,
            Path.GetFileName(receipt.File), receipt.Sha256, CancellationToken.None));
        await Assert.ThrowsAsync<InvalidDataException>(() => BackendEpisodeArchive.RestoreAsync(files.Config,
            Path.GetFileName(receipt.File), Sha(receipt.File), CancellationToken.None));
        Assert.Empty(Directory.GetFileSystemEntries(files.Destination));
        Assert.Equal(snapshot.Fingerprint, Sha(files.ProjectFile));
    }

    private static string Sha(string file) => Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(file)));

    private sealed class ArchiveFixture : IDisposable
    {
        private readonly BackendWriteFixture _source = new();
        internal string Project => _source.Files.ProjectDirectory;
        internal string ProjectFile => _source.Files.ProjectPath;
        internal string History => _source.Files.HistoryDirectory;
        internal string Config => _source.Base.ConfigPath;
        internal BackendOptions Options => BackendOptions.Create(Project, Config);
        internal string Materials => Path.Combine(_source.Files.Root, "materials");
        internal string Destination => Path.Combine(_source.Files.Root, "restored");
        internal string Archives => Path.Combine(_source.Files.Root, "archives");

        internal async Task<ProjectFileDocument> PrepareAsync()
        {
            Directory.CreateDirectory(Materials);
            Directory.CreateDirectory(Destination);
            Directory.CreateDirectory(Archives);
            File.WriteAllText(Path.Combine(Materials, "clip-a.mp4"), "a");
            File.WriteAllText(Path.Combine(Materials, "clip-b.mp4"), "b");
            var chapter = Path.Combine(Materials, "chapter.md");
            File.WriteAllText(chapter, "Проверка главы");
            var legacy = JsonNode.Parse(File.ReadAllBytes(ProjectFile))!;
            legacy["assets"]!["video-a"]!["path"] = "@media/clip-a.mp4";
            legacy["assets"]!["video-b"]!["path"] = "@media/clip-b.mp4";
            legacy["assets"]!.AsObject().Remove("other");
            File.WriteAllText(ProjectFile, legacy.ToJsonString());
            var config = JsonNode.Parse(File.ReadAllBytes(Config))!;
            config["projects_root"] = Destination;
            config["amv_root"] = Project;
            config["exports_root"] = Archives;
            config["project_write_roots"] = new JsonArray(Destination);
            config["mounts"] = new JsonObject { ["media"] = Materials };
            File.WriteAllText(Config, config.ToJsonString());
            var store = _source.Files.Store();
            var input = await store.OpenAsync();
            var document = await store.MigrateAsync(input, input.Project.Revision, "episode-archive-test");
            var envelope = JsonNode.Parse(File.ReadAllBytes(ProjectFile))!;
            envelope["opaque_archive_test"] = "kept byte for byte";
            envelope["project"]!["properties"]!["studio_adaptation_v1"] = JsonSerializer.SerializeToNode(new AdaptationNotes(
                [new(document.Project.Segments[0].Id, null, new("@media/chapter.md", Sha(chapter), 1, 1, "Проверка главы"))], []), BackendAdaptation.Json);
            File.WriteAllText(ProjectFile, envelope.ToJsonString());
            return await store.OpenAsync();
        }

        public void Dispose() => _source.Dispose();
    }
}
