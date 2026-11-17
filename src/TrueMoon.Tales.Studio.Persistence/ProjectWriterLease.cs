namespace TrueMoon.Tales.Studio.Persistence;

/// <summary>Exclusive cooperating writer session, also observed by the legacy Python commit adapter.</summary>
/// <remarks>The OS releases ownership on process exit. The persistent lock file is never deleted or used as PID evidence.</remarks>
public sealed class ProjectWriterLease : IDisposable
{
    private readonly FileStream _file;
    private ProjectWriterLease(FileStream file) => _file = file;

    /// <summary>Claims the allowlisted project until disposal; callers serialize commands within this session.</summary>
    /// <param name="options">Trusted project directory and write grants.</param>
    /// <returns>The owned session lease.</returns>
    public static ProjectWriterLease Acquire(ProjectStoreOptions options)
    {
        if (OperatingSystem.IsMacOS()) throw new PlatformNotSupportedException("Project writer handoff is supported on Windows and Linux.");
        var paths = new ProjectPathPolicy(options);
        var path = paths.At(".Studio.project.writer.lock");
        paths.Check(path, true);
        FileStream? file = null;
        try
        {
            file = new(path, FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None);
            // Byte-range locking interoperates with msvcrt.locking / POSIX lockf in Python.
            file.Lock(0, 1);
            return new(file);
        }
        catch (IOException error) { file?.Dispose(); throw new ProjectStoreBusyException(error); }
        catch { file?.Dispose(); throw; }
    }

    /// <summary>Releases ownership without removing the shared lock inode.</summary>
    public void Dispose() => _file.Dispose();
}
