using TrueMoon;
using TrueMoon.Alloy.Rendering.Skia;
using TrueMoon.Argentis;
using TrueMoon.Tales.Studio.Media;
using TrueMoon.Tales.Studio.Models;
using TrueMoon.Tales.Studio.Presentation;
using TrueMoon.Tales.Studio.Presentation.Shell;

namespace TrueMoon.Tales.Studio.Presentation.Scene;

internal sealed class SceneView : Window<SceneViewModel>, IUiFrameParticipant, IUiInputObserver
{
    internal static readonly ViewStyleRole<Border> PaneRole = new("studio.pane");
    internal static readonly ViewStyleRole<Text> CaptionRole = new("studio.caption");
    internal static readonly ViewStyleRole<Text> MutedRole = new("studio.muted");
    internal static readonly ViewStyleRole<Button> CommandRole = new("studio.command");
    internal static readonly ViewStyleRole<VStack> StackRole = new("studio.stack");
    private readonly SceneViewModel _model;
    private readonly SceneExport? _export;
    private readonly IFactory<StudioTitleBar, StudioTitleBarArguments> _headers;
    private readonly StudioCommands _commands;
    private readonly StudioPanelState _panels = new() { WideTimeline = true };
    private readonly Text _project = new("Открытие проекта…"), _mode = new(), _take = new(), _frame = new();
    private readonly Text _timelineCaption = new();
    private readonly Button _play;
    private readonly Button _previewMontage, _previewSource, _timelineWidth, _exportButton;
    private SceneExportPanel? _exportPanel;
    private bool _openExport;
    private readonly Text _status = new(), _revision = new(), _edit = new(), _sourceStart = new(), _sourceEnd = new(), _duration = new();
    private readonly Text _savedRange = new(), _editProblem = new();
    private readonly VStack _takes = VStack.Create(), _clips = VStack.Create();
    private readonly Dictionary<string, Button> _takeButtons = [], _clipButtons = [];
    private readonly Dictionary<Button, StudioCommand> _buttons = [];
    private readonly Image _preview = new();
    private readonly PreviewSurface _previewSurface;
    private readonly SceneStrip _strip;
    private readonly StudioLayout _layout;
    private byte[]? _pixels;
    private SkiaImageSource? _image;
    private SceneSnapshot? _shownScene;
    private StudioTitleBar? _header;
    private Action? _requestClose;
    private bool _closePrompt, _closeAfterSave, _contextMenu;

    internal OverlayHost Overlays { get; }
    internal Element Heading { get; }
    internal Element Navigation { get; }
    internal Element Preview { get; }
    internal Border Inspector { get; }
    internal Element Timeline { get; }
    internal Element Footer { get; }
    internal Element CloseNotice { get; }
    internal Button SaveButton { get; }
    internal Text StatusText => _status;
    internal Text ProjectTitle => _project;

