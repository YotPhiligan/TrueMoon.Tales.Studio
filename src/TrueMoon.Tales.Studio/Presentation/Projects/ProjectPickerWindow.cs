using TrueMoon.Alloy.Runtime;
using TrueMoon.Argentis;
using TrueMoon.Tales.Studio.Presentation.Shell;
using System.Globalization;

namespace TrueMoon.Tales.Studio.Presentation.Projects;

internal sealed class ProjectPickerWindow : Window, IUiFrameParticipant, IUiInputObserver
{
    private enum FormMode { Create, OpenFolder, ProjectsFolder, Maintenance }

    private static readonly ViewStyleRole<Border> RowRole = new("studio.projects.row");
    private static readonly ViewStyleRole<Text> LocationRole = new("studio.projects.location");
    private static readonly ViewStyleRole<Text> MetadataRole = new("studio.projects.metadata");
    private readonly ProjectCatalog _catalog;
    private readonly Action<ProjectEntry> _openProject;
    private readonly CancellationTokenSource _closing = new();
    private readonly Text _status = new(), _details = new(), _notices = new();
    private readonly VStack _list = VStack.Create().Configure(stack => stack.Spacing = 8);
    private readonly ScrollViewer _scroll;
    private readonly VStack _form;
    private readonly Text _formTitle = new(), _formPath = new(), _confirmation = new();
    private readonly VStack _newFields;
    private readonly VStack _folderFields;
    private readonly VStack _projectsFolderFields;
    private readonly StudioHeading _browseActions;
    private readonly HStack _formActions;
    private readonly List<(ProjectEntry Entry, Button Button)> _rows = [];
    private Task<ProjectListing>? _loading;
    private Task<ProjectEntry?>? _changing;
    private ProjectEntry? _selected;
    private string? _projectsRoot;
    private bool _canChangeProjectsRoot;
    private bool _projectsRootSaved;
    private string? _openingError;
    private bool _restoreFocus;
    private FormMode _mode;
    private bool _focusForm;
    private readonly Func<ProjectEntry, CancellationToken, Task<ProjectEntry>> _maintain;

    internal Button RefreshButton { get; } = Button.Simple("Обновить список");
    internal Button OpenButton { get; } = Button.Simple("Открыть");
    internal Button NewButton { get; } = Button.Simple("Создать проект");
    internal Button OpenExistingButton { get; } = Button.Simple("Открыть существующий проект");
    internal Button ProjectsFolderButton { get; } = Button.Simple("Папка проектов");
    internal Button MaintenanceButton { get; } = Button.Simple("");
    internal Button ConfirmButton { get; } = Button.Simple("");
    internal Button CancelButton { get; } = Button.Simple("Отмена");
    internal TextBox NameInput { get; } = new() { AccessibleName = "Название проекта" };
    internal TextBox FolderInput { get; } = new() { AccessibleName = "Путь к папке проекта или файлу .studio" };
    internal TextBox ProjectsFolderInput { get; } = new() { AccessibleName = "Путь к папке проектов" };
    internal TextBox WidthInput { get; } = new() { Value = "1280", AccessibleName = "Ширина" };
    internal TextBox HeightInput { get; } = new() { Value = "736", AccessibleName = "Высота" };
    internal TextBox FpsInput { get; } = new() { Value = "24", AccessibleName = "FPS" };
    internal IReadOnlyList<(ProjectEntry Entry, Button Button)> Rows => _rows;
    internal ProjectEntry? Selected => _selected;
    internal string Status => _status.Value;

