using TrueMoon.Tales.Studio.Configuration;
using TrueMoon.Tales.Studio.Persistence;

namespace TrueMoon.Tales.Studio.Backend;

internal sealed class BackendProjectAccess
{
    private readonly string[] _allowed, _denied, _writes;
    private readonly string[] _stateExcluded;
    private readonly string[] _codeRoots;
    internal string? ProjectsRoot { get; }
    internal string? StateRoot { get; }
    private string? CacheRoot { get; }
    internal WorkspaceSettings Settings { get; }

    internal static ProjectStoreOptions Read(BackendOptions options, WorkspaceSettings? settings = null) =>
        new BackendProjectAccess(options.ConfigPath, settings ?? WorkspaceProfile.Read(options.ConfigPath), picker: false, options.WriteEnabled)
            .ForProject(options.ProjectDirectory, options.WriteEnabled) with { ProjectFileName = options.ProjectFileName };

    internal static void CheckProductionWrite(ProjectStoreOptions project, string path)
    {
        path = Path.GetFullPath(path);
        if (!Within(path, Path.Combine(project.ProjectDirectory, "Production")) ||
            project.ProtectedRoots.Any(root => Within(path, root)) ||
            !project.AllowedWriteRoots.Any(root => Within(path, root)))
            throw new UnauthorizedAccessException("Project production write is not permitted.");
        BackendMediaResolver.CheckNoLinks(path, allowMissing: true);
    }

    internal BackendProjectAccess(string configPath, WorkspaceSettings? settings = null)
        : this(configPath, settings ?? WorkspaceProfile.Read(configPath), picker: true, write: true) { }

    private BackendProjectAccess(string configPath, WorkspaceSettings settings, bool picker, bool write)
    {
        Settings = settings;
        string Resolve(string value) => WorkspaceProfile.ResolvePath(value, configPath);
        string? Root(string? value) => value is null ? null : Resolve(value);
        ProjectsRoot = Root(settings.ProjectsRoot);
        StateRoot = picker ? Root(settings.StateRoot) : null;
        CacheRoot = picker ? Root(settings.CacheRoot) : null;
        _allowed = new[] { Root(settings.AmvRoot), ProjectsRoot }.OfType<string>().ToArray();
        var denied = settings.ProtectedRoots.Select(Resolve).ToList();
        var legacy = Root(settings.LegacyRoot);
        if (legacy is not null && !settings.LegacyReadEnabled)
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
        _codeRoots = code.ToArray();
        _writes = write ? settings.ProjectWriteRoots.Select(Resolve).ToArray() : [];
        var mounts = picker ? settings.Mounts.Where(mount => mount.Key != "state").Select(mount => Resolve(mount.Value)) : [];
        _stateExcluded = [.. _allowed, .. _denied, .. mounts, .. code, .. new[] { legacy }.OfType<string>()];
    }

    internal ProjectStoreOptions ForProject(string directory, bool write)
    {
        var project = Path.TrimEndingDirectorySeparator(Path.GetFullPath(directory));
        if (!_allowed.Any(root => Within(project, root)) || _denied.Any(root => Within(project, root)))
            throw new UnauthorizedAccessException("Project directory is outside the configured read roots or is protected.");
        return new() { ProjectDirectory = project, AllowedWriteRoots = write ? _writes : [], ProtectedRoots = _denied };
    }

    internal ProjectStoreOptions ForLocation(string path, bool write)
    {
        var options = ForProject(path, write);
        var name = Path.GetFileName(options.ProjectDirectory);
        if (!ProjectStore.IsStudioFileName(name)) return options;

        BackendMediaResolver.CheckNoLinks(options.ProjectDirectory, allowMissing: true);
        return Directory.Exists(options.ProjectDirectory) ? options :
            ForProject(Path.GetDirectoryName(options.ProjectDirectory)!, write) with { ProjectFileName = name };
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

    internal string? ProxyCache()
    {
        if (CacheRoot is null) return null;
        if (CacheRoot == Path.GetPathRoot(CacheRoot) ||
            _stateExcluded.Any(root => Within(CacheRoot, root) || Within(root, CacheRoot)))
            throw new UnauthorizedAccessException("Proxy cache overlaps a source or protected root.");
        var path = Path.Combine(CacheRoot, "StudioProxies.v1");
        BackendMediaResolver.CheckNoLinks(path, allowMissing: true);
        return path;
    }

    internal string RestoreRoot()
    {
        if (ProjectsRoot is not { } root || root == Path.GetPathRoot(root) || !AllowsProjectWrite(root) ||
            _denied.Concat(_codeRoots).Any(path => Within(root, path) || Within(path, root)))
            throw new UnauthorizedAccessException("Для восстановления нужен разрешённый каталог projects_root вне исходников Studio.");
        BackendMediaResolver.CheckNoLinks(root);
        if (!Directory.Exists(root)) throw new DirectoryNotFoundException("Каталог projects_root не найден.");
        if (new ProjectStore(ForProject(root, write: false)).HasProjectFile())
            throw new UnauthorizedAccessException("projects_root должен быть родительским каталогом для новой копии проекта.");
        return root;
    }

    internal bool AllowsProjectWrite(string folder) => _writes.Any(grant => Within(folder, grant));

    internal string ValidateProjectsFolder(string configPath)
    {
        var root = RestoreRoot();
        var separate = new[] { Settings.StateRoot, Settings.CacheRoot, Settings.ExportsRoot }.OfType<string>()
            .Select(path => WorkspaceProfile.ResolvePath(path, configPath)).Append(configPath);
        if (separate.Any(path => Within(root, path) || Within(path, root)))
            throw new UnauthorizedAccessException("Папка проектов пересекается с конфигурацией, состоянием, кэшем или экспортами Studio.");
        return root;
    }

    private static bool Within(string path, string root)
    {
        root = Path.TrimEndingDirectorySeparator(root);
        var comparison = OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;
        return string.Equals(path, root, comparison) ||
            path.StartsWith(Path.EndsInDirectorySeparator(root) ? root : root + Path.DirectorySeparatorChar, comparison);
    }
}
