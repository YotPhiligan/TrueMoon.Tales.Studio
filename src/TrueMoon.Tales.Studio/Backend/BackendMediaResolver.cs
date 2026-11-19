using System.Text.Json;
using System.Text.RegularExpressions;

namespace TrueMoon.Tales.Studio.Backend;

// Paths are project metadata, never grants. Aliases are lexical: disabled legacy is never probed.
internal sealed class BackendMediaResolver
{
    private readonly string _base;
    private readonly string? _legacy;
    private readonly bool _legacyEnabled;
    private readonly Dictionary<string, string> _mounts = new(StringComparer.Ordinal);
    private readonly List<string> _roots = [];
    private readonly List<string> _protected = [];
    private readonly List<(string Source, string Target)> _aliases = [];
    private static readonly StringComparison Comparison = OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;
    private static readonly StringComparer Comparer = OperatingSystem.IsWindows() ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal;

    internal BackendMediaResolver(BackendOptions options)
    {
        using var document = JsonDocument.Parse(File.ReadAllText(options.ConfigPath));
        var config = document.RootElement;
        if (config.ValueKind != JsonValueKind.Object || !config.TryGetProperty("version", out var version) || version.GetInt32() != 1)
            throw new InvalidDataException("Unsupported workspace configuration version.");
        var directory = Path.GetDirectoryName(options.ConfigPath)!;
        string ConfigPath(JsonElement value) => Absolute(RequiredString(value), directory, trustedConfiguration: true);
        _legacy = config.TryGetProperty("legacy_root", out var legacy) ? ConfigPath(legacy) : null;
        _legacyEnabled = !config.TryGetProperty("legacy_read_enabled", out var enabled) || enabled.GetBoolean();
        _base = _legacy ?? Path.GetFullPath(options.ProjectDirectory);

        if (config.TryGetProperty("protected_roots", out var denied))
            foreach (var value in denied.EnumerateArray()) _protected.Add(ConfigPath(value));
        string? codeRoot = null;
        foreach (var start in new[] { AppContext.BaseDirectory, Environment.CurrentDirectory })
            for (var parent = new DirectoryInfo(start); parent is not null; parent = parent.Parent)
            {
                if (!_legacyEnabled && _legacy is not null && Within(parent.FullName, _legacy) ||
                    _protected.Any(root => Within(parent.FullName, root))) continue;
                if (!File.Exists(Path.Combine(parent.FullName, "TrueMoon.Tales.Studio.slnx"))) continue;
                codeRoot = Path.GetFullPath(Path.Combine(parent.FullName, ".."));
                _protected.Add(Path.GetFullPath(Path.Combine(codeRoot, "..", "TrueMoon")));
                break;
            }

        if (config.TryGetProperty("mounts", out var mounts))
            foreach (var mount in mounts.EnumerateObject())
            {
                if (!Regex.IsMatch(mount.Name, "\\A[a-z][a-z0-9_-]{0,31}\\z", RegexOptions.CultureInvariant))
                    throw new InvalidDataException("Invalid workspace mount name.");
                if (!_mounts.TryAdd(mount.Name, ConfigPath(mount.Value))) throw new InvalidDataException("Duplicate workspace mount.");
            }
        foreach (var pair in new[] { ("projects_root", "projects"), ("state_root", "state"), ("cache_root", "cache"), ("exports_root", "exports"), ("profiles_root", "profiles") })
            if (config.TryGetProperty(pair.Item1, out var value)) AddReserved(pair.Item2, ConfigPath(value));
        if (codeRoot is not null) AddReserved("studio", codeRoot);
        foreach (var root in _mounts.Values) AddRoot(root);
        foreach (var key in new[] { "amv_root", "projects_root" })
            if (config.TryGetProperty(key, out var root)) AddRoot(ConfigPath(root));
        if (_legacyEnabled && _legacy is not null) _roots.Add(_legacy);
        if (_roots.Count == 0) throw new InvalidDataException("No media read roots are configured.");

        if (config.TryGetProperty("path_aliases", out var aliases))
            foreach (var alias in aliases.EnumerateArray())
            {
                if (alias.ValueKind != JsonValueKind.Object || alias.EnumerateObject().Count() != 2 ||
                    !alias.TryGetProperty("from", out var source) || !alias.TryGetProperty("to", out var target))
                    throw new InvalidDataException("Invalid workspace path alias.");
                var from = Logical(RequiredString(source));
                var to = Logical(RequiredString(target));
                CheckNamespace(from, alias: true); CheckNamespace(to, alias: true);
                if (string.Equals(from, to, Comparison) || _aliases.Any(row => string.Equals(row.Source, from, Comparison)))
                    throw new InvalidDataException("Duplicate or self-referencing workspace alias.");
                _aliases.Add((from, to));
            }
        _aliases.Sort((left, right) => right.Source.Length.CompareTo(left.Source.Length));
    }

    internal string Resolve(string reference)
    {
        var path = ResolveReference(reference);
        CheckNoLinks(path);
        if ((File.GetAttributes(path) & FileAttributes.Directory) != 0)
            throw new FileNotFoundException("Media reference is not a file.", path);
        return path;
    }

