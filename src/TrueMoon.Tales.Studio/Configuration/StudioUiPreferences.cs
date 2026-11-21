using System.Text.Json;
using TrueMoon.Argentis;
using TrueMoon.Configuration;
using TrueMoon.Dependencies;
using TrueMoon.Tales.Studio.Backend;

namespace TrueMoon.Tales.Studio.Configuration;

internal enum StudioUiWindow { Projects, Editor, Viewer, Workspace }

internal sealed record StudioUiSettings
{
    internal string Theme { get; set; } = "dark";
    internal int[]? ProjectsSize { get; set; }
    internal int[]? EditorSize { get; set; }
    internal int[]? ViewerSize { get; set; }
    internal int[]? WorkspaceSize { get; set; }

    internal int[]? SizeFor(StudioUiWindow role) => role switch
    {
        StudioUiWindow.Projects => ProjectsSize,
        StudioUiWindow.Editor => EditorSize,
        StudioUiWindow.Workspace => WorkspaceSize,
        _ => ViewerSize
    };
}

internal sealed class StudioUiPreferences(string? path, IApp? app, string? notice = null) : IAsyncDisposable
{
    private const string SourceId = "studio-ui";
    private const string ThemeKey = "ui:theme";
    private const string ProjectsSizeKey = "projects_size";
    private const string EditorSizeKey = "editor_size";
    private const string ViewerSizeKey = "viewer_size";
    private const string WorkspaceSizeKey = "workspace_size";
    private readonly SemaphoreSlim _updates = new(1);
    private readonly IConfigurationReader? _reader = app?.Services.Resolve<IConfigurationReader>();

    private static readonly IConfigurationSchema<StudioUiSettings> Schema = ConfigurationSchemas.Create("ui",
        () => new StudioUiSettings(), value => value with
        {
            ProjectsSize = value.ProjectsSize?.ToArray(),
            EditorSize = value.EditorSize?.ToArray(),
            ViewerSize = value.ViewerSize?.ToArray(),
            WorkspaceSize = value.WorkspaceSize?.ToArray()
        }, Validate, fields =>
        {
            fields.Field("theme", value => value.Theme, (value, theme) => value.Theme = theme,
                new(Change: ConfigurationChangeMode.Reloadable));
            fields.Field(ProjectsSizeKey, value => value.ProjectsSize, (value, size) => value.ProjectsSize = size);
            fields.Field(EditorSizeKey, value => value.EditorSize, (value, size) => value.EditorSize = size);
            fields.Field(ViewerSizeKey, value => value.ViewerSize, (value, size) => value.ViewerSize = size);
            fields.Field(WorkspaceSizeKey, value => value.WorkspaceSize, (value, size) => value.WorkspaceSize = size);
        });

    internal string? Notice { get; } = notice;
    internal StudioUiSettings Current => _reader?.Current.GetOptions(Schema) ?? new();

    internal static string ProfilePath(string localApplicationData) =>
        Path.Combine(localApplicationData, "TrueMoon.Tales", "Studio", "ui.json");

    internal static StudioUiPreferences OpenDefault()
    {
        var local = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        return local.Length > 0 ? Open(ProfilePath(local)) :
            new(null, null, "Каталог настроек интерфейса недоступен.");
    }

    internal static StudioUiPreferences Open(string path)
    {
        path = Path.GetFullPath(path);
        try
        {
            BackendMediaResolver.CheckNoLinks(path, allowMissing: true);
            var profile = ConfigurationProfiles.Create(builder =>
            {
                builder.AddSchema(Schema);
                builder.AddJsonFile(SourceId, path, optional: true);
            });
            return new(path, App.Build(context => context.UseConfiguration(profile)));
        }
        catch (Exception error) when (error is ConfigurationException or IOException or UnauthorizedAccessException)
        {
            return new(path, null, "Не удалось прочитать ui.json. Используются стандартные настройки интерфейса.");
        }
    }

