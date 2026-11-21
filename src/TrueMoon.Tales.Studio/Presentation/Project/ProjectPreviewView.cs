using TrueMoon.Alloy.Hosting;
using TrueMoon.Alloy.Runtime;
using TrueMoon.Argentis;
using TrueMoon.Tales.Studio.Backend;
using TrueMoon.Tales.Studio.Configuration;
using TrueMoon.Tales.Studio.Presentation.Projects;

namespace TrueMoon.Tales.Studio.Presentation.Project;

internal sealed record ProjectFormatUpgrade(string Location,
    Func<OpenedBackendProject, CancellationToken, Task<ProjectEntry>>? Apply, Action<ProjectEntry> OpenEditor);

internal sealed class ProjectPreviewView : View<ProjectViewModel>, IUiFrameParticipant, IUiInputObserver
{
    private readonly ProjectViewModel _model;
    private readonly ProjectFormatUpgrade? _upgrade;
    private readonly CancellationTokenSource _closing = new();
    private readonly Text _name = new("Открытие проекта…"), _revision = new(), _summary = new(), _status = new();
    private readonly Text _upgradePath, _upgradeDetails = new();
    private readonly Text _upgradeStatus = new() { IsVisible = false };
    private readonly Button _reload;
    private readonly VStack _confirmation;
    private readonly VStack _segments = VStack.Create();
    private OpenedBackendProject? _confirmedSnapshot;
    private Task<ProjectEntry>? _upgrading;
    private bool _focusCancel, _upgraded;

    internal Text ProjectTitle => _name;
    internal Text RevisionText => _revision;
    internal Text StatusText => _status;
    internal Button ReloadButton => _reload;
    internal Button UpgradeButton { get; } = Button.Simple("Обновить формат и открыть редактор");
    internal Button ConfirmUpgradeButton { get; } = Button.Simple("Обновить формат");
    internal Button CancelUpgradeButton { get; } = Button.Simple("Отмена");
    internal string UpgradePath => _upgradePath.Value;
    internal string UpgradeDetails => _upgradeDetails.Value;
    internal string UpgradeStatus => _upgradeStatus.Value;
    internal bool CanLeave => _upgrading is null;

    internal ProjectPreviewView(ProjectViewModel model, IViewStyle<ProjectPreviewView> style, ProjectFormatUpgrade? upgrade = null,
        Action? returnToProjects = null)
    {
        _model = model;
        _upgrade = upgrade;
        _reload = new Button("Перечитать проект");
        _reload.Click += () => { ShowUpgradeStatus(""); model.Reload(); Refresh(); };
        UpgradeButton.Click += AskUpgrade;
        UpgradeButton.Tone = ControlTone.Accent;
        ConfirmUpgradeButton.Click += ConfirmUpgrade;
        CancelUpgradeButton.Click += CancelUpgrade;
        ConfirmUpgradeButton.Tone = ControlTone.Accent;
        _upgradePath = new Text(upgrade?.Location ?? "") { FontSize = 13, AccessibleName = upgrade?.Location ?? "" };
        _confirmation = VStack.Create().Configure(stack => stack.Spacing = 8).WithChildren(_upgradePath, _upgradeDetails,
            HStack.Create().Configure(stack => stack.Spacing = 8).WithChildren(ConfirmUpgradeButton, CancelUpgradeButton));
        var fields = VStack.Create().Configure(stack => stack.Spacing = 16).WithChildren(_name, _revision, _summary);
        if (upgrade is not null) fields.WithChildren(UpgradeButton, _confirmation, _upgradeStatus);
        fields.WithChildren(_reload);
        if (returnToProjects is not null) fields.WithChildren(Button.Simple("К проектам").OnClick(returnToProjects));
        fields.WithChildren(Text.Plain("Сценарные сегменты"), ScrollViewer.Create().Content(_segments), _status);
        Content(model, _ => Border.Create().Configure(border => border.Padding = new Thickness(24)).Content(fields));
        this.WithViewStyle(style);
        model.Reload();
        Refresh();
    }

