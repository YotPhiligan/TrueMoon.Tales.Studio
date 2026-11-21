using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using TrueMoon.Tales.Studio.Configuration;

namespace TrueMoon.Tales.Studio.Backend.Export;

internal sealed class BackendExportException(string code) : IOException(code)
{
    internal string Code { get; } = code;
}

internal sealed record BackendExportStatus(int Version, string Id, string ProjectId, long ProjectRevision,
    string ProjectFingerprint, string Profile, long Revision, string State, string RequestSha256, int Frames,
    double Fps, long? ArtifactSizeBytes = null, string? ArtifactSha256 = null, string? ErrorCode = null,
    int? ProgressPercent = null,
    [property: System.Text.Json.Serialization.JsonIgnore(Condition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull)]
    int? RangeStart = null,
    [property: System.Text.Json.Serialization.JsonIgnore(Condition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingDefault)]
    bool Animatic = false);

internal sealed record BackendExportCheck(long ProjectRevision, string ProjectFingerprint, string Profile, int Width, int Height,
    int Frames, double Fps);

// The configured root grants export storage only. Job IDs and project paths are compiled into fixed path components.
internal sealed class BackendExportStorage
{
    internal const int MaxRequestBytes = 1024 * 1024;
    internal const int MaxStatusBytes = 16 * 1024;
    // Python and its native codecs must all be able to open the worker's longest temporary filename.
    private const int MaxWindowsPath = 259;
    private static readonly int WorkerSuffixLength = 1 + "InputHashes.json.".Length + 32 + ".pending".Length;
    internal static readonly JsonSerializerOptions Json = new() { PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower };

    private readonly string _root;
    private readonly string _namespace;
    private readonly string _project;
    private static readonly StringComparison Comparison = OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;

    internal BackendExportStorage(BackendOptions options, WorkspaceSettings? settings = null)
    {
        var config = settings ?? WorkspaceProfile.Read(options.ConfigPath);
        if (config.ExportsRoot is not { } root) throw new BackendExportException("export_unavailable");
        var directory = Path.GetDirectoryName(options.ConfigPath)!;
        _root = ConfigPath(root, directory);
        var project = Path.TrimEndingDirectorySeparator(Path.GetFullPath(options.ProjectDirectory));
        var denied = BackendProjectAccess.Read(options, config).ProtectedRoots.Append(project)
            .Concat(new[] { config.AmvRoot, config.ProjectsRoot, config.LegacyRoot }.OfType<string>()
                .Concat(config.Mounts.Values).Select(path => ConfigPath(path, directory)));
        if (_root == Path.GetPathRoot(_root) || denied.Any(path => Within(_root, path) || Within(path, _root)))
            throw new UnauthorizedAccessException("Export storage overlaps protected or source roots.");
        // Do not place writable export state in the source/distribution tree.
        foreach (var start in new[] { AppContext.BaseDirectory, Environment.CurrentDirectory })
            for (var parent = new DirectoryInfo(start); parent is not null; parent = parent.Parent)
                if (File.Exists(Path.Combine(parent.FullName, "TrueMoon.Tales.Studio.slnx")) &&
                    parent.Parent is { } codeRoot && Within(_root, codeRoot.FullName))
                    throw new UnauthorizedAccessException("Export storage must be outside the Studio source tree.");
        _namespace = Path.Combine(_root, "BackendExports.v1");
        var scope = Hash(Encoding.UTF8.GetBytes(OperatingSystem.IsWindows() ? project.ToUpperInvariant() : project))[..32];
        _project = Path.Combine(_namespace, scope);
        Check(_project);
    }

    internal BackendExportStorage(StudioReviewSession session)
    {
        _root = session.DirectoryPath;
        _namespace = _root;
        _project = Path.Combine(_root, "Jobs");
    }

    internal string JobDirectory(string id, bool working = false)
    {
        if (!ValidId(id)) throw new BackendExportException("invalid_export");
        var directory = Path.Combine(_project, working ? "working-" + id[7..] : id);
        if (OperatingSystem.IsWindows() && directory.Length + WorkerSuffixLength > MaxWindowsPath)
            throw new BackendExportException("export_unavailable");
        return Check(directory);
    }

