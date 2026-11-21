using System.Buffers;
using System.Globalization;
using System.IO.Compression;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.RegularExpressions;
using TrueMoon.Tales.Studio.Configuration;
using TrueMoon.Tales.Studio.Contracts.Projects;
using TrueMoon.Tales.Studio.Persistence;

namespace TrueMoon.Tales.Studio.Backend.Export;

internal sealed record EpisodeArchiveEntry(string Name, long Bytes, string Sha256);
internal sealed record EpisodeArchiveBinding(string Reference, string Name);
internal sealed record EpisodeArchiveManifest(int Version, string ProjectId, long Revision, string ProjectSha256,
    EpisodeArchiveEntry[] Entries, EpisodeArchiveBinding[] Media);
internal sealed record EpisodeArchiveReceipt(string File, string Sha256, long Revision, string ProjectSha256, int History, int Media);
internal sealed record EpisodeRestoreReceipt(string Directory, string Config, long Revision, string ProjectSha256, int History, int Media);

internal static partial class BackendEpisodeArchive
{
    internal const string MediaIndex = "ArchiveMedia.json";
    private const string ManifestName = "Manifest.json", ProjectName = "Studio.project.json", ConfigName = "Studio.archive.config.json";
    private const string HistoryPrefix = "Production/History/";
    private const int MaxEntries = 4096, MaxMetadataBytes = 16 * 1024 * 1024;
    private const long MaxFileBytes = 8L * 1024 * 1024 * 1024, MaxTotalBytes = 32L * 1024 * 1024 * 1024;
    private static readonly StringComparer FileComparer = OperatingSystem.IsWindows() ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal;

    [GeneratedRegex(@"\A[r][0-9]{6,19}-[0-9a-f]{32}\.json\z", RegexOptions.CultureInvariant)]
    private static partial Regex HistoryName();
    [GeneratedRegex(@"\AMedia/[0-9]{8}(\.[a-z0-9]{1,16})?\z", RegexOptions.CultureInvariant)]
    private static partial Regex MediaName();
    [GeneratedRegex(@"\A\.[a-z0-9]{1,16}\z", RegexOptions.CultureInvariant)]
    private static partial Regex Extension();
    [GeneratedRegex(@"\AEpisode-[0-9a-f]{32}\.zip\z", RegexOptions.CultureInvariant)]
    private static partial Regex ArchiveName();

