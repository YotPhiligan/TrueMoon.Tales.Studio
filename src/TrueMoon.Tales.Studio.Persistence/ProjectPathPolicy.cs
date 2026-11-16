namespace TrueMoon.Tales.Studio.Persistence;

internal sealed class ProjectPathPolicy
{
    private readonly string[] _writes;
    private readonly string[] _protected;
    internal static StringComparison Comparison => OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;
    internal string Root { get; }

    internal ProjectPathPolicy(ProjectStoreOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);
        ArgumentException.ThrowIfNullOrWhiteSpace(options.ProjectDirectory);
        ArgumentNullException.ThrowIfNull(options.AllowedWriteRoots);
        ArgumentNullException.ThrowIfNull(options.ProtectedRoots);
        Root = Path.TrimEndingDirectorySeparator(Path.GetFullPath(options.ProjectDirectory));
        _writes = options.AllowedWriteRoots.Select(p => Path.TrimEndingDirectorySeparator(Path.GetFullPath(p))).ToArray();
        _protected = options.ProtectedRoots.Select(p => Path.TrimEndingDirectorySeparator(Path.GetFullPath(p))).ToArray();
        Check(Root);
        if (!Directory.Exists(Root)) throw new DirectoryNotFoundException(Root);
        for (var parent = Directory.GetParent(Root); parent is not null; parent = parent.Parent) NoLink(parent.FullName);
    }

    internal string At(params string[] parts)
    {
        var path = Path.GetFullPath(Path.Combine([Root, .. parts]));
        Check(path);
        return path;
    }

    internal void Check(string path, bool write = false)
    {
        if (!Within(path, Root) || _protected.Any(p => Within(path, p))) throw new UnauthorizedAccessException("Project path is not permitted.");
        if (write && !_writes.Any(p => Within(path, p))) throw new UnauthorizedAccessException("Project write is not allowlisted.");
        var current = path;
        while (Within(current, Root))
        {
            NoLink(current);
            if (string.Equals(current, Root, Comparison)) break;
            current = Path.GetDirectoryName(current)!;
        }
    }

    internal void CreateDirectory(string path)
    {
        Check(path, true);
        Directory.CreateDirectory(path);
        Check(path, true);
    }

    private static bool Within(string path, string root) => string.Equals(path, root, Comparison) ||
        path.StartsWith(Path.EndsInDirectorySeparator(root) ? root : root + Path.DirectorySeparatorChar, Comparison);

    private static void NoLink(string path)
    {
        try
        {
            if ((File.GetAttributes(path) & FileAttributes.ReparsePoint) != 0) throw new UnauthorizedAccessException("Reparse points are not permitted for project storage.");
        }
        catch (FileNotFoundException) { }
        catch (DirectoryNotFoundException) { }
    }
}
