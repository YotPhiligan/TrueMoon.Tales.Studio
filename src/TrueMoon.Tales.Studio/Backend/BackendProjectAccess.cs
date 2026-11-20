using System.Text.Json;
using TrueMoon.Tales.Studio.Persistence;

namespace TrueMoon.Tales.Studio.Backend;

internal sealed class BackendProjectAccess
{
    private readonly string[] _allowed, _denied, _writes;
    private readonly string[] _stateExcluded;
    internal string? ProjectsRoot { get; }
    internal string? StateRoot { get; }

    internal static ProjectStoreOptions Read(BackendOptions options) =>
        new BackendProjectAccess(options.ConfigPath, picker: false, options.WriteEnabled)
            .ForProject(options.ProjectDirectory, options.WriteEnabled);

    internal static void CheckProductionWrite(ProjectStoreOptions project, string path)
    {
        path = Path.GetFullPath(path);
        if (!Within(path, Path.Combine(project.ProjectDirectory, "Production")) ||
            project.ProtectedRoots.Any(root => Within(path, root)) ||
            !project.AllowedWriteRoots.Any(root => Within(path, root)))
            throw new UnauthorizedAccessException("Project production write is not permitted.");
        BackendMediaResolver.CheckNoLinks(path, allowMissing: true);
    }

    internal BackendProjectAccess(string configPath) : this(configPath, picker: true, write: true) { }

    private BackendProjectAccess(string configPath, bool picker, bool write)
    {
        using var document = JsonDocument.Parse(File.ReadAllText(configPath));
        var config = document.RootElement;
        if (config.GetProperty("version").GetInt32() != 1) throw new InvalidDataException("Unsupported workspace configuration version.");
        var configDirectory = Path.GetDirectoryName(configPath)!;
        string Resolve(JsonElement value) => Path.TrimEndingDirectorySeparator(
            Path.GetFullPath(Environment.ExpandEnvironmentVariables(value.GetString()!), configDirectory));
        string? Root(string key) => config.TryGetProperty(key, out var value) ? Resolve(value) : null;
        ProjectsRoot = Root("projects_root");
        StateRoot = picker ? Root("state_root") : null;
        _allowed = new[] { Root("amv_root"), ProjectsRoot }.OfType<string>().ToArray();
        var denied = config.TryGetProperty("protected_roots", out var roots)
            ? roots.EnumerateArray().Select(Resolve).ToList() : [];
        var legacy = Root("legacy_root");
        if (legacy is not null && config.TryGetProperty("legacy_read_enabled", out var enabled) && !enabled.GetBoolean())
            denied.Add(legacy);
        // Preserve Studio's automatic exclusion even with a configuration outside the source tree.
        var code = new List<string>();
        foreach (var start in new[] { AppContext.BaseDirectory, Environment.CurrentDirectory })
            for (var directory = new DirectoryInfo(start); directory is not null; directory = directory.Parent)
                if (File.Exists(Path.Combine(directory.FullName, "TrueMoon.Tales.Studio.slnx")))
                {
                    denied.Add(Path.GetFullPath(Path.Combine(directory.FullName, "..", "..", "TrueMoon")));
                    if (picker && directory.Parent is { } parent) code.Add(parent.FullName);
                }
        _denied = denied.ToArray();
        _writes = write && config.TryGetProperty("project_write_roots", out var grants)
            ? grants.EnumerateArray().Select(Resolve).ToArray() : [];
        var mounts = picker && config.TryGetProperty("mounts", out var values)
            ? values.EnumerateObject().Where(mount => mount.Name != "state").Select(mount => Resolve(mount.Value)) : [];
        _stateExcluded = [.. _allowed, .. _denied, .. mounts, .. code, .. new[] { legacy }.OfType<string>()];
    }

    internal ProjectStoreOptions ForProject(string directory, bool write)
    {
        var project = Path.TrimEndingDirectorySeparator(Path.GetFullPath(directory));
        if (!_allowed.Any(root => Within(project, root)) || _denied.Any(root => Within(project, root)))
            throw new UnauthorizedAccessException("Project directory is outside the configured read roots or is protected.");
        return new() { ProjectDirectory = project, AllowedWriteRoots = write ? _writes : [], ProtectedRoots = _denied };
    }

    internal string StatePath(string name)
    {
        if (StateRoot is null) throw new InvalidDataException("Настройте state_root для списка проектов.");
        if (StateRoot == Path.GetPathRoot(StateRoot) ||
            _stateExcluded.Any(root => Within(StateRoot, root) || Within(root, StateRoot)))
            throw new UnauthorizedAccessException("Файл состояния пересекается с защищённым каталогом или каталогом исходных данных.");
        var path = Path.Combine(StateRoot, name);
        BackendMediaResolver.CheckNoLinks(path, allowMissing: true);
        return path;
    }

    private static bool Within(string path, string root)
    {
        root = Path.TrimEndingDirectorySeparator(root);
        var comparison = OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;
        return string.Equals(path, root, comparison) ||
            path.StartsWith(Path.EndsInDirectorySeparator(root) ? root : root + Path.DirectorySeparatorChar, comparison);
    }
}