    internal void DropWorking(string id)
    {
        var directory = JobDirectory(id, working: true);
        if (!Directory.Exists(directory)) return;
        foreach (var file in Directory.EnumerateFiles(directory)) File.Delete(Check(file));
        Directory.Delete(Check(directory));
    }

    internal string ClientOutputDirectory => _root;
    internal string WorkerScope => _namespace;

    internal string PublishInterchange(BackendInterchange package, CancellationToken token)
    {
        var id = Guid.NewGuid().ToString("N");
        var name = "Montage-" + id;
        var stage = Check(Path.Combine(_root, ".montage-" + id + ".pending"));
        var output = Check(Path.Combine(_root, name));
        Directory.CreateDirectory(stage);
        try
        {
            WriteNew(Path.Combine(stage, BackendInterchange.XmlName), package.Xml);
            WriteNew(Path.Combine(stage, BackendInterchange.ReportName), package.Report);
            token.ThrowIfCancellationRequested();
            Directory.Move(Check(stage), Check(output));
            return name;
        }
        finally
        {
            if (Directory.Exists(Check(stage)))
            {
                File.Delete(Check(Path.Combine(stage, BackendInterchange.XmlName)));
                File.Delete(Check(Path.Combine(stage, BackendInterchange.ReportName)));
                Directory.Delete(Check(stage));
            }
        }
    }

    internal IEnumerable<string> ExportIds() => Directory.Exists(Check(_project))
        ? Directory.EnumerateDirectories(_project, "export-*").Select(Path.GetFileName).OfType<string>() : [];

    internal string? LoadClientKey()
    {
        var path = Check(Path.Combine(_project, "ClientKey"));
        if (!File.Exists(path)) return null;
        var key = Encoding.ASCII.GetString(Read(_project, "ClientKey", 80));
        if (!BackendConnection.IsExportClientKey(key)) throw new InvalidDataException("Invalid export checkpoint.");
        return key;
    }

    internal void SaveClientKey(string key)
    {
        if (!BackendConnection.IsExportClientKey(key)) throw new InvalidDataException("Invalid export key.");
        Directory.CreateDirectory(Check(_project));
        var temp = Check(Path.Combine(_project, "client-" + Guid.NewGuid().ToString("N") + ".tmp"));
        try
        {
            WriteNew(temp, Encoding.ASCII.GetBytes(key));
            File.Move(Check(temp), Check(Path.Combine(_project, "ClientKey")), overwrite: true);
        }
        finally { File.Delete(Check(temp)); }
    }

    internal FileStream AcquireSlot()
    {
        var path = Check(Path.Combine(_namespace, "Active.lock"));
        Directory.CreateDirectory(_namespace);
        Check(path);
        try { return new(path, FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None); }
        catch (IOException) { throw new BackendExportException("export_busy"); }
    }

    internal FileStream AcquireWorkerLock(string directory)
    {
        // The Python worker takes the same byte-range lock before its READY handshake.
        if (OperatingSystem.IsMacOS() || !OperatingSystem.IsWindows() && !OperatingSystem.IsLinux())
            throw new BackendExportException("export_unavailable");
        var stream = new FileStream(Check(Path.Combine(directory, "Worker.lock")), FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.ReadWrite);
        try { stream.Lock(0, 1); return stream; }
        catch (IOException) { stream.Dispose(); throw new BackendExportException("export_busy"); }
        catch { stream.Dispose(); throw; }
    }