    internal void Attach(Window window, StudioUiWindow role)
    {
        var settings = Current;
        if (settings.SizeFor(role) is [var width, var height]) window.ClientSize = new(width, height);
        else if (role == StudioUiWindow.Workspace && settings.ProjectsSize is [var oldWidth, var oldHeight])
            window.ClientSize = new(Math.Max(880, oldWidth), Math.Max(640, oldHeight));
        ApplyTheme(window, settings.Theme);
        var requested = window.ClientSize;
        Size lastNormal = requested;
        window.HostStateChanged += changed =>
        {
            if (changed.IsHosted && changed.State == WindowPresentationState.Normal)
            {
                var size = new Size(MathF.Round(changed.ActualClientSize.Width), MathF.Round(changed.ActualClientSize.Height));
                if (ValidSize(role, size)) lastNormal = size;
            }
        };
        window.Closing += async _ =>
        {
            if (lastNormal != requested)
            {
                try { await SaveSizeAsync(role, lastNormal).ConfigureAwait(false); }
                catch (Exception error) when (IsWriteError(error))
                { Console.Error.WriteLine("Studio: не удалось сохранить размер окна в ui.json."); }
            }
            return true;
        };
    }

    internal static void ApplyTheme(Element view, string choice)
    {
        var root = view;
        while (root.Parent is { } parent) root = parent;
        root.ApplyTheme((choice == "light" ? Theme.Light : Theme.Dark) with
        { FontSize = view.Theme.FontSize, FontFamily = view.Theme.FontFamily });
    }

    internal async Task<(string Theme, string? Notice)> SaveThemeAsync(string choice)
    {
        if (app is null) return (choice, Notice);
        await _updates.WaitAsync().ConfigureAwait(false);
        try
        {
            await WriteAsync(ThemeKey, JsonSerializer.SerializeToElement(choice)).ConfigureAwait(false);
            var result = await app.Services.Resolve<IConfigurationReloader>()!.ReloadAsync().ConfigureAwait(false);
            return result.RestartRequired
                ? (Current.Theme, "Тема сохранена. Откройте новое окно, чтобы применить настройки.")
                : (Current.Theme, null);
        }
        catch (Exception error) when (IsWriteError(error))
        { return (Current.Theme, "Не удалось сохранить тему в ui.json."); }
        finally { _updates.Release(); }
    }

    private async Task SaveSizeAsync(StudioUiWindow role, Size size)
    {
        if (app is null) return;
        var key = role switch
        {
            StudioUiWindow.Projects => ProjectsSizeKey,
            StudioUiWindow.Editor => EditorSizeKey,
            StudioUiWindow.Workspace => WorkspaceSizeKey,
            _ => ViewerSizeKey
        };
        await _updates.WaitAsync().ConfigureAwait(false);
        try { await WriteAsync("ui:" + key, JsonSerializer.SerializeToElement(new[] { (int)size.Width, (int)size.Height })).ConfigureAwait(false); }
        finally { _updates.Release(); }
    }

    private Task WriteAsync(string key, JsonElement value)
    {
        var file = path!;
        var writer = app!.Services.Resolve<IConfigurationWriter>()!;
        // Own the named mutex and its release on one worker thread.
        return Task.Run(() => StudioConfigurationWriteGate.Run(file, () =>
        {
            BackendMediaResolver.CheckNoLinks(file, allowMissing: true);
            Directory.CreateDirectory(Path.GetDirectoryName(file)!);
            writer.WriteAsync(SourceId, [new(key, value)]).AsTask().GetAwaiter().GetResult();
        }));
    }

    private static bool IsWriteError(Exception error) => error is ConfigurationException or IOException or UnauthorizedAccessException;

    private static void Validate(StudioUiSettings settings)
    {
        if (settings.Theme is not ("dark" or "light")) throw new InvalidDataException("Unknown UI theme.");
        foreach (var role in Enum.GetValues<StudioUiWindow>())
            if (settings.SizeFor(role) is { } size && (size is not [var width, var height] || !ValidSize(role, new(width, height))))
                throw new InvalidDataException("Invalid window size.");
    }

    private static bool ValidSize(StudioUiWindow role, Size size) => role switch
    {
        StudioUiWindow.Editor or StudioUiWindow.Workspace => size.Width is >= 880 and <= 32768 && size.Height is >= 640 and <= 32768,
        _ => size.Width is >= 740 and <= 32768 && size.Height is >= 480 and <= 32768
    };

    public async ValueTask DisposeAsync()
    {
        await _updates.WaitAsync().ConfigureAwait(false);
        try { if (app is not null) await app.DisposeAsync().ConfigureAwait(false); }
        finally { _updates.Dispose(); }
    }
}