    internal ProjectPickerWindow(ProjectCatalog catalog, Action<ProjectEntry> openProject,
        ProjectEntry? previous = null, string? openingError = null,
        Func<ProjectEntry, CancellationToken, Task<ProjectEntry>>? maintain = null)
    {
        _catalog = catalog;
        _openProject = openProject;
        _maintain = maintain ?? ((entry, token) => catalog.MaintainAsync(entry, token));
        _selected = previous;
        _openingError = openingError;
        Title = "Проекты · TrueMoon Tales Studio";
        ClientSize = new(1000, 720);

        RefreshButton.Click += StartRead;
        OpenButton.Tone = ControlTone.Accent;
        OpenButton.Click += Open;
        NewButton.Click += () => ShowForm(FormMode.Create);
        OpenExistingButton.Click += () => ShowForm(FormMode.OpenFolder);
        ProjectsFolderButton.Click += () => ShowForm(FormMode.ProjectsFolder);
        MaintenanceButton.Click += () => ShowForm(FormMode.Maintenance);
        ConfirmButton.Tone = ControlTone.Accent;
        ConfirmButton.Click += Confirm;
        CancelButton.Click += HideForm;
        var title = Text.Plain("Проекты");
        title.FontSize = 26;
        var configuration = PathLabel(catalog.ConfigPath);
        configuration.AccessibleName = "Конфигурация: " + catalog.ConfigPath;
        var actions = HStack.Create().Configure(stack => stack.Spacing = 10).WithChildren(NewButton, OpenExistingButton, ProjectsFolderButton);
        var header = Stack(12, new StudioHeading(title, RefreshButton), actions, configuration, _notices);
        _browseActions = new(_details, HStack.Create().WithChildren(OpenButton, MaintenanceButton));
        _formActions = HStack.Create().Configure(stack => stack.Spacing = 10).WithChildren(ConfirmButton, CancelButton);
        _formActions.IsVisible = false;
        var footer = Stack(10, _status, _browseActions, _formActions);
        _newFields = Stack(10, Text.Plain("Название"), NameInput,
            new StudioHeading(Text.Plain("Ширина, пикселей"), WidthInput),
            new StudioHeading(Text.Plain("Высота, пикселей"), HeightInput),
            new StudioHeading(Text.Plain("Кадров в секунду"), FpsInput));
        _folderFields = Stack(10, Text.Plain("Полный путь к папке проекта или файлу .studio"), FolderInput);
        _projectsFolderFields = Stack(10, Text.Plain("Полный путь к существующей папке"), ProjectsFolderInput,
            Text.Plain("Studio сможет создавать проекты и сохранять изменения в этой папке."));
        _formTitle.FontSize = 20;
        _form = Stack(14, _formTitle, _formPath.WithRole(LocationRole), _newFields, _folderFields, _projectsFolderFields, _confirmation);
        _form.IsVisible = false;
        _scroll = ScrollViewer.Create().Content(Stack(0, _list, _form));
        Closing += _ => Task.FromResult(_changing is null);
        SetContent(Border.Create().Configure(border => border.Padding = new Thickness(24))
            .Content(new ProjectPickerLayout(header, _scroll, footer)));
        this.WithViewStyle(new PickerStyle());
        StartRead();
    }

    public bool NeedsFrame => _loading is not null || _changing is not null || _restoreFocus || _focusForm;

    private void StartRead()
    {
        if (_loading is not null || _changing is not null || _form.IsVisible) return;
        var token = _closing.Token;
        _loading = Task.Run(() => _catalog.ReadAsync(token), token);
        _status.Value = "Читаю список проектов…";
        _status.IsVisible = true;
        _status.HasError = false;
        RefreshButton.IsEnabled = false;
        _scroll.IsEnabled = false;
        OpenButton.IsEnabled = false;
        NewButton.IsEnabled = false;
        OpenExistingButton.IsEnabled = false;
        ProjectsFolderButton.IsEnabled = false;
        MaintenanceButton.IsEnabled = false;
    }

