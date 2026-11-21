using System.Text.Json;
using TrueMoon.Tales.Studio.Backend;
using TrueMoon.Tales.Studio.Persistence;

namespace TrueMoon.Tales.Studio.Presentation.Projects;

internal enum ProjectAvailability
{
    Ready, Missing, MigrationRequired, Unsupported, RecoveryRequired, DuplicateLocation,
    AccessDenied, WriteDenied, NoClip, IdentityMismatch, Invalid
}

internal sealed record ProjectEntry(string Name, string Location, ProjectAvailability Availability,
    string? Directory = null, string? Id = null, int? Schema = null, long? Revision = null,
    string? Fingerprint = null, bool Recent = false, string? ProjectFileName = null)
{
    internal bool OpensReadOnly => Availability == ProjectAvailability.MigrationRequired ||
        Availability == ProjectAvailability.WriteDenied && Schema is 1 or 2;
    internal bool CanOpen => OpensReadOnly || Availability is ProjectAvailability.Ready or ProjectAvailability.NoClip;
    internal bool CanMaintain => Availability is ProjectAvailability.MigrationRequired or ProjectAvailability.RecoveryRequired;
    internal string Details => Availability switch
    {
        ProjectAvailability.Ready => "Готов к открытию",
        ProjectAvailability.Missing => "Файл проекта не найден",
        ProjectAvailability.MigrationRequired => "Только чтение. Для редактирования выполните миграцию.",
        ProjectAvailability.Unsupported => "Неподдерживаемая схема проекта",
        ProjectAvailability.RecoveryRequired => "Обнаружена незавершённая запись. Требуется восстановление.",
        ProjectAvailability.DuplicateLocation => "Найдено несколько файлов проекта. Оставьте один файл.",
        ProjectAvailability.AccessDenied => "Путь запрещён или содержит ссылку",
        ProjectAvailability.WriteDenied when Schema == 1 => "Только чтение. Для миграции настройте project_write_roots.",
        ProjectAvailability.WriteDenied when OpensReadOnly => "Только чтение. Для редактирования настройте project_write_roots.",
        ProjectAvailability.WriteDenied => "Операция требует разрешения в project_write_roots.",
        ProjectAvailability.NoClip => "Доступен пустой таймлайн",
        ProjectAvailability.IdentityMismatch => "ID проекта не совпадает с сохранённой записью",
        _ => "Некорректный файл или запись проекта"
    };

    internal void VerifySelection(string? id, string fingerprint)
    {
        if (id != Id || !string.Equals(fingerprint, Fingerprint, StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException("Проект изменился после просмотра списка. Обновите список и выберите его снова.");
    }
}

internal sealed record ProjectListing(ProjectEntry[] Items, string[] Notices);

internal sealed partial class ProjectCatalog(string configPath)
{
    internal static StringComparer PathComparer { get; } =
        OperatingSystem.IsWindows() ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal;
    internal string ConfigPath { get; } = Path.GetFullPath(configPath);

    internal async Task<ProjectListing> ReadAsync(CancellationToken token)
    {
        var access = new BackendProjectAccess(ConfigPath);
        var resolver = Resolver(access);
        var entries = new List<ProjectEntry>();
        var known = new HashSet<string>(PathComparer);
        var notices = new List<string>();

        foreach (var row in ReadSource("Недавние проекты", () => ProjectRecents.Read(access, ConfigPath), notices))
            await AddAsync(row.Directory, row.Name, row.Id, recent: true).ConfigureAwait(false);

        foreach (var row in ReadSource("Реестр проектов", () => ReadRegistry(access), notices))
        {
            if (row is { } valid) await AddAsync(valid.Root, valid.Id, valid.Id).ConfigureAwait(false);
            else entries.Add(new("Запись реестра", "Registry.json", ProjectAvailability.Invalid));
        }

        if (access.ProjectsRoot is { } root)
            foreach (var directory in ReadSource("Каталог проектов", () => Directories(access, root), notices))
                await AddAsync(directory, Path.GetFileName(directory), null).ConfigureAwait(false);

        var ordered = entries.OrderByDescending(item => item.Recent)
            .ThenBy(item => item.Recent ? "" : item.Name, StringComparer.CurrentCultureIgnoreCase).ToArray();
        return new(ordered, notices.ToArray());

        async Task AddAsync(string reference, string name, string? id, bool recent = false)
        {
            token.ThrowIfCancellationRequested();
            var item = await InspectAsync(access, resolver, reference, name, id, token).ConfigureAwait(false);
            if (item.Directory is null || item.Availability == ProjectAvailability.IdentityMismatch || known.Add(item.Directory))
                entries.Add(item with { Recent = recent });
        }
    }

    internal async Task<BackendOptions> PrepareAsync(ProjectEntry selected, CancellationToken token, bool maintenance = false)
    {
        if (!(maintenance ? selected.CanMaintain : selected.CanOpen) || selected.Directory is null)
            throw new InvalidDataException(selected.Details);
        var access = new BackendProjectAccess(ConfigPath);
        var reference = selected.ProjectFileName is { } name ? Path.Combine(selected.Directory, name) : selected.Directory;
        var current = await InspectAsync(access, Resolver(access), reference, selected.Name, selected.Id, token,
                fileOrFolder: selected.ProjectFileName is not null)
            .ConfigureAwait(false);
        if (maintenance ? current.Availability != selected.Availability
            : !current.CanOpen || current.OpensReadOnly != selected.OpensReadOnly)
            throw new InvalidDataException("Состояние проекта изменилось. Перечитайте список.");
        if (!PathComparer.Equals(selected.Directory, current.Directory) || !PathComparer.Equals(selected.ProjectFileName, current.ProjectFileName))
            throw new InvalidDataException("Расположение проекта изменилось. Обновите список.");
        selected.VerifySelection(current.Id, current.Fingerprint!);
        var readOnly = !maintenance && current.OpensReadOnly;
        return BackendOptions.Create(current.Directory!, ConfigPath) with
        {
            Action = current.Availability switch
            {
                _ when readOnly => "read",
                ProjectAvailability.MigrationRequired => "migrate",
                ProjectAvailability.RecoveryRequired => "recover",
                _ => "edit"
            },
            ProjectFileName = current.ProjectFileName,
            WriteEnabled = !readOnly, InitialFingerprint = current.Fingerprint,
            RecoveryFingerprint = current.Availability == ProjectAvailability.RecoveryRequired ? current.Fingerprint : null
        };
    }

    internal async Task<ProjectEntry> InspectFolderAsync(string folder, CancellationToken token)
    {
        var path = folder.Trim();
        if (path is ['"', .., '"']) path = path[1..^1];
        if (!Path.IsPathFullyQualified(path))
            throw new ArgumentException("Укажите полный путь к папке или файлу .studio.");

        var access = new BackendProjectAccess(ConfigPath);
        var project = await InspectAsync(access, Resolver(access), path, path, null, token, fileOrFolder: true).ConfigureAwait(false);
        if (!project.CanOpen)
            throw new InvalidDataException(project.Availability == ProjectAvailability.AccessDenied
                ? "Папка вне разрешённых корней проектов (projects_root, amv_root), защищена или содержит ссылку."
                : project.Details);
        return project;
    }

    internal void Remember(ProjectEntry entry) => ProjectRecents.Remember(new BackendProjectAccess(ConfigPath), ConfigPath, entry);

    private BackendMediaResolver Resolver(BackendProjectAccess access) =>
        new(BackendOptions.Create(access.ProjectsRoot ?? Path.GetDirectoryName(ConfigPath)!, ConfigPath));

    private static async Task<ProjectEntry> InspectAsync(BackendProjectAccess access, BackendMediaResolver resolver,
        string reference, string name, string? expectedId, CancellationToken token, bool fileOrFolder = false)
    {
        var entry = new ProjectEntry(name, reference, ProjectAvailability.Invalid);
        try
        {
            var path = resolver.ResolveReference(reference);
            var storage = fileOrFolder ? access.ForLocation(path, write: true) : access.ForProject(path, write: true);
            var directory = storage.ProjectDirectory;
            entry = entry with { Location = directory, Directory = directory, ProjectFileName = storage.ProjectFileName };
            var store = new ProjectStore(storage);
            ProjectFileDocument document;
            try { document = await store.OpenAsync(token).ConfigureAwait(false); }
            catch (ProjectRecoveryRequiredException)
            {
                try { store.VerifyWriteAccess(); }
                catch (UnauthorizedAccessException) { return entry with { Availability = ProjectAvailability.WriteDenied }; }
                var recovery = await store.ReadRecoveryIdentityAsync(token).ConfigureAwait(false);
                return entry with
                {
                    Availability = expectedId is null || expectedId == recovery.Id
                        ? ProjectAvailability.RecoveryRequired : ProjectAvailability.IdentityMismatch,
                    Id = recovery.Id, Name = recovery.Name, Fingerprint = recovery.Fingerprint
                };
            }
            catch (ProjectFileConflictException)
            { return entry with { Availability = ProjectAvailability.DuplicateLocation }; }
            var project = document.Project;
            entry = entry with
            {
                Name = project.Name, Id = project.Id, Schema = document.SchemaVersion,
                Revision = project.Revision, Fingerprint = document.Fingerprint
            };
            if (expectedId is not null && expectedId != project.Id)
                return entry with { Availability = ProjectAvailability.IdentityMismatch };
            try { store.VerifyWriteAccess(); }
            catch (UnauthorizedAccessException) { return entry with { Availability = ProjectAvailability.WriteDenied }; }
            if (document.SchemaVersion == 1)
                return entry with { Availability = ProjectAvailability.MigrationRequired };
            if (!BackendSceneMapping.HasVideoClip(project))
                return entry with { Availability = ProjectAvailability.NoClip };
            BackendSceneMapping.Map(project, StudioBackendService.ScenarioActive(document), null);
            return entry with { Availability = ProjectAvailability.Ready };
        }
        catch (Exception error) when (IsDataError(error))
        {
            var state = error switch
            {
                ProjectRecoveryRequiredException => ProjectAvailability.RecoveryRequired,
                FileNotFoundException or DirectoryNotFoundException => ProjectAvailability.Missing,
                UnauthorizedAccessException => ProjectAvailability.AccessDenied,
                NotSupportedException => ProjectAvailability.Unsupported,
                _ => ProjectAvailability.Invalid
            };
            return entry with { Availability = state };
        }
    }

    private static string[] Directories(BackendProjectAccess access, string root)
    {
        access.ForProject(root, write: false);
        BackendMediaResolver.CheckNoLinks(root, allowMissing: true);
        return Directory.Exists(root)
            ? Directory.GetDirectories(root).Where(directory => !Path.GetFileName(directory)
                .StartsWith(ProjectStore.CreationStagingPrefix, StringComparison.OrdinalIgnoreCase)).Order(PathComparer).ToArray() : [];
    }

    private static (string Id, string Root)?[] ReadRegistry(BackendProjectAccess access)
    {
        using var input = ProjectRecents.OpenOptional(access.StatePath("Registry.json"));
        if (input is null) return [];
        using var document = JsonDocument.Parse(input);
        if (!document.RootElement.TryGetProperty("projects", out var rows) || rows.ValueKind != JsonValueKind.Array)
            throw new InvalidDataException("Ожидается массив projects.");
        return rows.EnumerateArray().Select(row =>
        {
            if (row.ValueKind == JsonValueKind.Object && row.TryGetProperty("id", out var id) && row.TryGetProperty("root", out var root) &&
                id.ValueKind == JsonValueKind.String && root.ValueKind == JsonValueKind.String &&
                id.GetString() is { Length: > 0 and <= 80 } key && key.All(c => char.IsAsciiLetterOrDigit(c) || c is '-' or '_') &&
                root.GetString() is { Length: > 0 and <= 4096 } path) return ((string Id, string Root)?)(key, path);
            return null;
        }).ToArray();
    }

    private static T[] ReadSource<T>(string source, Func<T[]> read, List<string> notices)
    {
        try { return read(); }
        catch (Exception error) when (IsDataError(error))
        {
            notices.Add($"{source} — {error.Message}");
            return [];
        }
    }

    internal static bool IsDataError(Exception error) => error is IOException or UnauthorizedAccessException or
        JsonException or InvalidDataException or InvalidOperationException or ArgumentException or NotSupportedException or KeyNotFoundException;
}
