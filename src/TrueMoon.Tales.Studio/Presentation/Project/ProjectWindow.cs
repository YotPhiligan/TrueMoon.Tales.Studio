using TrueMoon.Alloy.Hosting;
using TrueMoon.Alloy.Runtime;
using TrueMoon.Argentis;
using TrueMoon.Tales.Studio.Backend;

namespace TrueMoon.Tales.Studio.Presentation.Project;

internal sealed class ProjectWindow : Window<ProjectViewModel>, IUiFrameParticipant
{
    private readonly ProjectViewModel _model;
    private readonly Text _name = new("Открытие проекта…"), _revision = new(), _summary = new(), _status = new();
    private readonly Button _reload;
    private readonly VStack _segments = VStack.Create();

    internal Text ProjectTitle => _name;
    internal Text RevisionText => _revision;
    internal Text StatusText => _status;
    internal Button ReloadButton => _reload;

    internal ProjectWindow(ProjectViewModel model, IViewStyle<ProjectWindow> style)
    {
        _model = model;
        Title = "TrueMoon · Tales Studio";
        ClientSize = new(1000, 720);
        _reload = new Button("Перечитать проект");
        _reload.Click += () => { model.Reload(); Refresh(); };
        Content(model, _ => Border.Create().Configure(border => border.Padding = new Thickness(24)).Content(
            VStack.Create().Configure(stack => stack.Spacing = 16).WithChildren(
                _name, _revision, _summary, _reload,
                Text.Plain("Сценарные сегменты"), ScrollViewer.Create().Content(_segments), _status)));
        this.WithViewStyle(style);
        model.Reload();
        Refresh();
    }

    public bool NeedsFrame => _model.Loading;

    public void AdvanceFrame(TimeSpan elapsed, IInputContext context)
    {
        if (_model.Poll()) Refresh();
    }

    private void Refresh()
    {
        _status.Value = _model.Status;
        _reload.IsEnabled = !_model.Loading;
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
}

internal sealed class ProjectWindowFactory(ProjectViewModel model, IViewStyle<ProjectWindow> style) : IFactory<ProjectWindow>
{
    public ProjectWindow Create() => new(model, style);
    public ProjectWindow Create<TData>(TData? data = default) => data is null ? Create() : throw new ArgumentException("Window arguments are unsupported.");
}

internal static class ProjectPresentation
{
    internal static IAppConfigurationContext UseProjectWindow(this IAppConfigurationContext context,
        Action<string?>? initialOpen = null, bool nativeWindow = true)
    {
        context.Services(r => r.Singleton<ProjectViewModel>(resolver => new(resolver.Resolve<IBackendProjectReader>()!,
                resolver.Resolve<BackendOptions>()!.InitialFingerprint, initialOpen))
            .Singleton<IViewStyle<ProjectWindow>, ProjectWindowStyle>()
            .Singleton<IFactory<ProjectWindow>, ProjectWindowFactory>());
        if (!nativeWindow) return context;
        return context.UsePresentationFactory<ProjectWindow>(options => options.UseSkiaOpenGL().UseSilkWindow(window =>
        {
            window.Title = "TrueMoon · Tales Studio";
            window.Width = 1000;
            window.Height = 720;
        }));
    }
}

internal sealed class ProjectWindowStyle : IViewStyle<ProjectWindow>
{
    public void Configure(IViewStyleBuilder<ProjectWindow> styles)
    {
        styles.Root().Background(theme => theme.Surface).FontSize(14);
        styles.Part(view => view.ProjectTitle).FontSize(20);
        styles.Part(view => view.RevisionText).Foreground(theme => theme.Disabled);
        styles.Part(view => view.ReloadButton).Tone(ControlTone.Accent).Height(36);
    }
}