    public bool NeedsFrame => _model.Loading || _upgrading is not null || _focusCancel;

    public void AdvanceFrame(TimeSpan elapsed, IInputContext context)
    {
        if (_model.Poll()) Refresh();
        if (_focusCancel)
        {
            _focusCancel = false;
            context.Focus(CancelUpgradeButton);
        }
        if (_upgrading is not { IsCompleted: true } task) return;
        _upgrading = null;
        ProjectEntry updated;
        try { updated = task.GetAwaiter().GetResult(); }
        catch (Exception error)
        {
            var message = error is BackendCommandException command ? command.Code switch
            {
                "writer_busy" => "Проект открыт на запись в другой сессии.",
                "revision_conflict" or "conflict" => "Проект изменился. Перечитайте его перед повтором.",
                _ => error.Message
            } : error.Message;
            ShowUpgradeStatus($"Не удалось обновить формат — {message}", error: true);
            UpdateUpgradeControls();
            return;
        }
        _upgraded = true;
        ShowUpgradeStatus("Формат обновлён. Открываю редактор…");
        UpdateUpgradeControls();
        _upgrade!.OpenEditor(updated);
    }

    public int InputPriority => 0;
    public bool PreviewInput(UiInput input, IInputContext context)
    {
        if (_confirmedSnapshot is null || _upgrading is not null || input.Kind != InputKind.KeyDown || input.Key != UiKey.Escape)
            return false;
        CancelUpgrade();
        context.Focus(UpgradeButton);
        return true;
    }

    private void AskUpgrade()
    {
        if (_upgrade?.Apply is null || _model.Loading || _model.Failed || _model.Opened is not { SchemaVersion: 1 } snapshot) return;
        _confirmedSnapshot = snapshot;
        _upgradeDetails.Value = $"Ревизия {snapshot.Project.Revision} · обновление до схемы 2. Исходный JSON сохранится в истории.";
        _focusCancel = true;
        ShowUpgradeStatus("");
        UpdateUpgradeControls();
    }

    private void ConfirmUpgrade()
    {
        if (_upgrade?.Apply is not { } apply || _confirmedSnapshot is not { } snapshot || _upgrading is not null) return;
        _confirmedSnapshot = null;
        var token = _closing.Token;
        _upgrading = Task.Run(() => apply(snapshot, token), token);
        ShowUpgradeStatus("Обновляю формат проекта…");
        UpdateUpgradeControls();
    }

    private void CancelUpgrade()
    {
        _confirmedSnapshot = null;
        _focusCancel = false;
        UpdateUpgradeControls();
    }

    internal async Task<bool> WaitForUpgradeOnShutdownAsync(WindowCloseReason reason)
    {
        if (_upgrading is not { } pending) return true;
        if (reason != WindowCloseReason.Shutdown) return false;
        try { await pending.ConfigureAwait(false); }
        catch (Exception) { /* The screen reports the failure while open. */ }
        return true;
    }

    private void UpdateUpgradeControls()
    {
        _reload.IsEnabled = !_model.Loading && _confirmedSnapshot is null && _upgrading is null && !_upgraded;
        UpgradeButton.IsVisible = _upgrade is not null && _model.Opened?.SchemaVersion == 1 &&
            _confirmedSnapshot is null && _upgrading is null && !_upgraded;
        UpgradeButton.IsEnabled = UpgradeButton.IsVisible && _upgrade?.Apply is not null && !_model.Loading && !_model.Failed;
        _confirmation.IsVisible = _confirmedSnapshot is not null;
    }

    private void ShowUpgradeStatus(string text, bool error = false)
    {
        _upgradeStatus.Value = text;
        _upgradeStatus.IsVisible = text.Length > 0;
        _upgradeStatus.HasError = error;
    }