    public void AdvanceFrame(TimeSpan elapsed, IInputContext context)
    {
        if (_focusForm)
        {
            _focusForm = false;
            context.Focus(_mode switch
            {
                FormMode.Create => NameInput,
                FormMode.OpenFolder => FolderInput,
                FormMode.ProjectsFolder => ProjectsFolderInput,
                _ => CancelButton
            });
        }
        if (_changing is { IsCompleted: true } change)
        {
            _changing = null;
            _form.IsEnabled = _formActions.IsEnabled = true;
            ProjectEntry? entry;
            try { entry = change.GetAwaiter().GetResult(); }
            catch (Exception error)
            {
                var message = error is Backend.BackendCommandException command ? command.Code switch
                {
                    "writer_busy" => "Проект открыт на запись в другой сессии.",
                    "recovery_conflict" => "Журнал или данные проекта изменились. Перечитайте список.",
                    "invalid_project" => "Хранилище отклонило данные проекта или журнал. Проверьте их перед повтором.",
                    _ => error.Message
                } : error.Message;
                ShowStatus(message, error: true);
                return;
            }
            var open = _mode != FormMode.Maintenance;
            HideForm();
            if (_mode == FormMode.ProjectsFolder)
            {
                _openingError = null;
                _projectsRootSaved = true;
                StartRead();
            }
            else if (open) _openProject(entry!);
            else
            {
                _selected = entry;
                _openingError = null;
                StartRead();
            }
            return;
        }
        if (_restoreFocus)
        {
            _restoreFocus = false;
            var button = _rows.Find(row => ReferenceEquals(row.Entry, _selected)).Button;
            if (button is not null) Focus(button, context);
        }
        if (_loading is not { IsCompleted: true } task) return;
        _loading = null;
        ProjectListing listing;
        try
        {
            listing = task.GetAwaiter().GetResult();
            _canChangeProjectsRoot = true;
            _projectsRoot = listing.ProjectsRoot;
        }
        catch (Exception error) when (ProjectCatalog.IsDataError(error))
        {
            _canChangeProjectsRoot = false;
            listing = new([], [$"Не удалось прочитать рабочее пространство — {error.Message}"]);
        }

        foreach (var child in _list.Items.ToArray())
        {
            _list.Items.Remove(child);
            child.Dispose();
        }
        _rows.Clear();
        _notices.Value = string.Join(" · ", listing.Notices);
        _notices.IsVisible = listing.Notices.Length > 0;
        _notices.HasError = _notices.IsVisible;
        AddGroup("Недавние", listing.Items.Where(entry => entry.Recent));
        AddGroup("Доступные", listing.Items.Where(entry => !entry.Recent));
        var previous = _selected;
        _selected = previous?.Directory is null ? null : listing.Items.FirstOrDefault(entry =>
            ProjectCatalog.PathComparer.Equals(entry.Directory, previous.Directory) && entry.Id == previous.Id &&
            entry.Availability != ProjectAvailability.IdentityMismatch);
        _restoreFocus = _selected is not null;
        _scroll.IsEnabled = true;
        RefreshButton.IsEnabled = true;

        ShowStatus(_openingError ?? (_projectsRootSaved ? "Папка проектов сохранена." : _rows.Count == 0 ? "Проекты не найдены." : ""),
            _openingError is not null);
        _projectsRootSaved = false;
        UpdateSelection();
    }

    private void AddGroup(string name, IEnumerable<ProjectEntry> items)
    {
        var rows = items.ToArray();
        if (rows.Length == 0) return;
        var title = Text.Plain(name);
        title.FontSize = 17;
        _list.Items.Add(title);
        foreach (var entry in rows)
        {
            var button = Button.Simple(entry.Name);
            button.AccessibleName = $"{entry.Name}, {entry.Location}, {entry.Details}";
            button.Click += () => Select(entry);
            var scheme = entry.Schema is { } schema ? $"Схема {schema} · ревизия {entry.Revision} · " : "";
            _list.Items.Add(Border.Create().WithRole(RowRole)
                .Content(Stack(5, button, PathLabel(entry.Location),
                    Text.Plain(scheme + entry.Details).WithRole(MetadataRole))));
            _rows.Add((entry, button));
        }
    }

    private void Select(ProjectEntry entry)
    {
        _selected = entry;
        _openingError = null;
        _status.IsVisible = false;
        UpdateSelection();
    }

    private void UpdateSelection()
    {
        foreach (var (entry, button) in _rows) button.Tone = ReferenceEquals(entry, _selected) ? ControlTone.Accent : ControlTone.Neutral;
        _details.Value = _selected switch
        {
            { OpensReadOnly: true } or { CanOpen: false } => _selected.Details,
            null when _rows.Count > 0 => "Выберите проект",
            _ => ""
        };
        OpenButton.IsEnabled = _selected?.CanOpen == true && _loading is null;
        var canMaintain = _selected?.CanMaintain == true;
        var browsing = !_form.IsVisible;
        _browseActions.IsVisible = browsing;
        _formActions.IsVisible = !browsing;
        OpenButton.IsVisible = browsing && (!canMaintain || _selected?.CanOpen == true);
        MaintenanceButton.IsVisible = browsing && canMaintain;
        MaintenanceButton.IsEnabled = canMaintain && _loading is null;
        MaintenanceButton.Value = _selected?.Availability == ProjectAvailability.MigrationRequired ? "Миграция…" : "Восстановление…";
        NewButton.IsEnabled = browsing && _loading is null;
        OpenExistingButton.IsEnabled = (browsing || _mode == FormMode.Create) && _loading is null && _changing is null;
        ProjectsFolderButton.IsEnabled = browsing && _loading is null && _canChangeProjectsRoot;
        RefreshButton.IsEnabled = browsing && _loading is null;
    }