    internal static async Task<EpisodeArchiveReceipt> CreateAsync(BackendOptions options, long revision, string fingerprint,
        CancellationToken token)
    {
        var store = new ProjectStore(BackendProjectAccess.Read(options));
        var snapshot = await store.OpenAsync(token).ConfigureAwait(false);
        if (snapshot.SchemaVersion != 2) throw new ProjectMigrationRequiredException();
        if (snapshot.Project.Revision != revision || !BackendExportStorage.EqualHash(snapshot.Fingerprint, fingerprint))
            throw new ProjectRevisionConflictException(revision, snapshot.Project.Revision);
        var storage = new BackendExportStorage(options);
        var id = Guid.NewGuid().ToString("N");
        var name = $"Episode-{id}.zip";
        var pending = storage.Check(Path.Combine(storage.ClientOutputDirectory, $".episode-{id}.pending"));
        var output = storage.Check(Path.Combine(storage.ClientOutputDirectory, name));
        var resolver = new BackendMediaResolver(options);
        var files = new List<EpisodeArchiveEntry>();
        var bindings = new List<EpisodeArchiveBinding>();
        var savedMedia = new Dictionary<string, EpisodeArchiveEntry>(FileComparer);
        var historyNames = HistoryFiles(options.ProjectDirectory);
        var total = 0L;
        var created = false;
        try
        {
            Directory.CreateDirectory(storage.Check(storage.ClientOutputDirectory));
            await using (var stream = new FileStream(storage.Check(pending), FileMode.CreateNew, FileAccess.ReadWrite,
                             FileShare.None, 64 * 1024, FileOptions.Asynchronous))
            {
                created = true;
                using (var zip = new ZipArchive(stream, ZipArchiveMode.Create, leaveOpen: true))
                {
                    await AddAsync(ProjectName, snapshot.FilePath!, snapshot.Fingerprint).ConfigureAwait(false);
                    foreach (var file in historyNames)
                    {
                        var entry = HistoryPrefix + file;
                        var previousRevision = HistoryRevision(file);
                        if (previousRevision >= revision) throw Invalid();
                        var verified = await store.ReadHistoryAsync(snapshot.Project.Id, previousRevision, token).ConfigureAwait(false);
                        await AddAsync(entry, Path.Combine(options.ProjectDirectory, "Production", "History", file), verified.Fingerprint)
                            .ConfigureAwait(false);
                    }
                    foreach (var (reference, expected) in Dependencies(snapshot.Project).OrderBy(row => row.Key, StringComparer.Ordinal))
                    {
                        var path = resolver.Resolve(reference);
                        if (!savedMedia.TryGetValue(path, out var media))
                        {
                            var extension = Path.GetExtension(path).ToLowerInvariant();
                            var mediaName = "Media/" + savedMedia.Count.ToString("D8", CultureInfo.InvariantCulture) +
                                (Extension().IsMatch(extension) ? extension : "");
                            media = await AddAsync(mediaName, path, expected).ConfigureAwait(false);
                            savedMedia.Add(path, media);
                        }
                        else if (expected is not null && !BackendExportStorage.EqualHash(media.Sha256, expected))
                            throw new InvalidDataException("Сохранённая версия материала изменилась.");
                        bindings.Add(new(reference, media.Name));
                    }
                    var manifest = new EpisodeArchiveManifest(1, snapshot.Project.Id, revision, snapshot.Fingerprint,
                        files.ToArray(), bindings.ToArray());
                    var bytes = JsonSerializer.SerializeToUtf8Bytes(manifest, BackendAdaptation.Json);
                    if (bytes.Length > MaxMetadataBytes) throw Invalid();
                    await using var index = zip.CreateEntry(ManifestName, CompressionLevel.Fastest).Open();
                    await index.WriteAsync(bytes, token).ConfigureAwait(false);

                    async Task<EpisodeArchiveEntry> AddAsync(string entry, string path, string? expected)
                    {
                        if (files.Count == MaxEntries) throw Invalid();
                        await using var source = Open(path);
                        var length = source.Length;
                        ValidateSize(entry, length);
                        total += length;
                        if (total > MaxTotalBytes) throw Invalid();
                        await using var destination = zip.CreateEntry(entry, IsMedia(entry) ? CompressionLevel.NoCompression : CompressionLevel.Fastest).Open();
                        var sha = await TransferAsync(source, destination, length, token).ConfigureAwait(false);
                        if (expected is not null && !BackendExportStorage.EqualHash(expected, sha))
                            throw new InvalidDataException("Снимок или сохранённая версия материала изменилась.");
                        var result = new EpisodeArchiveEntry(entry, length, sha);
                        files.Add(result);
                        return result;
                    }
                }
                stream.Flush(flushToDisk: true);
            }
            string digest;
            await using (var archive = Open(pending)) digest = await HashAsync(archive, token).ConfigureAwait(false);
            var checkedSnapshot = await store.OpenAsync(token).ConfigureAwait(false);
            if (checkedSnapshot.Fingerprint != snapshot.Fingerprint || !historyNames.SequenceEqual(HistoryFiles(options.ProjectDirectory)))
                throw new ProjectRevisionConflictException(revision, checkedSnapshot.Project.Revision);
            token.ThrowIfCancellationRequested();
            File.Move(storage.Check(pending), storage.Check(output));
            created = false;
            return new(output, digest, revision, snapshot.Fingerprint, historyNames.Length, savedMedia.Count);
        }
        finally { if (created) File.Delete(storage.Check(pending)); }
    }