    public SceneView(SceneViewModel model, IViewStyle<SceneView> style, IFactory<StudioTitleBar, StudioTitleBarArguments> headers,
        SceneExport? export = null)
    {
        _model = model; _headers = headers; _commands = new(Refresh); _export = export;
        Title = "TrueMoon · Tales Studio"; ClientSize = new(1280, 820);
        RegisterCommands();
        _exportButton = Command("Экспорт MP4", StudioCommand.Export);
        _exportButton.Tone = ControlTone.Accent;
        _exportButton.IsVisible = _export is not null;
        Heading = new StudioHeading(_project, HStack.Create().Configure(x => x.Spacing = 8).WithChildren(_mode, _exportButton));
        Navigation = Pane("Клипы проекта", _clips,
            Text.Plain("Дубли клипа").WithRole(CaptionRole), _takes);
        _previewSurface = new(_preview);
        _play = Command("Старт", StudioCommand.PlayPause);
        _previewMontage = Command("Монтаж", StudioCommand.PreviewMontage);
        _previewSource = Command("Исходник", StudioCommand.PreviewSource);
        Preview = new PreviewLayout(Stack(new StudioHeading(HStack.Create().Configure(x => x.Spacing = 6)
                .WithChildren(_previewMontage, _previewSource), _take)),
            _previewSurface, Stack(new StudioHeading(_frame, _play), HStack.Create().Configure(x => x.Spacing = 6).WithChildren(
                Command("К началу", StudioCommand.ReturnToClip), Command("Пред. кадр", StudioCommand.Previous),
                Command("След. кадр", StudioCommand.Next), Command("К концу", StudioCommand.End))));
        SaveButton = Command("Сохранить обрезку", StudioCommand.Save);
        Inspector = Border.Create().WithRole(PaneRole).Content(new InspectorLayout(
            Stack(Text.Plain("Монтажный клип").WithRole(CaptionRole), _edit),
            ScrollViewer.Create().Content(Stack(
                _savedRange.WithRole(MutedRole), _editProblem,
                Text.Plain("Вход · кадры источника").WithRole(MutedRole), ValueBox(_sourceStart), Command("Начало по кадру", StudioCommand.SetStart),
                Text.Plain("Выход · не включается").WithRole(MutedRole), ValueBox(_sourceEnd), Command("Конец после кадра", StudioCommand.SetEnd),
                Text.Plain("Длина на шкале").WithRole(MutedRole), _duration)),
            Stack(SaveButton, Command("Отменить черновик", StudioCommand.Cancel))));
        _strip = new(model, Refresh) { AccessibleName = "Линейка — перемотка; тело клипа — перенос; края — обрезка; Escape — отмена" };
        _timelineWidth = Command("Компактно", StudioCommand.WideTimeline);
        Timeline = new TimelineLayout(new StudioHeading(_timelineCaption.WithRole(CaptionRole), _timelineWidth), _strip);
        Footer = new StudioHeading(_status, _revision);
        CloseNotice = Border.Create().Content(Stack(Text.Plain("Есть черновик обрезки. Сохранить перед закрытием?"),
            HStack.Create().Configure(x => x.Spacing = 8).WithChildren(Command("Сохранить и закрыть", StudioCommand.SaveClose),
                Command("Отбросить и закрыть", StudioCommand.DiscardClose), Command("Остаться", StudioCommand.Stay))));
        _layout = new(_panels, CloseNotice, Heading, Navigation, Preview, Inspector, Timeline, Footer);
        Overlays = new(_layout) { PopupInsets = new(0, 36, 0, 0) };
        Overlays.Closed += ContextClosed;
        Content(model, _ => Overlays);
        this.WithViewStyle(style);
        Closing += ConfirmClose;
        model.Open(); Refresh();
    }

    internal StudioTitleBar CreateTitleBar(Action requestClose)
    {
        _requestClose = requestClose;
        return _header = _headers.Create(new(_model, Overlays, _commands));
    }