    internal string ResolveReference(string reference)
    {
        var path = Logical(reference);
        var visited = new HashSet<string>(Comparer);
        for (var hops = 0; ; hops++)
        {
            CheckNamespace(path, alias: true);
            if (!visited.Add(path)) throw new InvalidDataException("Cyclic workspace aliases.");
            var match = _aliases.Find(row => Within(path, row.Source));
            if (match.Source is null) break;
            if (hops == 16) throw new InvalidDataException("Workspace alias chain exceeds its limit.");
            path = Path.GetFullPath(Path.Combine(match.Target, Path.GetRelativePath(match.Source, path)));
        }
        CheckNamespace(path, alias: false);
        return path;
    }

    private void AddReserved(string name, string root)
    {
        if (_mounts.TryGetValue(name, out var existing) && !string.Equals(existing, root, Comparison))
            throw new InvalidDataException("Conflicting reserved workspace mount.");
        _mounts[name] = root;
    }

    private void AddRoot(string root)
    {
        if (!_legacyEnabled && _legacy is not null && (Within(root, _legacy) || Within(_legacy, root)))
            throw new InvalidDataException("A media read root overlaps disabled legacy.", new DisabledLegacyOverlapException());
        _roots.Add(root);
    }

    private void CheckNamespace(string path, bool alias)
    {
        if (_protected.Any(root => Within(path, root))) throw new UnauthorizedAccessException("Protected media namespace.");
        if (alias && !_legacyEnabled && _legacy is not null && Within(path, _legacy)) return;
        if (!_legacyEnabled && _legacy is not null && Within(path, _legacy)) throw new UnauthorizedAccessException("Legacy media reads are disabled.");
        if (!_roots.Any(root => Within(path, root))) throw new UnauthorizedAccessException("Media is outside configured read roots.");
    }

    private string Logical(string reference)
    {
        Validate(reference);
        if (!reference.StartsWith('@')) return Absolute(reference, _base);
        var raw = reference[1..].Replace('\\', '/');
        var separator = raw.IndexOf('/');
        var name = separator < 0 ? raw : raw[..separator];
        var tail = separator < 0 ? "" : raw[(separator + 1)..];
        if (!_mounts.TryGetValue(name, out var root) || tail.StartsWith('/') || Path.IsPathRooted(tail) || tail.Contains(':'))
            throw new UnauthorizedAccessException("Invalid named media reference.");
        var path = Path.GetFullPath(Path.Combine(root, tail));
        if (!Within(path, root)) throw new UnauthorizedAccessException("Media escapes its named root.");
        return Path.TrimEndingDirectorySeparator(path);
    }

    private static string RequiredString(JsonElement value) => value.ValueKind == JsonValueKind.String && value.GetString() is { Length: > 0 } text
        ? text : throw new InvalidDataException("Expected a workspace path string.");

    private static string Absolute(string value, string relativeTo, bool trustedConfiguration = false)
    {
        value = Environment.ExpandEnvironmentVariables(value);
        Validate(value, trustedConfiguration);
        if (Path.IsPathRooted(value) && !Path.IsPathFullyQualified(value)) throw new UnauthorizedAccessException("Drive-relative paths are forbidden.");
        return Path.TrimEndingDirectorySeparator(Path.GetFullPath(value, relativeTo));
    }

    private static void Validate(string value, bool trustedConfiguration = false)
    {
        if (string.IsNullOrWhiteSpace(value) || value.Length > 4096 || value.Contains('\0') || value.StartsWith("\\\\", StringComparison.Ordinal) || value.StartsWith("//", StringComparison.Ordinal))
            throw new UnauthorizedAccessException("Invalid media reference.");
        var raw = value.Replace('\\', '/');
        var parts = raw.Split('/');
        if (!trustedConfiguration && parts.Any(part => part == "..")) throw new UnauthorizedAccessException("Media traversal is forbidden.");
        var colon = raw.IndexOf(':');
        if (colon >= 0 && !(colon == 1 && char.IsAsciiLetter(raw[0]) && raw.Length > 2 && raw[2] == '/' && raw.IndexOf(':', 2) < 0))
            throw new UnauthorizedAccessException("URLs, alternate streams and drive-relative paths are forbidden.");
        if (OperatingSystem.IsWindows() && parts.Any(part => part.Length != 0 && part is not "." && !(trustedConfiguration && part == "..") &&
            (part.EndsWith('.') || part.EndsWith(' ') || part.IndexOfAny(['<', '>', '"', '|', '?', '*']) >= 0 ||
             Regex.IsMatch(part, "\\A(?:CON|PRN|AUX|NUL|COM[1-9]|LPT[1-9])(?:\\..*)?\\z", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant))))
            throw new UnauthorizedAccessException("Device or ambiguous Windows media path.");
    }

    private static bool Within(string path, string root) => string.Equals(path, root, Comparison) ||
        path.StartsWith(Path.EndsInDirectorySeparator(root) ? root : root + Path.DirectorySeparatorChar, Comparison);

    internal static void CheckNoLinks(string path, bool allowMissing = false)
    {
        var root = Path.GetPathRoot(path)!;
        var cursor = root;
        foreach (var part in path[root.Length..].Split(Path.DirectorySeparatorChar, StringSplitOptions.RemoveEmptyEntries))
        {
            cursor = Path.Combine(cursor, part);
            FileAttributes attributes;
            try { attributes = File.GetAttributes(cursor); }
            catch (Exception error) when (error is FileNotFoundException or DirectoryNotFoundException)
            {
                if (allowMissing) return;
                throw new FileNotFoundException("File or directory was not found.", path, error);
            }
            if ((attributes & FileAttributes.ReparsePoint) != 0) throw new UnauthorizedAccessException("Linked paths are forbidden.");
        }
    }
}