    internal static async Task<EpisodeRestoreReceipt> RestoreAsync(string config, string name, string expectedSha, CancellationToken token)
    {
        if (!ArchiveName().IsMatch(name) || !BackendExportStorage.ValidHash(expectedSha)) throw Invalid();
        var access = new BackendProjectAccess(config);
        var root = access.RestoreRoot();
        var id = Guid.NewGuid().ToString("N");
        var destination = Path.Combine(root, "project-restored-" + id);
        var stage = Path.Combine(root, ".restore-" + id);
        var storage = new BackendExportStorage(BackendOptions.Create(destination, config), access.Settings);
        await using var stream = Open(storage.Check(Path.Combine(storage.ClientOutputDirectory, name)));
        if (stream.Length is < 1 or > MaxTotalBytes + 64 * 1024 * 1024 ||
            !BackendExportStorage.EqualHash(await HashAsync(stream, token).ConfigureAwait(false), expectedSha))
            throw new InvalidDataException("SHA-256 ZIP не совпадает с указанным.");
        stream.Position = 0;
        using var zip = new ZipArchive(stream, ZipArchiveMode.Read);
        var manifest = await ReadManifestAsync(zip, token).ConfigureAwait(false);
        BackendMediaResolver.CheckNoLinks(stage, allowMissing: true);
        if (Path.Exists(stage)) throw Invalid();
        Directory.CreateDirectory(stage);
        try
        {
            foreach (var file in manifest.Entries)
            {
                var relative = file.Name == ProjectName ? ProjectStore.DefaultProjectFileName : file.Name.Replace('/', Path.DirectorySeparatorChar);
                var path = Path.Combine(stage, relative);
                BackendMediaResolver.CheckNoLinks(path, allowMissing: true);
                Directory.CreateDirectory(Path.GetDirectoryName(path)!);
                await using var input = zip.GetEntry(file.Name)!.Open();
                await using var output = new FileStream(path, FileMode.CreateNew, FileAccess.Write, FileShare.None, 64 * 1024, FileOptions.Asynchronous);
                if (!BackendExportStorage.EqualHash(await TransferAsync(input, output, file.Bytes, token).ConfigureAwait(false), file.Sha256))
                    throw Invalid();
                output.Flush(flushToDisk: true);
            }
            var snapshot = ProjectFileCodec.Read(await File.ReadAllBytesAsync(Path.Combine(stage, ProjectStore.DefaultProjectFileName), token).ConfigureAwait(false));
            if (snapshot.SchemaVersion != 2 || snapshot.Project.Id != manifest.ProjectId || snapshot.Project.Revision != manifest.Revision ||
                !BackendExportStorage.EqualHash(snapshot.Fingerprint, manifest.ProjectSha256)) throw Invalid();
            var entries = manifest.Entries.ToDictionary(row => row.Name, StringComparer.Ordinal);
            var bindings = manifest.Media.ToDictionary(row => row.Reference, StringComparer.Ordinal);
            var dependencies = Dependencies(snapshot.Project);
            if (dependencies.Count != bindings.Count || dependencies.Any(row => !bindings.TryGetValue(row.Key, out var link) ||
                row.Value is { } sha && !BackendExportStorage.EqualHash(sha, entries[link.Name].Sha256))) throw Invalid();
            var historyCount = 0;
            foreach (var nameInZip in entries.Keys.Where(IsHistory))
            {
                var past = ProjectFileCodec.Read(await File.ReadAllBytesAsync(Path.Combine(stage, nameInZip), token).ConfigureAwait(false));
                if (past.Project.Id != manifest.ProjectId || past.Project.Revision != HistoryRevision(Path.GetFileName(nameInZip)) ||
                    past.Project.Revision >= snapshot.Project.Revision) throw Invalid();
                historyCount++;
            }
            await WriteNewAsync(Path.Combine(stage, MediaIndex), manifest.Media, token).ConfigureAwait(false);
            await WriteNewAsync(Path.Combine(stage, ConfigName), RestoredConfig(access.Settings, config, storage.ClientOutputDirectory), token).ConfigureAwait(false);
            token.ThrowIfCancellationRequested();
            BackendMediaResolver.CheckNoLinks(stage);
            BackendMediaResolver.CheckNoLinks(destination, allowMissing: true);
            Directory.Move(stage, destination);
            return new(destination, Path.Combine(destination, ConfigName), snapshot.Project.Revision,
                snapshot.Fingerprint, historyCount, manifest.Entries.Count(row => IsMedia(row.Name)));
        }
        finally
        {
            if (Directory.Exists(stage))
            {
                BackendMediaResolver.CheckNoLinks(stage);
                Directory.Delete(stage, recursive: true);
            }
        }
    }