    private void RegisterCommands()
    {
        _commands.Add(StudioCommand.Reload, _model.Open, () => _model.CanReload);
        _commands.Add(StudioCommand.Save, _model.Save, () => _model.CanSave);
        _commands.Add(StudioCommand.Export, () => _openExport = true, () => _export is not null && _model.Scene is not null);
        _commands.Add(StudioCommand.Cancel, _model.Cancel, () => _model.Idle && _model.Modified);
        _commands.Add(StudioCommand.Previous, () => _model.Step(-1), () => _model.CanPrevious);
        _commands.Add(StudioCommand.Next, () => _model.Step(1), () => _model.CanNext);
        _commands.Add(StudioCommand.PlayPause, _model.TogglePlayback, () => _model.CanPlay);
        _commands.Add(StudioCommand.ReturnToClip, _model.ReturnToClip, () => _model.CanSeek);
        _commands.Add(StudioCommand.End, _model.GoToEnd, () => _model.CanSeek);
        _commands.Add(StudioCommand.SetStart, () => _model.SetBoundary(true), () => _model.CanSetStart);
        _commands.Add(StudioCommand.SetEnd, () => _model.SetBoundary(false), () => _model.CanSetEnd);
        _commands.Add(StudioCommand.Navigation, () => _panels.Navigation = !_panels.Navigation);
        _commands.Add(StudioCommand.Inspector, () => _panels.Inspector = !_panels.Inspector);
        _commands.Add(StudioCommand.Timeline, () => _panels.Timeline = !_panels.Timeline);
        _commands.Add(StudioCommand.WideTimeline, () => _panels.WideTimeline = !_panels.WideTimeline);
        _commands.Add(StudioCommand.PreviewMontage, _model.ResumeMontagePreview, () => _model.CanChangePreview && _model.ViewingSource);
        _commands.Add(StudioCommand.PreviewSource, _model.PreviewSource, () => _model.CanChangePreview && !_model.ViewingSource);
        _commands.Add(StudioCommand.FocusPreview, _panels.ToggleFocus);
        _commands.Add(StudioCommand.ResetLayout, () => { _panels.Reset(); _panels.WideTimeline = true; _layout.ResetSizes(); });
        _commands.Add(StudioCommand.Theme, ToggleTheme);
        _commands.Add(StudioCommand.Close, () => _requestClose?.Invoke(), () => !_model.Saving);
        _commands.Add(StudioCommand.Stay, () => _closePrompt = false);
        _commands.Add(StudioCommand.DiscardClose, () => { _model.Cancel(); _closePrompt = false; _requestClose?.Invoke(); }, () => _model.Idle);
        _commands.Add(StudioCommand.SaveClose, () => { _closeAfterSave = true; _model.Save(); }, () => _model.CanSave);
    }

    private Button Command(string label, StudioCommand id)
    {
        var button = Button.Simple(label).WithRole(CommandRole).OnClick(() => _commands[id].Execute());
        _buttons.Add(button, id); return button;
    }
    private static VStack Stack(params Element[] children) => VStack.Create().WithRole(StackRole).WithChildren(children);
    private static Border Pane(string label, params Element[] children) => Border.Create().WithRole(PaneRole)
        .Content(ScrollViewer.Create().Content(Stack([Text.Plain(label).WithRole(CaptionRole), .. children])));
    private static Border ValueBox(Text value) => Border.Create().Configure(x =>
    { x.ColorRole = EditorColorRole.Number; x.Padding = new(8, 5, 8, 5); }).Content(value);

    public bool NeedsFrame => _model.NeedsFrame || _openExport || _export?.NeedsFrame == true;
    public void AdvanceFrame(TimeSpan elapsed, IInputContext context)
    {
        var sceneChanged = _model.Poll(elapsed);
        var exportChanged = _export?.Poll(elapsed) == true;
        if (_openExport)
        {
            _openExport = false;
            if (_export is not null && _exportPanel is null)
            {
                _model.PausePlayback();
                if (_export.CanInspect) _export.Inspect();
                var panel = new SceneExportPanel(_export, _model, () => Overlays.Close());
                Overlays.Open(_exportButton, panel, _exportButton.Bounds, context, _exportButton);
                _exportPanel = panel;
                panel.FocusFirst(context);
            }
        }
        if (!sceneChanged && !exportChanged) return;
        if (sceneChanged && _closeAfterSave)
        {
            _closeAfterSave = false;
            if (!_model.Modified && !_model.HasError && !_model.Stale) { _closePrompt = false; _requestClose?.Invoke(); return; }
        }
        Refresh();
    }

    private Task<bool> ConfirmClose(WindowCloseReason reason)
    {
        _model.PausePlayback();
        _strip.CancelGesture();
        if (reason == WindowCloseReason.Shutdown) return Task.FromResult(true);
        if (_model.Saving) { _model.Explain("Дождитесь завершения сохранения"); Refresh(); return Task.FromResult(false); }
        if (!_model.Modified) return Task.FromResult(true);
        _closePrompt = true; Overlays.Close(); Refresh(); return Task.FromResult(false);
    }

