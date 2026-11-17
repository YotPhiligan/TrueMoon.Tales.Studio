using System.Text.Json;
using TrueMoon.Tales.Studio.Persistence;

namespace TrueMoon.Tales.Studio.Backend;

internal static class BackendProjectAccess
{
    internal static ProjectStoreOptions Read(BackendOptions options)
    {
        using var document = JsonDocument.Parse(File.ReadAllText(options.ConfigPath));
        var config = document.RootElement;
        if (config.GetProperty("version").GetInt32() != 1) throw new InvalidDataException("Unsupported workspace configuration version.");
        var configDirectory = Path.GetDirectoryName(options.ConfigPath)!;
        string Resolve(string value) => Path.GetFullPath(Environment.ExpandEnvironmentVariables(value), configDirectory);
        var allowed = new[] { "amv_root", "projects_root" }.Where(key => config.TryGetProperty(key, out _))
            .Select(key => Resolve(config.GetProperty(key).GetString()!)).ToArray();
        var denied = config.TryGetProperty("protected_roots", out var roots)
            ? roots.EnumerateArray().Select(value => Resolve(value.GetString()!)).ToList() : [];
        if (config.TryGetProperty("legacy_read_enabled", out var enabled) && !enabled.GetBoolean() &&
            config.TryGetProperty("legacy_root", out var legacy)) denied.Add(Resolve(legacy.GetString()!));
        // Preserve Studio's automatic exclusion even with a configuration outside the source tree.
        foreach (var start in new[] { AppContext.BaseDirectory, Environment.CurrentDirectory })
            for (var directory = new DirectoryInfo(start); directory is not null; directory = directory.Parent)
                if (File.Exists(Path.Combine(directory.FullName, "TrueMoon.Tales.Studio.slnx")))
                    denied.Add(Path.GetFullPath(Path.Combine(directory.FullName, "..", "..", "TrueMoon")));
        var project = Path.TrimEndingDirectorySeparator(Path.GetFullPath(options.ProjectDirectory));
        if (!allowed.Any(root => Within(project, root)) || denied.Any(root => Within(project, root)))
            throw new UnauthorizedAccessException("Project directory is outside the configured read roots or is protected.");
        var writes = options.WriteEnabled && config.TryGetProperty("project_write_roots", out var grants)
            ? grants.EnumerateArray().Select(value => Resolve(value.GetString()!)).ToArray() : [];
        return new() { ProjectDirectory = project, AllowedWriteRoots = writes, ProtectedRoots = denied.ToArray() };
    }

    private static bool Within(string path, string root)
    {
        root = Path.TrimEndingDirectorySeparator(root);
        var comparison = OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;
        return string.Equals(path, root, comparison) || path.StartsWith(root + Path.DirectorySeparatorChar, comparison);
    }
}