    private static async Task<EpisodeArchiveManifest> ReadManifestAsync(ZipArchive zip, CancellationToken token)
    {
        if (zip.Entries.Count is < 2 or > MaxEntries + 1 ||
            zip.Entries.Select(row => row.FullName).Distinct(StringComparer.OrdinalIgnoreCase).Count() != zip.Entries.Count ||
            zip.GetEntry(ManifestName) is not { Length: > 0 and <= MaxMetadataBytes } index) throw Invalid();
        await using var input = index.Open();
        using var bytes = new MemoryStream();
        await TransferAsync(input, bytes, index.Length, token).ConfigureAwait(false);
        bytes.Position = 0;
        var manifest = await JsonSerializer.DeserializeAsync<EpisodeArchiveManifest>(bytes, BackendAdaptation.Json, token).ConfigureAwait(false);
        if (manifest is not { Version: 1, ProjectId.Length: > 0, Revision: >= 0, Entries: not null, Media: not null } ||
            manifest.Entries.Length != zip.Entries.Count - 1 || manifest.Media.Length > MaxEntries || !BackendExportStorage.ValidHash(manifest.ProjectSha256))
            throw Invalid();
        var names = new HashSet<string>(StringComparer.Ordinal);
        var history = new HashSet<long>();
        long total = 0;
        foreach (var file in manifest.Entries)
        {
            if (file?.Name is null || !(file.Name == ProjectName || IsHistory(file.Name) || IsMedia(file.Name)) ||
                !names.Add(file.Name) || !BackendExportStorage.ValidHash(file.Sha256) || zip.GetEntry(file.Name)?.Length != file.Bytes ||
                IsHistory(file.Name) && !history.Add(HistoryRevision(Path.GetFileName(file.Name)))) throw Invalid();
            ValidateSize(file.Name, file.Bytes);
            total += file.Bytes;
            if (total > MaxTotalBytes) throw Invalid();
        }
        if (!names.Contains(ProjectName) || !BackendExportStorage.EqualHash(manifest.ProjectSha256,
            manifest.Entries.Single(row => row.Name == ProjectName).Sha256)) throw Invalid();
        var media = ValidateMediaIndex(manifest.Media);
        if (!names.Where(IsMedia).ToHashSet(StringComparer.Ordinal).SetEquals(media.Values)) throw Invalid();
        return manifest;
    }

    internal static Dictionary<string, string> ReadMediaIndex(string directory)
    {
        using var stream = Open(Path.Combine(directory, MediaIndex));
        if (stream.Length is < 1 or > MaxMetadataBytes) throw Invalid();
        return ValidateMediaIndex(JsonSerializer.Deserialize<EpisodeArchiveBinding[]>(stream, BackendAdaptation.Json) ?? throw Invalid());
    }

