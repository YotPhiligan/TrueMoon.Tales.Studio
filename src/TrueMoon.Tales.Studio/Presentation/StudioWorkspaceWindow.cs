using TrueMoon.Argentis;
using TrueMoon.Dependencies;
using TrueMoon.Tales.Studio.Backend;
using TrueMoon.Tales.Studio.Configuration;
using TrueMoon.Tales.Studio.Presentation.Project;
using TrueMoon.Tales.Studio.Presentation.Projects;
using TrueMoon.Tales.Studio.Presentation.Scene;

namespace TrueMoon.Tales.Studio.Presentation;

internal sealed class StudioWorkspaceWindow : Window, IUiFrameParticipant
{
    private const string WorkspaceTitle = "TrueMoon · Tales Studio";
    private readonly ProjectCatalog _catalog;
    private readonly string? _executablePath;
    private readonly CancellationTokenSource _closing = new();
    private readonly CancellationToken _token;
    private Task<(ProjectEntry? Project, string? Notice)>? _emptyWorkspace;
    private Task<string?>? _route;
    private ProjectEntry? _previous;
    private WindowTitleBar? _titleBar;
    private bool _returning;
    private volatile bool _disposing;

    internal StudioUiPreferences? Preferences { get; }
    internal IApp? ProjectApp { get; private set; }

    internal StudioWorkspaceWindow(ProjectCatalog catalog, bool openEmptyWorkspace, StudioUiPreferences? preferences,
        string? executablePath = null)
    {
        _catalog = catalog;
        _executablePath = executablePath;
        _token = _closing.Token;
        Preferences = preferences;
        Title = WorkspaceTitle;
        ClientSize = new(1280, 820);
        Closing += ConfirmCloseAsync;
        preferences?.Attach(this, StudioUiWindow.Workspace);
        if (openEmptyWorkspace)
        {
            ShowMessage("Читаю список проектов…");
            _emptyWorkspace = Task.Run(() => ProjectPickerApplication.PrepareEmptyWorkspaceAsync(catalog, _token), _token);
        }
        else ShowPicker(preferences?.Notice);
    }

    internal void AttachTitleBar(WindowTitleBar title)
    {
        _titleBar = title;
        UpdateTitleBar();
        if (Preferences is not null) StudioUiPreferences.ApplyTheme(this, Preferences.Current.Theme);
    }

    public bool NeedsFrame => _emptyWorkspace is not null || _route is not null && (ProjectApp is null || _route.IsCompleted);

    public void AdvanceFrame(TimeSpan elapsed, IInputContext context)
    {
        if (_emptyWorkspace is { IsCompleted: true } initial)
        {
            _emptyWorkspace = null;
            var prepared = initial.GetAwaiter().GetResult();
            if (prepared.Project is { } project) BeginOpen(project);
            else ShowPicker(prepared.Notice ?? Preferences?.Notice);
        }
        if (_route is not { IsCompleted: true } route) return;
        _route = null;
        var notice = route.GetAwaiter().GetResult();
        ShowPicker(notice);
    }

    private void BeginOpen(ProjectEntry entry)
    {
        if (_route is not null) return;
        _previous = entry;
        ShowMessage("Открываю проект…");
        _route = Task.Run(() => ProjectPickerApplication.OpenSelectedAsync(_catalog, entry, _token,
            executablePath: _executablePath, workspace: this), _token);
    }

    internal Task AttachProjectAsync(IApp project) => OnOwnerAsync(() =>
    {
        if (ProjectApp is not null) throw new InvalidOperationException("The previous project view is still attached.");
        var services = project.Services;
        Element view = services.Resolve<BackendOptions>()!.WriteEnabled
            ? services.Resolve<IFactory<SceneView>>()!.Create()!
            : services.Resolve<IFactory<ProjectPreviewView>>()!.Create()!;
        ProjectApp = project;
        _returning = false;
        Show(view, WorkspaceTitle);
    });

    internal async Task DetachProjectAsync(IApp project)
    {
        try
        {
            await OnOwnerAsync(() =>
            {
                if (!ReferenceEquals(ProjectApp, project)) return;
                ProjectApp = null;
                ShowMessage("Закрываю сессию проекта…");
            }).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (_token.IsCancellationRequested) { }
    }

    private Task OnOwnerAsync(Action work)
    {
        if (_token.IsCancellationRequested) return Task.FromCanceled(_token);
        var done = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        Dispatch(() =>
        {
            if (_disposing) return;
            try { work(); done.TrySetResult(); }
            catch (Exception error) { done.TrySetException(error); }
        });
        return done.Task.WaitAsync(_token);
    }

    internal void RequestProjects()
    {
        if (ProjectApp is not { } project || _returning) return;
        if (Child is SceneView editor && !editor.ConfirmClose(WindowCloseReason.Application, RequestProjects)) return;
        if (Child is ProjectPreviewView { CanLeave: false }) return;
        _returning = true;
        project.Services.Resolve<IAppLifetime>()!.Cancel();
    }

    private Task<bool> ConfirmCloseAsync(WindowCloseReason reason) => Child switch
    {
        SceneView editor => Task.FromResult(editor.ConfirmClose(reason, RequestWindowClose)),
        ProjectPreviewView preview => preview.WaitForUpgradeOnShutdownAsync(reason),
        ProjectPickerView picker => Task.FromResult(picker.CanLeave),
        _ => Task.FromResult(true)
    };

    private void RequestWindowClose() => _ = RequestCloseAsync();

    private void ShowPicker(string? notice) =>
        Show(new ProjectPickerView(_catalog, BeginOpen, _previous, notice), "Проекты · TrueMoon Tales Studio");

    private void ShowMessage(string message) =>
        Show(Border.Create().Configure(border => border.Padding = new Thickness(24)).Content(Text.Plain(message)), WorkspaceTitle);

    private void Show(Element screen, string title)
    {
        ClearHeader();
        var previous = Child;
        SetContent(screen);
        previous?.Dispose();
        Title = title;
        UpdateTitleBar();
    }

    private void UpdateTitleBar()
    {
        if (_titleBar is not { } title) return;
        if (title.Parent is WindowFrame frame) frame.TitleBarOverContent = Child is SceneView;
        if (Child is SceneView editor) title.SetHeader(editor.CreateTitleBar(RequestProjects));
    }

    private void ClearHeader()
    {
        if (_titleBar is not { IsDisposed: false, HeaderContent: { } previous }) return;
        _titleBar.SetHeader(null);
        previous.Dispose();
    }

    public override void Dispose()
    {
        if (IsDisposed) return;
        _disposing = true;
        // The enclosing Alloy frame owns its header and disposes it with the window.
        try { base.Dispose(); }
        finally { _closing.Cancel(); }
    }

    // Alloy has disposed the screen on its owner thread before the application drains its project App.
    internal async Task DrainAsync()
    {
        try
        {
            if (_emptyWorkspace is not null) await _emptyWorkspace.ConfigureAwait(false);
            if (_route is not null) await _route.ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (_token.IsCancellationRequested) { }
        finally { _closing.Dispose(); }
    }
}