    private void ToggleTheme()
    {
        Element root = this; while (root.Parent is { } parent) root = parent;
        root.ApplyTheme((Theme.Surface == Theme.Dark.Surface ? Theme.Light : Theme.Dark) with
        { FontSize = Theme.FontSize, FontFamily = Theme.FontFamily });
    }

    public int InputPriority => -10;
    public bool PreviewInput(UiInput input, IInputContext context)
    {
        if (input.Kind == InputKind.FocusLost) { _model.PausePlayback(); Refresh(); }
        if (_strip.IsDragging)
        {
            if (input.Kind == InputKind.FocusLost) { _strip.CancelGesture(); return false; }
            if (input.Kind == InputKind.KeyDown && input.Key == UiKey.Escape || input.Kind == InputKind.PointerDown && input.Button == 1)
            { _strip.CancelGesture(); return true; }
            if (input.Kind is InputKind.KeyDown or InputKind.KeyUp or InputKind.Text) return true;
        }
        if (_contextMenu && Overlays.Popup is { } popup && input.Kind == InputKind.PointerDown && !popup.Bounds.Contains(input.X, input.Y))
        { Overlays.Close(); return true; }
        if (_exportPanel is { } exportPanel && ReferenceEquals(Overlays.Popup, exportPanel))
            return exportPanel.MoveFocus(input, context);
        if (Overlays.Popup is not null) return false;
        if (input.Kind == InputKind.PointerDown && input.Button == 1)
        {
            _model.PausePlayback(); Refresh();
            var take = Navigation.IsVisible ? _takeButtons.FirstOrDefault(pair => pair.Value.Bounds.Contains(input.X, input.Y)) : default;
            if (take.Key is not null) { OpenTakeMenu(take.Key, take.Value, new(input.X, input.Y, 0, 0), context); return true; }
            var clip = Navigation.IsVisible ? _clipButtons.FirstOrDefault(pair => pair.Value.Bounds.Contains(input.X, input.Y)) : default;
            if (clip.Key is not null) { OpenClipMenu(clip.Key, clip.Value, new(input.X, input.Y, 0, 0), context); return true; }
            if (Preview.Bounds.Contains(input.X, input.Y) || Inspector.IsVisible && Inspector.Bounds.Contains(input.X, input.Y) || Timeline.IsVisible && Timeline.Bounds.Contains(input.X, input.Y))
            { OpenContext(this, new(input.X, input.Y, 0, 0), context, ClipActions()); return true; }
        }
        if (input.Kind != InputKind.KeyDown) return false;
        if (input.Key == UiKey.F10 && input.Shift)
            return OpenFocusedContext(context);
        if (_header?.Menu.IsExpanded == true) return false;
        if (input.Key == UiKey.Space && !input.Control && (FocusWithin(Preview) || FocusWithin(Timeline)))
        { _commands[StudioCommand.PlayPause].Execute(); return true; }
        StudioCommand? command = input switch
        {
            { Control: true, Key: UiKey.S } => StudioCommand.Save,
            { Control: false, Key: UiKey.Left } => StudioCommand.Previous,
            { Control: false, Key: UiKey.Right } => StudioCommand.Next,
            { Control: false, Key: UiKey.Home } => StudioCommand.ReturnToClip,
            { Control: false, Key: UiKey.End } => StudioCommand.End,
            { Key: UiKey.Escape } => _closePrompt ? StudioCommand.Stay : StudioCommand.Cancel,
            _ => null
        };
        if (command is not { } selected) return false;
        _commands[selected].Execute(); return true;
    }

