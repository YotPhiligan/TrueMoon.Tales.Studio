using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using TrueMoon.Configuration;
using TrueMoon.Dependencies;
using TrueMoon.Tales.Studio.Backend;
using TrueMoon.Tales.Studio.Configuration;
using TrueMoon.Tales.Studio.Contracts.Projects;
using TrueMoon.Tales.Studio.Persistence;

namespace TrueMoon.Tales.Studio.Presentation.Projects;

internal sealed record NewProject(string Name, int Width, int Height, int Fps);

internal sealed partial class ProjectCatalog
{
    internal async Task SaveProjectsRootAsync(string selection, string? expectedRoot, CancellationToken token)
    {
        var input = selection.Trim();
        if (input is ['"', .., '"']) input = input[1..^1];
        if (!Path.IsPathFullyQualified(input))
            throw new ArgumentException("Укажите полный путь к папке проектов.");
        BackendMediaResolver.Validate(input, trustedConfiguration: true);
        var folder = Path.TrimEndingDirectorySeparator(Path.GetFullPath(input));
        var observed = await File.ReadAllTextAsync(ConfigPath, token).ConfigureAwait(false);
        try
        {
            await using var app = App.Build(context => context.UseConfiguration(WorkspaceProfile.Create(ConfigPath, observed)));
            var settings = app.Services.Resolve<IConfigurationReader>()!.Current.GetOptions(WorkspaceProfile.Schema);
            var access = new BackendProjectAccess(ConfigPath, settings);
            if (!PathComparer.Equals(access.ProjectsRoot, expectedRoot))
                throw new IOException("Папка проектов уже изменена. Отмените правку и обновите список.");
            if (PathComparer.Equals(access.ProjectsRoot, folder)) return;

            var edits = new List<ConfigurationEdit> { new(WorkspaceProfile.ProjectsRootKey, JsonSerializer.SerializeToElement(folder)) };
            settings.ProjectsRoot = folder;
            if (!access.AllowsProjectWrite(folder))
            {
                settings.ProjectWriteRoots = [.. settings.ProjectWriteRoots, folder];
                edits.Add(new(WorkspaceProfile.ProjectWriteRootsKey, JsonSerializer.SerializeToElement(settings.ProjectWriteRoots)));
            }
            var candidate = new BackendProjectAccess(ConfigPath, settings);
            var root = candidate.ValidateProjectsFolder(ConfigPath);
            if (!PathComparer.Equals(Resolver(candidate).ResolveReference(root), root))
                throw new UnauthorizedAccessException("Псевдоним workspace меняет путь папки проектов.");
            await app.Services.Resolve<IConfigurationWriter>()!.WriteAsync(WorkspaceProfile.SourceId, edits, token).ConfigureAwait(false);
        }
        catch (ConfigurationException error) when (error.Issues.Any(issue => issue.Code == "SourceWrite"))
        {
            if (!File.Exists(ConfigPath) || !string.Equals(observed, await File.ReadAllTextAsync(ConfigPath, token).ConfigureAwait(false), StringComparison.Ordinal))
                throw new IOException("Конфигурация изменилась. Отмените правку и обновите список.", error);
            throw new IOException("Не удалось сохранить настройки. Проверьте права записи и повторите попытку.", error);
        }
        catch (ConfigurationException error) { throw new InvalidDataException(WorkspaceProfile.Describe(error), error); }
    }

    internal async Task<ProjectEntry> CreateAsync(NewProject draft, CancellationToken token)
    {
        var name = draft.Name.Trim();
        if (name.Length is < 1 or > 160 || name.Any(char.IsControl))
            throw new ArgumentException("Укажите название проекта длиной до 160 символов.");
        if (draft.Width is < 2 or > 4096 || draft.Height is < 2 or > 4096 || draft.Width % 2 != 0 || draft.Height % 2 != 0)
            throw new ArgumentException("Ширина и высота должны быть чётными числами от 2 до 4096.");
        if (draft.Fps is < 1 or > 120)
            throw new ArgumentException("Укажите FPS целым числом от 1 до 120.");

        var identity = NameKey(name);
        var access = new BackendProjectAccess(ConfigPath);
        var root = access.ProjectsRoot ?? throw new InvalidDataException("Настройте projects_root для создания проекта.");
        var storage = access.ForProject(root, write: true);
        var store = new ProjectStore(storage);
        var existing = await ReadAsync(token).ConfigureAwait(false);
        if (existing.Items.Any(row => row.Id is not null && NameKey(row.Name) == identity))
            throw new InvalidDataException("Проект с таким названием уже существует.");

        // One portable destination for case/Unicode-equivalent names. The final rename arbitrates concurrent creates.
        var hash = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(identity))).ToLowerInvariant();
        var directoryName = "project-" + hash[..24];
        var directory = Path.Combine(root, directoryName);
        if (!PathComparer.Equals(Resolver(access).ResolveReference(directory), directory))
            throw new UnauthorizedAccessException("Псевдоним workspace меняет путь нового проекта.");
        var project = new ProjectContract
        {
            Id = "project-" + Guid.NewGuid().ToString("N"), Name = name, Revision = 1, Fps = draft.Fps,
            Properties = new Dictionary<string, JsonElement>
            {
                ["output_size"] = JsonSerializer.SerializeToElement(new[] { draft.Width, draft.Height })
            }
        };
        var created = await store.CreateChildAsync(directoryName, project, token).ConfigureAwait(false);
        return new(name, directory, ProjectAvailability.NoClip, directory, project.Id, 2, 1, created.Fingerprint);
    }

    internal async Task<ProjectEntry> MaintainAsync(ProjectEntry selected, CancellationToken token, string? executablePath = null)
    {
        var (options, _) = await PrepareAsync(selected, token, maintenance: true).ConfigureAwait(false);
        var snapshot = await BackendProjectMaintenance.ApplyAsync(options with { ExecutablePath = executablePath }, token).ConfigureAwait(false);
        if (snapshot.Project.Id != selected.Id) throw new InvalidDataException("ID проекта изменился. Обновите список.");
        var access = new BackendProjectAccess(ConfigPath);
        return await InspectAsync(access, Resolver(access), options.ProjectDirectory, snapshot.Project.Name, snapshot.Project.Id, token)
            .ConfigureAwait(false);
    }

    private static string NameKey(string name) => name.Trim().Normalize(NormalizationForm.FormC).ToUpperInvariant();
}
