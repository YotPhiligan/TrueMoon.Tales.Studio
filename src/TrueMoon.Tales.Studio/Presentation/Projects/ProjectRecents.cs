using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using TrueMoon.Tales.Studio.Backend;

namespace TrueMoon.Tales.Studio.Presentation.Projects;

internal static class ProjectRecents
{
    private const int Limit = 12;
    internal sealed record Entry(string Directory, string Id, string Name);

    internal static Entry[] Read(BackendProjectAccess access, string config)
    {
        using var input = OpenOptional(access.StatePath(FileName(config)));
        if (input is null) return [];
        using var document = JsonDocument.Parse(input);
        if (document.RootElement.ValueKind != JsonValueKind.Array)
            throw new InvalidDataException("Некорректный список недавних проектов.");
        return document.RootElement.EnumerateArray().Take(Limit).Select(row =>
        {
            if (row.ValueKind != JsonValueKind.Object || ReadString(row, "Directory", 4096) is not { } directory ||
                ReadString(row, "Id", 80) is not { } id || ReadString(row, "Name", 1024) is not { } name) return null;
            return new Entry(directory, id, name);
        }).OfType<Entry>().ToArray();
    }

    internal static void Remember(BackendProjectAccess access, string config, ProjectEntry entry)
    {
        var name = FileName(config);
        using var mutex = new Mutex(false, "TrueMoon.Studio." + name);
        try
        {
            if (!mutex.WaitOne(TimeSpan.FromSeconds(2))) throw new IOException("Список недавних проектов занят.");
        }
        catch (AbandonedMutexException) { }
        try
        {
            var path = access.StatePath(name);
            var next = new Entry(entry.Directory!, entry.Id!, entry.Name);
            Entry[] existing;
            try { existing = Read(access, config); }
            catch (JsonException) { existing = []; }
            catch (InvalidDataException) { existing = []; }
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            var staging = access.StatePath(name + "." + Guid.NewGuid().ToString("N") + ".tmp");
            try
            {
                using (var stream = new FileStream(staging, FileMode.CreateNew, FileAccess.Write, FileShare.None))
                    JsonSerializer.Serialize(stream, existing.Where(item => !ProjectCatalog.PathComparer.Equals(item.Directory, next.Directory))
                        .Prepend(next).Take(Limit).ToArray());
                File.Move(staging, access.StatePath(name), overwrite: true);
            }
            finally { File.Delete(access.StatePath(Path.GetFileName(staging))); }
        }
        finally { mutex.ReleaseMutex(); }
    }

    internal static FileStream? OpenOptional(string path)
    {
        FileStream stream;
        try { stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read | FileShare.Delete); }
        catch (FileNotFoundException) { return null; }
        catch (DirectoryNotFoundException) { return null; }
        if (stream.Length <= 4 * 1024 * 1024) return stream;
        stream.Dispose();
        throw new InvalidDataException("Файл списка проектов больше 4 МиБ.");
    }

    private static string? ReadString(JsonElement row, string name, int limit) =>
        row.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String &&
        value.GetString() is { Length: > 0 } text && text.Length <= limit ? text : null;

    private static string FileName(string config)
    {
        var key = OperatingSystem.IsWindows() ? config.ToUpperInvariant() : config;
        return $"Projects.recent.{Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(key)))}.json";
    }
}