    private MenuAction[] ClipActions() => [ _commands.Describe(StudioCommand.PlayPause), _commands.Describe(StudioCommand.ReturnToClip), _commands.Describe(StudioCommand.End),
        _commands.Describe(StudioCommand.SetStart), _commands.Describe(StudioCommand.SetEnd), _commands.Describe(StudioCommand.Save), _commands.Describe(StudioCommand.Cancel) ];
    private bool OpenFocusedContext(IInputContext context)
    {
        if (Navigation.IsVisible)
        {
            foreach (var (id, button) in _takeButtons)
                if (FocusWithin(button)) { OpenTakeMenu(id, button, button.Bounds, context); return true; }
            foreach (var (id, button) in _clipButtons)
                if (FocusWithin(button)) { OpenClipMenu(id, button, button.Bounds, context); return true; }
        }
        var focused = FindFocus(this);
        if (focused is null || !Within(focused, Preview) && !Within(focused, Inspector) && !Within(focused, Timeline)) return false;
        OpenContext(focused, focused.Bounds, context, ClipActions());
        return true;
    }
    private static bool FocusWithin(Element element) => FindFocus(element) is not null;
    private static Element? FindFocus(Element element) => element.IsFocused ? element : element.Children.Select(FindFocus).FirstOrDefault(child => child is not null);
    private static bool Within(Element element, Element ancestor)
    {
        for (Element? node = element; node is not null; node = node.Parent)
            if (ReferenceEquals(node, ancestor)) return true;
        return false;
    }
    private void OpenTakeMenu(string id, Button owner, Rect anchor, IInputContext context)
    {
        var preview = new DelegateUiCommand(() => { SelectTake(id); Refresh(); }, () => _model.CanSeek && !_model.Trimming);
        OpenContext(owner, anchor, context,
            [new("take.preview", "Просмотреть этот дубль", preview, UiSymbol.Preview), _commands.Describe(StudioCommand.ReturnToClip)]);
    }
    private void OpenClipMenu(string id, Button owner, Rect anchor, IInputContext context)
    {
        var open = new DelegateUiCommand(() => { _model.SelectClip(id); Refresh(); }, () => _model.CanReload);
        OpenContext(owner, anchor, context,
            [new("clip.open", "Открыть этот монтажный клип", open, UiSymbol.Folder), _commands.Describe(StudioCommand.Save), _commands.Describe(StudioCommand.Cancel)]);
    }
    private void OpenContext(Element owner, Rect anchor, IInputContext context, MenuAction[] actions)
    {
        var popup = new MenuPopup(actions, () => Overlays.Close());
        try { Overlays.Open(owner, popup, anchor, context, ReferenceEquals(owner, this) ? _previewSurface : owner); _contextMenu = true; popup.FocusFirst(context); }
        catch
        {
            if (ReferenceEquals(Overlays.Popup, popup)) Overlays.Close(false);
            else if (popup.Parent is null && !popup.IsDisposed) popup.Dispose();
            throw;
        }
    }
    private void ContextClosed() { _contextMenu = false; _exportPanel = null; }
    private void SelectTake(string id) => _model.SelectTake(Array.FindIndex(_model.Scene!.Takes, take => take.Id == id));