    private void Refresh()
    {
        _status.Value = _model.Status;
        UpdateUpgradeControls();
        if (_upgrade is { Apply: null } && _model.Opened?.SchemaVersion == 1)
            ShowUpgradeStatus(ProjectEntry.FormatWriteDenied);
        if (_model.Opened is not { } opened) return;
        var project = opened.Project;
        _name.Value = project.Name;
        _revision.Value = $"Ревизия {project.Revision} · схема {opened.SchemaVersion}" +
            (_model.Failed || _model.Loading ? " · предыдущий снимок" : "");
        _summary.Value = $"Источников: {project.Sources.Length} · дублей: {project.Sources.Sum(source => source.Takes.Length)} · " +
            $"окон: {project.Montage.Windows.Length} · клипов: {project.Montage.Clips.Length}";
        foreach (var item in _segments.Items.ToArray()) { _segments.Items.Remove(item); item.Dispose(); }
        foreach (var segment in project.Segments)
            _segments.Items.Add(Text.Plain($"{segment.Name} · действий: {segment.Actions.Length}"));
    }

    public override void Dispose()
    {
        if (IsDisposed) return;
        _closing.Cancel();
        if (_upgrading is { } task)
            _ = task.ContinueWith(failed => _ = failed.Exception, CancellationToken.None,
                TaskContinuationOptions.OnlyOnFaulted | TaskContinuationOptions.ExecuteSynchronously, TaskScheduler.Default);
        _closing.Dispose();
        if (_upgrade is null)
        {
            UpgradeButton.Dispose();
            _confirmation.Dispose();
            _upgradeStatus.Dispose();
        }
        base.Dispose();
    }
}

internal sealed class ProjectWindowFactory(ProjectViewModel model, IViewStyle<ProjectPreviewView> style, ProjectFormatUpgrade? upgrade,
    Action? returnToProjects) : IFactory<ProjectPreviewView>
{
    public ProjectPreviewView Create() => new(model, style, upgrade, returnToProjects);
    public ProjectPreviewView Create<TData>(TData? data = default) => data is null ? Create() : throw new ArgumentException("Window arguments are unsupported.");
}

internal static class ProjectPresentation
{
    internal static IAppConfigurationContext UseProjectWindow(this IAppConfigurationContext context,
        Action<string?>? initialOpen = null, bool nativeWindow = true, StudioUiPreferences? preferences = null,
        ProjectFormatUpgrade? upgrade = null, Action? returnToProjects = null)
    {
        context.Services(r => r.Singleton<ProjectViewModel>(resolver => new(resolver.Resolve<IBackendProjectReader>()!,
                resolver.Resolve<BackendOptions>()!.InitialFingerprint, initialOpen))
            .Singleton<IViewStyle<ProjectPreviewView>, ProjectWindowStyle>()
            .Singleton<IFactory<ProjectPreviewView>>(resolver => new ProjectWindowFactory(resolver.Resolve<ProjectViewModel>()!,
                resolver.Resolve<IViewStyle<ProjectPreviewView>>()!, upgrade, returnToProjects)));
        if (!nativeWindow) return context;
        return context.UsePresentation(resolver =>
        {
            var preview = resolver.Resolve<IFactory<ProjectPreviewView>>()!.Create()!;
            var window = new Window { Title = "TrueMoon · Tales Studio", ClientSize = new(1000, 720) };
            window.SetContent(preview);
            window.Closing += preview.WaitForUpgradeOnShutdownAsync;
            preferences?.Attach(window, StudioUiWindow.Viewer);
            return window;
        }, options => options.UseSkiaOpenGL().UseSilkWindow(_ => { }));
    }
}

internal sealed class ProjectWindowStyle : IViewStyle<ProjectPreviewView>
{
    public void Configure(IViewStyleBuilder<ProjectPreviewView> styles)
    {
        styles.Root().Background(theme => theme.Surface).FontSize(14);
        styles.Part(view => view.ProjectTitle).FontSize(20);
        styles.Part(view => view.RevisionText).Foreground(theme => theme.Disabled);
        styles.Part(view => view.ReloadButton).Tone(ControlTone.Accent).Height(36);
    }
}