    private static Dictionary<string, string> ValidateMediaIndex(EpisodeArchiveBinding[] media)
    {
        if (media.Length > MaxEntries) throw Invalid();
        var result = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var row in media)
        {
            if (row is not { Reference.Length: > 0 and <= 4096, Name: not null } || !IsMedia(row.Name) ||
                !result.TryAdd(row.Reference, row.Name)) throw Invalid();
            BackendMediaResolver.Validate(row.Reference);
        }
        return result;
    }

    private static Dictionary<string, string?> Dependencies(ProjectContract project)
    {
        var result = new Dictionary<string, string?>(StringComparer.Ordinal);
        if (project.Properties.TryGetValue("assets", out var assets))
            foreach (var row in assets.EnumerateObject())
                if (BackendScenarioEdit.TryReference(project, row.Name) is { } path) Add(path, null);
        foreach (var clip in project.Sources.SelectMany(row => row.Takes))
        {
            if (clip.VideoAssetId is { } id) RequireAsset(id);
            foreach (var input in BackendReferenceLibrary.Inputs(clip)) Add(input.Version.ImageReference, input.Version.Sha256);
            if (BackendTakePairs.Read(clip) is { } pair)
            {
                Add(pair.Draft.Reference, pair.Draft.Sha256);
                Add(pair.Final.Reference, pair.Final.Sha256);
            }
        }
        var adaptation = BackendAdaptation.Read(project);
        foreach (var text in adaptation.Texts) Add(text.Fragment.Reference, text.Fragment.Sha256);
        foreach (var still in adaptation.Stills) Add(still.Reference, null);
        foreach (var version in BackendReferenceLibrary.Read(project).Entries.SelectMany(row => row.Versions)) Add(version.ImageReference, version.Sha256);
        if (BackendExportPlan.ReadMusic(project.Montage.Properties) is { } music) RequireAsset(music.AssetId);
        if (project.Montage.Properties.TryGetValue("sound_events", out var sounds) && sounds.ValueKind != JsonValueKind.Null)
            foreach (var sound in sounds.EnumerateArray())
            {
                RequireAsset(sound.GetProperty("asset_id").GetString() ?? throw Invalid());
                if (BackendSpeech.Read(sound) is { } speech)
                    foreach (var version in speech.Versions) Add(version.Reference, version.Sha256);
            }
        return result;

        void RequireAsset(string id) => Add(BackendScenarioEdit.TryReference(project, id) ??
            throw new InvalidDataException("Монтаж ссылается на незарегистрированный ассет."), null);

        void Add(string reference, string? sha)
        {
            if (string.IsNullOrWhiteSpace(reference) || reference.Length > 4096 || sha is not null && !BackendExportStorage.ValidHash(sha)) throw Invalid();
            if (result.TryGetValue(reference, out var previous) && previous is not null)
            {
                if (sha is not null && !BackendExportStorage.EqualHash(sha, previous))
                    throw new InvalidDataException("Один путь указывает на разные сохранённые версии.");
                return;
            }
            result[reference] = sha;
            if (result.Count > MaxEntries) throw Invalid();
        }
    }

    private static Dictionary<string, object> RestoredConfig(WorkspaceSettings source, string config, string exports)
    {
        var settings = new Dictionary<string, object>
        {
            ["version"] = 1, ["projects_root"] = ".", ["project_write_roots"] = new[] { "." },
            ["exports_root"] = exports, ["archive_media"] = true
        };
        foreach (var (key, path) in new[] { ("ffmpeg_executable", source.FfmpegExecutable),
            ("ffprobe_executable", source.FfprobeExecutable), ("python_executable", source.PythonExecutable) })
            if (path is not null)
                settings[key] = Absolute(path);
        if (source.ProtectedRoots.Length > 0)
            settings["protected_roots"] = source.ProtectedRoots.Select(Absolute).ToArray();
        return settings;

        string Absolute(string path) => WorkspaceProfile.ResolvePath(path, config);
    }

    private static string[] HistoryFiles(string project)
    {
        var directory = Path.Combine(project, "Production", "History");
        BackendMediaResolver.CheckNoLinks(directory, allowMissing: true);
        if (!Directory.Exists(directory)) return [];
        var names = Directory.EnumerateFiles(directory, "r*.json").Select(Path.GetFileName).OfType<string>()
            .Where(name => HistoryName().IsMatch(name)).Take(MaxEntries + 1).Order(StringComparer.Ordinal).ToArray();
        return names.Length >= MaxEntries ? throw Invalid() : names;
    }

    private static bool IsHistory(string name) => name.StartsWith(HistoryPrefix, StringComparison.Ordinal) && HistoryName().IsMatch(name[HistoryPrefix.Length..]);
    private static bool IsMedia(string name) => MediaName().IsMatch(name);
    private static long HistoryRevision(string name)
    {
        var number = name.AsSpan(1, name.IndexOf('-') - 1);
        return long.TryParse(number, NumberStyles.None, CultureInfo.InvariantCulture, out var revision) &&
            number.SequenceEqual(revision.ToString("D6", CultureInfo.InvariantCulture)) ? revision : throw Invalid();
    }

    private static void ValidateSize(string name, long size)
    {
        if (size < 1 || size > (IsMedia(name) ? MaxFileBytes : MaxMetadataBytes)) throw Invalid();
    }

    private static FileStream Open(string path)
    {
        BackendMediaResolver.CheckNoLinks(path);
        return new(path, FileMode.Open, FileAccess.Read, FileShare.Read, 64 * 1024, FileOptions.Asynchronous | FileOptions.SequentialScan);
    }

    private static async Task<string> HashAsync(Stream input, CancellationToken token) =>
        Convert.ToHexString(await SHA256.HashDataAsync(input, token).ConfigureAwait(false));

    private static async Task<string> TransferAsync(Stream source, Stream destination, long length, CancellationToken token)
    {
        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        var buffer = ArrayPool<byte>.Shared.Rent(64 * 1024);
        try
        {
            while (length > 0)
            {
                var read = await source.ReadAsync(buffer.AsMemory(0, (int)Math.Min(length, buffer.Length)), token).ConfigureAwait(false);
                if (read == 0) throw Invalid();
                await destination.WriteAsync(buffer.AsMemory(0, read), token).ConfigureAwait(false);
                hash.AppendData(buffer, 0, read);
                length -= read;
            }
            if (await source.ReadAsync(buffer.AsMemory(0, 1), token).ConfigureAwait(false) != 0) throw Invalid();
            return Convert.ToHexString(hash.GetHashAndReset());
        }
        finally { ArrayPool<byte>.Shared.Return(buffer); }
    }

    private static async Task WriteNewAsync<T>(string path, T value, CancellationToken token)
    {
        await using var target = new FileStream(path, FileMode.CreateNew, FileAccess.Write, FileShare.None, 16 * 1024, FileOptions.Asynchronous);
        await JsonSerializer.SerializeAsync(target, value, BackendAdaptation.Json, token).ConfigureAwait(false);
        target.Flush(flushToDisk: true);
    }

    private static InvalidDataException Invalid() => new("Недопустимое содержимое или размер архива эпизода.");
}