    private void Refresh()
    {
        if (!ReferenceEquals(_shownScene, _model.Scene)) { Overlays.Close(); _shownScene = _model.Scene; }
        if (!ReferenceEquals(_pixels, _model.Pixels))
        {
            var next = _model.Pixels is { } pixels ? SkiaImageSource.CopyBgraPixels(FrameDecoder.Width, FrameDecoder.Height, pixels) : null;
            _preview.Source = next; _image?.Dispose(); _image = next; _pixels = _model.Pixels;
        }
        if (!_model.Modified) _closePrompt = false;
        CloseNotice.IsVisible = _closePrompt;
        Navigation.IsVisible = _panels.Navigation;
        Inspector.IsVisible = _panels.Inspector;
        Timeline.IsVisible = _panels.Timeline;
        foreach (var (button, command) in _buttons) button.IsEnabled = _commands[command].CanExecute;
        _frame.Value = _model.FrameLabel;
        _play.Value = _model.PlayLabel;
        _play.AccessibleName = _model.Playing ? "Приостановить просмотр · Пробел" : "Воспроизвести монтажный клип · Пробел";
        _timelineCaption.Value = "Таймлайн";
        _timelineWidth.Value = _panels.WideTimeline ? "Компактно" : "На всю ширину";
        _timelineWidth.AccessibleName = _timelineWidth.Value + " · таймлайн";
        _timelineCaption.AccessibleName = _model.MediaStatus;
        _take.Value = _model.TakeLabel;
        _mode.Value = _export?.Job is { } job ? SceneExport.StateText(job) : _model.ViewingSource ? "Исходник" : "Монтаж";
        _previewMontage.AccessibleName = _model.ViewingSource ? "Перейти к монтажу" : "Монтаж · выбран";
        _previewSource.AccessibleName = _model.ViewingSource ? "Исходник · выбран" : "Просмотреть исходник";
        _edit.Value = _model.EditLabel; _status.Value = _model.Status; _status.HasError = _model.HasError;
        _status.AccessibleName = _model.Status;
        Inspector.IsModified = _model.Modified; Inspector.HasError = _model.HasError;
        _sourceStart.Value = _model.Loaded ? $"{_model.Start:0.###}" : "—";
        _sourceEnd.Value = _model.Loaded ? $"{_model.End:0.###}" : "—";
        _editProblem.IsVisible = _model.HasError;
        _editProblem.Value = _model.HasError ? _model.Status : "";
        _editProblem.HasError = _model.HasError;
        if (_model.Scene is { } scene)
        {
            _project.Value = $"{scene.Segment.Name} / {scene.ClipTitle}";
            _revision.Value = $"Рев. {scene.Revision} · {scene.Fps:0.###} кадр/с";
            _savedRange.Value = $"Сохранено · [{scene.Clip.SourceInFrame:0.###}, {scene.Clip.SourceEnd:0.###}) к.";
            _duration.Value = $"{_model.TimelineDuration} кадров · {_model.TimelineDuration / scene.Fps:0.###} с";
            Title = $"{scene.ProjectName}{(_model.Modified ? " •" : "")} · Tales Studio";
            SyncRows(_clipButtons, _clips, scene.Choices.Select(row => (row.Clip.Id, row.Clip.Id == scene.Clip.Id ? $"{row.Title} · выбран" : row.Title)),
                id => _model.SelectClip(id), _model.CanReload);
            SyncRows(_takeButtons, _takes, scene.Takes.Select((row, index) => (row.Id,
                $"{row.Name ?? $"Дубль {index + 1}"}{(row.Id == scene.Clip.TakeId ? " · монтаж" : "")}{(index == _model.TakeIndex && _model.ViewingSource ? " · выбран" : "")}")), SelectTake, _model.CanSeek && !_model.Trimming);
        }
        _exportPanel?.Refresh();
        _header?.Refresh(); _commands.Refresh(); _strip.Invalidate(); _layout.Sync();
    }

    private void SyncRows(Dictionary<string, Button> rows, VStack list, IEnumerable<(string Id, string Label)> entries, Action<string> select, bool enabled)
    {
        var values = entries.ToArray();
        foreach (var (id, label) in values)
        {
            if (!rows.TryGetValue(id, out var button))
            {
                button = Button.Simple("").WithRole(CommandRole).OnClick(() => { select(id); Refresh(); });
                rows.Add(id, button); list.Add(button);
            }
            button.Value = label; button.IsEnabled = enabled; button.AccessibleName = label;
        }
        foreach (var id in rows.Keys.Where(id => !values.Any(row => row.Id == id)).ToArray())
        { var button = rows[id]; list.Items.Remove(button); button.Dispose(); rows.Remove(id); }
    }

    public override void Dispose()
    {
        _strip.CancelGesture();
        Closing -= ConfirmClose; _requestClose = null;
        Overlays.Closed -= ContextClosed;
        _export?.Dispose();
        _preview.Source = null; _image?.Dispose(); _image = null;
        base.Dispose();
    }
}