    internal void Create(string directory, byte[] request, BackendExportStatus receipt)
    {
        // Install the accepted job as a single rename. A crash before this point has accepted nothing.
        Directory.CreateDirectory(Check(_project));
        var stage = Check(Path.Combine(_project, "pending-" + Guid.NewGuid().ToString("N")));
        Directory.CreateDirectory(stage);
        try
        {
            var metadata = JsonSerializer.SerializeToUtf8Bytes(receipt, Json);
            WriteNew(Path.Combine(stage, "Request.json"), request);
            WriteNew(Path.Combine(stage, "Receipt.json"), metadata);
            WriteNew(Path.Combine(stage, "Status.json"), metadata);
            Directory.Move(Check(stage), Check(directory));
        }
        finally
        {
            if (Directory.Exists(Check(stage)))
            {
                foreach (var file in new[] { "Request.json", "Receipt.json", "Status.json" })
                    File.Delete(Check(Path.Combine(stage, file)));
                Directory.Delete(Check(stage));
            }
        }
    }

    internal void Save(string directory, BackendExportStatus status)
    {
        var path = Check(Path.Combine(directory, "Status.json"));
        var temp = Check(Path.Combine(directory, "status-" + Guid.NewGuid().ToString("N") + ".tmp"));
        try
        {
            WriteNew(temp, JsonSerializer.SerializeToUtf8Bytes(status, Json));
            File.Move(Check(temp), Check(path), overwrite: true);
        }
        finally { File.Delete(Check(temp)); }
    }

    private void WriteNew(string path, ReadOnlySpan<byte> content)
    {
        using var stream = new FileStream(Check(path), FileMode.CreateNew, FileAccess.Write, FileShare.None);
        stream.Write(content);
        stream.Flush(flushToDisk: true);
    }

    internal byte[] Read(string directory, string name, int limit)
    {
        using var stream = Open(directory, name);
        if (stream.Length is < 1 || stream.Length > limit) throw new BackendExportException("invalid_export_artifact");
        var data = new byte[(int)stream.Length];
        stream.ReadExactly(data);
        return data;
    }

    internal FileStream Open(string directory, string name) => new(Check(Path.Combine(directory, name)),
        FileMode.Open, FileAccess.Read, FileShare.Read | FileShare.Delete, 16 * 1024, FileOptions.Asynchronous);

    internal string Check(string path)
    {
        if (!Within(path, _root)) throw new UnauthorizedAccessException("Export path escaped its root.");
        for (var current = path; current is not null; current = Path.GetDirectoryName(current))
        {
            try
            {
                if ((File.GetAttributes(current) & FileAttributes.ReparsePoint) != 0)
                    throw new UnauthorizedAccessException("Export paths must not traverse links.");
            }
            catch (FileNotFoundException) { }
            catch (DirectoryNotFoundException) { }
        }
        return path;
    }

    internal static bool ValidId(string? id) => id is { Length: 39 } && id.StartsWith("export-", StringComparison.Ordinal) &&
        id.AsSpan(7).IndexOfAnyExcept("0123456789abcdef") < 0;

    internal static bool ValidHash(string? hash) => hash is { Length: 64 } && hash.All(Uri.IsHexDigit);
    internal static bool EqualHash(string? a, string? b) => string.Equals(a, b, StringComparison.OrdinalIgnoreCase);
    internal static string Hash(ReadOnlySpan<byte> bytes) => Convert.ToHexString(SHA256.HashData(bytes));

    private static string ConfigPath(string value, string directory)
    {
        var path = Environment.ExpandEnvironmentVariables(value).Replace(Path.AltDirectorySeparatorChar, Path.DirectorySeparatorChar);
        if (path.StartsWith(new string(Path.DirectorySeparatorChar, 2), StringComparison.Ordinal) ||
            Path.IsPathRooted(path) && !Path.IsPathFullyQualified(path) ||
            path.IndexOf(':', OperatingSystem.IsWindows() && Path.IsPathFullyQualified(path) ? 2 : 0) >= 0)
            throw new UnauthorizedAccessException("Export storage requires an ordinary local path.");
        return Path.TrimEndingDirectorySeparator(Path.GetFullPath(path, directory));
    }

    private static bool Within(string path, string root) => string.Equals(path, root, Comparison) ||
        path.StartsWith(Path.EndsInDirectorySeparator(root) ? root : root + Path.DirectorySeparatorChar, Comparison);
}