    private void Open()
    {
        if (_selected?.CanOpen == true && _loading is null) _openProject(_selected);
    }

    private void ShowForm(FormMode mode)
    {
        _mode = mode;
        _formTitle.Value = mode switch
        {
            FormMode.Create => "Новый проект",
            FormMode.OpenFolder => OpenExistingButton.Value,
            FormMode.ProjectsFolder => ProjectsFolderButton.Value,
            _ => _selected!.Name
        };
        _formPath.IsVisible = mode == FormMode.Maintenance;
        _formPath.Value = ShortPath(_selected?.Location ?? "");
        _formPath.AccessibleName = _selected?.Location ?? "";
        _newFields.IsVisible = mode == FormMode.Create;
        _folderFields.IsVisible = mode == FormMode.OpenFolder;
        _projectsFolderFields.IsVisible = mode == FormMode.ProjectsFolder;
        if (mode == FormMode.ProjectsFolder) ProjectsFolderInput.Value = _projectsRoot ?? "";
        _confirmation.IsVisible = mode == FormMode.Maintenance;
        var migration = _selected?.Availability == ProjectAvailability.MigrationRequired;
        _confirmation.Value = migration ? "Миграция сохранит исходный JSON в истории проекта." :
            "Восстановление завершит запись по найденному журналу.";
        ConfirmButton.Value = mode switch
        {
            FormMode.Create => "Создать и открыть",
            FormMode.OpenFolder => "Открыть",
            FormMode.ProjectsFolder => "Сохранить",
            _ => migration ? "Мигрировать" : "Восстановить"
        };
        _list.IsVisible = false;
        _form.IsVisible = true;
        _scroll.Offset = 0;
        _focusForm = true;
        ShowStatus("", error: false);
        UpdateSelection();
    }

    private void HideForm()
    {
        if (_changing is not null) return;
        _form.IsVisible = false;
        _list.IsVisible = true;
        _restoreFocus = _selected is not null;
        ShowStatus("", error: false);
        UpdateSelection();
    }

    private void Confirm()
    {
        if (_changing is not null || !_form.IsVisible) return;
        var token = _closing.Token;
        if (_mode == FormMode.Create)
        {
            if (!Parse(WidthInput, out var width) || !Parse(HeightInput, out var height) || !Parse(FpsInput, out var fps))
            {
                ShowStatus("Укажите размер и FPS целыми числами.", error: true);
                return;
            }
            var draft = new NewProject(NameInput.Value, width, height, fps);
            _changing = Task.Run<ProjectEntry?>(async () => await _catalog.CreateAsync(draft, token).ConfigureAwait(false), token);
        }
        else if (_mode == FormMode.OpenFolder)
        {
            var path = FolderInput.Value;
            _changing = Task.Run<ProjectEntry?>(async () => await _catalog.InspectFolderAsync(path, token).ConfigureAwait(false), token);
        }
        else if (_mode == FormMode.ProjectsFolder)
        {
            var path = ProjectsFolderInput.Value;
            var expectedRoot = _projectsRoot;
            _changing = Task.Run<ProjectEntry?>(async () =>
            {
                await _catalog.SaveProjectsRootAsync(path, expectedRoot, token).ConfigureAwait(false);
                return null;
            }, token);
        }
        else if (_selected is { } entry)
            _changing = Task.Run<ProjectEntry?>(async () => await _maintain(entry, token).ConfigureAwait(false), token);
        if (_changing is null) return;
        _form.IsEnabled = false;
        _formActions.IsEnabled = false;
        UpdateSelection();
        ShowStatus(_mode switch
        {
            FormMode.Create => "Создаю проект…",
            FormMode.OpenFolder => "Проверяю расположение проекта…",
            FormMode.ProjectsFolder => "Сохраняю папку проектов…",
            _ => "Выполняю операцию…"
        }, error: false);
    }

    private static bool Parse(TextBox input, out int value) =>
        int.TryParse(input.Value.Trim(), NumberStyles.None, CultureInfo.InvariantCulture, out value);

    private void ShowStatus(string status, bool error)
    {
        _status.Value = status;
        _status.HasError = error;
        _status.IsVisible = status.Length > 0;
    }

    public int InputPriority => 0;

    public bool PreviewInput(UiInput input, IInputContext context)
    {
        if (_form.IsVisible)
        {
            if (input.Kind == InputKind.KeyDown && input.Key == UiKey.Escape) { HideForm(); return true; }
            if (input.Kind == InputKind.KeyDown && input.Key == UiKey.Enter &&
                (_mode == FormMode.OpenFolder && FolderInput.IsFocused || _mode == FormMode.ProjectsFolder && ProjectsFolderInput.IsFocused))
            { Confirm(); return true; }
            return false;
        }
        if (_loading is not null || input.Kind != InputKind.KeyDown || _rows.Count == 0) return false;
        var focused = _rows.FindIndex(row => row.Button.IsFocused);
        if (focused < 0) return false;
        if (input.Key == UiKey.Enter)
        {
            Select(_rows[focused].Entry);
            Open();
            return true;
        }
        var next = input.Key switch
        {
            UiKey.Up => Math.Max(0, focused - 1),
            UiKey.Down => Math.Min(_rows.Count - 1, focused + 1),
            UiKey.Home => 0,
            UiKey.End => _rows.Count - 1,
            _ => -1
        };
        if (next < 0) return false;
        Select(_rows[next].Entry);
        Focus(_rows[next].Button, context);
        return true;
    }

    private void Focus(Button button, IInputContext context)
    {
        context.Focus(button);
        _scroll.BringIntoView(button.Bounds);
    }

    public override void Dispose()
    {
        _closing.Cancel();
        if (_changing is { } change)
            try { change.GetAwaiter().GetResult(); }
            catch (Exception) when (change.IsCompleted) { }
        _closing.Dispose();
        if (_loading is { } pending)
            _ = pending.ContinueWith(task => _ = task.Exception, CancellationToken.None,
                TaskContinuationOptions.OnlyOnFaulted | TaskContinuationOptions.ExecuteSynchronously, TaskScheduler.Default);
        base.Dispose();
    }

    private static VStack Stack(float spacing, params Element[] children) =>
        VStack.Create().Configure(stack => stack.Spacing = spacing).WithChildren(children);

    private static Text PathLabel(string path)
    {
        var label = Text.Plain(ShortPath(path)).WithRole(LocationRole);
        label.AccessibleName = path;
        return label;
    }

    private static string ShortPath(string path)
    {
        const int limit = 82;
        if (path.Length <= limit) return path;
        var tail = path.Length - limit + 6;
        if (char.IsLowSurrogate(path[tail])) tail++;
        var head = char.IsHighSurrogate(path[2]) ? 2 : 3;
        return path[..head] + " … " + path[tail..];
    }

    private sealed class PickerStyle : IViewStyle<ProjectPickerWindow>
    {
        public void Configure(IViewStyleBuilder<ProjectPickerWindow> styles)
        {
            styles.Root().Background(theme => theme.Surface).FontSize(14);
            styles.Role(RowRole).Background(theme => theme.Input).Padding(new Thickness(12, 8, 12, 8));
            styles.Role(LocationRole).Foreground(theme => theme.Disabled).FontSize(13);
            styles.Role(MetadataRole).FontSize(13);
        }
    }

    private sealed class ProjectPickerLayout : ElementList
    {
        private readonly Element _header, _list, _footer;
        private const float Gap = 14;

        internal ProjectPickerLayout(Element header, Element list, Element footer)
        {
            _header = header; _list = list; _footer = footer;
            Add(header); Add(list); Add(footer);
        }

        protected override Size MeasureCore(Size available, ITextLayoutService text)
        {
            _header.Measure(available, text);
            _footer.Measure(available, text);
            _list.Measure(new(available.Width, ListHeight(available.Height)), text);
            return new(available.Width, _header.DesiredSize.Height + _list.DesiredSize.Height + _footer.DesiredSize.Height + 2 * Gap);
        }

        protected override void ArrangeCore(Rect area)
        {
            _header.Arrange(new(area.X, area.Y, area.Width, _header.DesiredSize.Height));
            _list.Arrange(new(area.X, area.Y + _header.DesiredSize.Height + Gap, area.Width, ListHeight(area.Height)));
            _footer.Arrange(new(area.X, area.Y + area.Height - _footer.DesiredSize.Height, area.Width, _footer.DesiredSize.Height));
        }

        private float ListHeight(float total) => Math.Max(0, total - _header.DesiredSize.Height - _footer.DesiredSize.Height - 2 * Gap);
    }
}
