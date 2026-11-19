using TrueMoon;
using TrueMoon.Alloy.Rendering.Skia;
using TrueMoon.Argentis;
using TrueMoon.Tales.Studio.Media;
using TrueMoon.Tales.Studio.Models;
using TrueMoon.Tales.Studio.Presentation;
using TrueMoon.Tales.Studio.Presentation.Shell;
using TrueMoon.Tales.Studio.Presentation.Scenario;
using TrueMoon.Tales.Studio.Backend;

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
    private readonly MontageTools? _montageTools;
    private readonly ScenarioWorkspace? _scenario;
    private readonly BackendConnection? _backend;
    private readonly Button? _tracksButton;
    private MontageTrackWorkspace? _tracks;
    private readonly Button? _workingButton;
    private WorkingReviewWorkspace? _workingReview;
    private readonly ContentControl _page = new();
    private readonly Button? _scenarioButton;
    private Action<string?>? _initialOpen;
    private readonly IFactory<StudioTitleBar, StudioTitleBarArguments> _headers;
    private readonly StudioCommands _commands;
    private readonly StudioPanelState _panels = new() { WideTimeline = true };
    private readonly Text _project = new("Открытие проекта…"), _mode = new(), _take = new(), _frame = new();
    private readonly Text _timelineCaption = new();
    private readonly Button _play;
    private readonly Button _previewMontage, _previewSource, _timelineWidth, _exportButton;
    private SceneExportPanel? _exportPanel;
    private RenderedReviewPanel? _review;
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
    private bool _closePrompt, _closeAfterSave, _contextMenu, _exportClosePrompt, _confirmExportClose;
    private readonly Element _draftCloseNotice, _exportCloseNotice;

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
        SceneExport? export = null, Action<string?>? initialOpen = null, ScenarioWorkspace? scenario = null, BackendConnection? backend = null)
    {
        _model = model; _headers = headers; _commands = new(Refresh); _export = export; _initialOpen = initialOpen; _scenario = scenario; _backend = backend;
        Title = "TrueMoon · Tales Studio"; ClientSize = new(1280, 820);
        RegisterCommands();
        _exportButton = Command("Экспорт MP4", StudioCommand.Export);
        _exportButton.Tone = ControlTone.Accent;
        _exportButton.IsVisible = _export is not null;
        Heading = new StudioHeading(_project, HStack.Create().Configure(x => x.Spacing = 8).WithChildren(_mode, _exportButton));
        var navigation = new List<Element>();
        if (_scenario is not null)
        {
            _scenarioButton = Button.Simple("Сценарий и материалы").WithRole(CommandRole).OnClick(OpenScenario);
            _scenario.ReturnToMontage = ReturnToMontage;
            navigation.Add(_scenarioButton);
        }
        navigation.AddRange([_clips, Text.Plain("Дубли клипа").WithRole(CaptionRole), _takes]);
        Navigation = Pane("Клипы проекта", navigation.ToArray());
        _previewSurface = new(_preview);
        _play = Command("Старт", StudioCommand.PlayPause);
        _previewMontage = Command("Монтаж", StudioCommand.PreviewMontage);
        _previewSource = Command("Исходник", StudioCommand.PreviewSource);
        var modes = HStack.Create().Configure(x => x.Spacing = 6).WithChildren(_previewMontage, _previewSource);
        if (backend is not null)
        {
            _workingButton = Button.Simple("Рабочий просмотр").WithRole(CommandRole).OnClick(OpenWorkingReview);
            modes.Add(_workingButton);
        }
        Preview = new PreviewLayout(Stack(new StudioHeading(modes, _take)),
            _previewSurface, Stack(new StudioHeading(_frame, _play), HStack.Create().Configure(x => x.Spacing = 6).WithChildren(
                Command("К началу", StudioCommand.ReturnToClip), Command("Пред. кадр", StudioCommand.Previous),
                Command("След. кадр", StudioCommand.Next), Command("К концу", StudioCommand.End))));
        SaveButton = Command("Сохранить обрезку", StudioCommand.Save);
        _montageTools = model.SupportsMontageEdits ? new(model, Refresh) : null;
        Inspector = Border.Create().WithRole(PaneRole).Content(new InspectorLayout(
            Stack(Text.Plain("Монтажный клип").WithRole(CaptionRole), _edit),
            ScrollViewer.Create().Content(Stack([
                _savedRange.WithRole(MutedRole), _editProblem,
                Text.Plain("Вход · кадры источника").WithRole(MutedRole), ValueBox(_sourceStart), Command("Начало по кадру", StudioCommand.SetStart),
                Text.Plain("Выход · не включается").WithRole(MutedRole), ValueBox(_sourceEnd), Command("Конец после кадра", StudioCommand.SetEnd),
                Text.Plain("Длина на шкале").WithRole(MutedRole), _duration,
                .. (_montageTools is null ? Array.Empty<Element>() : new Element[] { _montageTools })])),
            Stack(SaveButton, Command("Отменить черновик", StudioCommand.Cancel))));
        _strip = new(model, Refresh, OpenTrack) { AccessibleName = "Линейка — перемотка; тело клипа — перенос; края — обрезка; Escape — отмена" };
        _timelineWidth = Command("Компактно", StudioCommand.WideTimeline);
        var timelineActions = HStack.Create().Configure(row => row.Spacing = 6);
        if (backend is not null)
        {
            _tracksButton = Button.Simple("Дорожки").WithRole(CommandRole).OnClick(() => OpenTrack(null));
            timelineActions.Add(_tracksButton);
        }
        timelineActions.Add(_timelineWidth);
        Timeline = new TimelineLayout(new StudioHeading(_timelineCaption.WithRole(CaptionRole), timelineActions), _strip);
        var footerActions = HStack.Create().Configure(x => x.Spacing = 12).WithChildren(_revision);
        if (initialOpen is not null) footerActions.Items.Add(Command("К проектам", StudioCommand.Close));
        Footer = new StudioHeading(_status, footerActions);
        _draftCloseNotice = Stack(Text.Plain("Есть черновик обрезки. Сохранить перед закрытием?"),
            HStack.Create().Configure(x => x.Spacing = 8).WithChildren(Command("Сохранить и закрыть", StudioCommand.SaveClose),
                Command("Отбросить и закрыть", StudioCommand.DiscardClose), Command("Остаться", StudioCommand.Stay)));
        _exportCloseNotice = Stack(Text.Plain("Закрытие проекта прервёт активный экспорт."),
            HStack.Create().Configure(x => x.Spacing = 8).WithChildren(
                Button.Simple("Закрыть проект").OnClick(() => { _confirmExportClose = true; _requestClose?.Invoke(); }),
                Command("Остаться", StudioCommand.Stay)));
        CloseNotice = Border.Create().Content(Stack(_draftCloseNotice, _exportCloseNotice));
        _layout = new(_panels, CloseNotice, Heading, Navigation, Preview, Inspector, Timeline, Footer);
        Overlays = new(_layout) { PopupInsets = new(0, 36, 0, 0) };
        Overlays.Closed += ContextClosed;
        _page.SetContent(Overlays);
        Content(model, _ => _page);
        this.WithViewStyle(style);
        Closing += ConfirmClose;
        model.Open(); Refresh();
    }

    internal StudioTitleBar CreateTitleBar(Action requestClose)
    {
        _requestClose = requestClose;
        return _header = _headers.Create(new(_model, Overlays, _commands));
    }

    private bool ScenarioVisible => _scenario is not null && _page.Child == _scenario;

    private void OpenScenario() => OpenScenario(null);

    private void OpenScenario(BackendReadinessIssue? issue)
    {
        if (_scenario is null || !_model.ProjectReady || !_model.CanReload) return;
        _model.PausePlayback();
        Overlays.Close();
        _page.SetContent(_scenario);
        _scenario.Reload(issue);
    }

    private void ReturnToMontage()
    {
        _scenario?.ClearPreview();
        _page.SetContent(Overlays);
        _model.Open();
        Refresh();
    }

    private void OpenTrack(TrackSpan? track) => OpenTrack(track?.Track, track?.Id);

    private void OpenTrack(string? track, string? id, BackendReadinessIssue? issue = null)
    {
        if (_backend is null || !_model.ProjectReady || !_model.CanReload || _tracks is not null) return;
        _model.PausePlayback();
        Overlays.Close();
        _tracks = new(_backend, _model.Scene, _model.SplitFrame, track, id, CloseTracks, issue);
        _page.SetContent(_tracks);
    }

    private void CloseTracks()
    {
        var tracks = _tracks;
        _tracks = null;
        _page.SetContent(Overlays);
        tracks?.Dispose();
        _model.Open();
        Refresh();
    }

    private void OpenReview(bool segment)
    {
        if (_export?.CanReview(_model.Scene) != true || !_model.CanReload) return;
        Overlays.Close();
        _review = _export.Review(_model, segment, CloseReview);
        _page.SetContent(_review);
    }

    private void OpenWorkingReview()
    {
        if (_backend is null || !_model.Idle || !_model.CanReload || _model.Stale || _model.Scene is not { Fingerprint: { } } scene) return;
        Overlays.Close();
        _workingReview = new(_backend, scene, ShowWorkingReview, CloseWorkingReview, OpenReadinessIssue);
        _page.SetContent(_workingReview);
    }

    private void ShowWorkingReview(BackendRenderedMontage review, bool segment)
    {
        var workspace = _workingReview;
        _review = new(_model.ReviewPlayer(review, _backend!), segment, CloseReview,
            review.Bounded ? new(review.Start, review.Start + review.Frames) : null);
        _workingReview = null;
        _page.SetContent(_review);
        workspace?.Dispose();
    }

    private void CloseWorkingReview()
    {
        RemoveWorkingReview();
        _model.Open();
        Refresh();
    }

    private void RemoveWorkingReview()
    {
        var workspace = _workingReview;
        _workingReview = null;
        _page.SetContent(Overlays);
        workspace?.Dispose();
    }

    private void OpenReadinessIssue(BackendReadinessIssue issue, bool asset)
    {
        if (!_model.CanReload || _model.Stale || !issue.Matches(_model.SavedRevision, _model.SavedFingerprint) ||
            _workingReview is { CanClose: false }) return;
        if (_workingReview is not null) RemoveWorkingReview();
        Overlays.Close();
        if (!asset && issue.ClipId is { } clip)
        {
            _panels.Timeline = true;
            _model.SelectClip(clip, issue);
            Refresh();
        }
        else if (asset && issue is { Track: { } track, TrackId: { } id }) OpenTrack(track, id, issue);
        else if (asset && issue.AssetId is not null) OpenScenario(issue);
    }

    private void CloseReview()
    {
        var review = _review;
        if (review is null) return;
        _review = null;
        _page.SetContent(Overlays);
        review.Dispose();
        _model.Open();
        Refresh();
    }

    private void RegisterCommands()
    {
        _commands.Add(StudioCommand.Reload, _model.Open, () => _model.CanReload);
        _commands.Add(StudioCommand.Save, _model.Save, () => _model.CanSave);
        _commands.Add(StudioCommand.Export, () => _openExport = true, () => _export is not null &&
            _model.SavedFingerprint is not null && (_model.Scene is not null || _model.EmptyProject?.HasMontage == true));
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
        _commands.Add(StudioCommand.Stay, () => { _closePrompt = false; _exportClosePrompt = false; });
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
        if (_initialOpen is { } notify && (_model.ProjectReady || _model.HasError))
        {
            _initialOpen = null;
            if (_model.Loaded && _export?.CanInspect == true) _export.Inspect();
            notify(_model.ProjectReady ? null : _model.Status);
        }
        if (_openExport)
        {
            _openExport = false;
            if (_export is not null && _exportPanel is null)
            {
                _model.PausePlayback();
                if (_export.CanInspect) _export.Inspect();
                var panel = new SceneExportPanel(_export, _model, () => Overlays.Close(), OpenReview, OpenReadinessIssue);
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
        if (_workingReview is { CanClose: false } review) { review.Back(); return Task.FromResult(false); }
        if (_tracks?.CancelNavigation() == true) return Task.FromResult(false);
        if (ScenarioVisible && (_scenario!.HasDraft || _scenario.Busy || _export?.MayInterrupt == true))
        {
            _scenario.ExplainClose();
            return Task.FromResult(false);
        }
        if (_model.Saving) { _model.Explain("Дождитесь завершения сохранения"); Refresh(); return Task.FromResult(false); }
        if (_model.Modified)
        {
            _closePrompt = true; Overlays.Close(); Refresh();
            return Task.FromResult(false);
        }
        if (_export?.MayInterrupt == true && !_confirmExportClose)
        {
            _exportClosePrompt = true; Overlays.Close(); Refresh();
            return Task.FromResult(false);
        }
        return Task.FromResult(true);
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
        if (_workingReview is { } working)
        {
            if (input.Kind != InputKind.KeyDown || input.Key != UiKey.Escape) return false;
            working.Back();
            return true;
        }
        if (_tracks is { } tracks)
        {
            if (input.Kind != InputKind.KeyDown || input.Key != UiKey.Escape) return false;
            if (!tracks.CancelNavigation()) CloseTracks();
            return true;
        }
        if (_review is not null)
        {
            if (input.Kind == InputKind.FocusLost) _review.Pause();
            if (input.Kind != InputKind.KeyDown || input.Key != UiKey.Escape) return false;
            CloseReview();
            return true;
        }
        if (ScenarioVisible) return false;
        if (input.Kind == InputKind.KeyDown && input.Key == UiKey.Escape && _montageTools?.CancelDelete() == true) return true;
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
            { Key: UiKey.Escape } => _closePrompt || _exportClosePrompt ? StudioCommand.Stay : StudioCommand.Cancel,
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
        if (_export?.MayInterrupt != true) _exportClosePrompt = false;
        _draftCloseNotice.IsVisible = _closePrompt;
        _exportCloseNotice.IsVisible = !_closePrompt && _exportClosePrompt;
        CloseNotice.IsVisible = _closePrompt || _exportClosePrompt;
        Navigation.IsVisible = _panels.Navigation;
        Inspector.IsVisible = _panels.Inspector;
        Timeline.IsVisible = _panels.Timeline;
        _layout.ShowTrackLanes(SceneStrip.HasVisibleTracks(_model.Scene));
        foreach (var (button, command) in _buttons) button.IsEnabled = _commands[command].CanExecute;
        if (_scenarioButton is not null) _scenarioButton.IsEnabled = _model.ProjectReady && _model.CanReload;
        if (_tracksButton is not null) _tracksButton.IsEnabled = _model.ProjectReady && _model.CanReload;
        if (_workingButton is not null) _workingButton.IsEnabled = _model.Scene?.Fingerprint is not null &&
            _model.Idle && _model.CanReload && !_model.Stale;
        _frame.Value = _model.FrameLabel;
        _play.Value = _model.PlayLabel;
        _play.AccessibleName = _model.Playing ? "Приостановить просмотр · Пробел" : "Воспроизвести монтажный клип · Пробел";
        _timelineCaption.Value = "Таймлайн";
        _strip.IsEnabled = _model.EmptyProject is null;
        _strip.AccessibleName = _model.EmptyProject is not null ? "Монтажных клипов пока нет" :
            "Линейка — перемотка; тело клипа — перенос; края — обрезка; Escape — отмена";
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
        else if (_model.EmptyProject is { } empty)
        {
            _project.Value = empty.Name;
            _revision.Value = $"Рев. {empty.Revision} · {empty.Fps:0.###} кадр/с";
            _take.Value = empty.Dimensions ?? "Предпросмотр";
            _savedRange.Value = "";
            _duration.Value = "";
            _edit.Value = "Нет монтажного клипа";
            Title = $"{empty.Name} · Tales Studio";
            SyncRows(_clipButtons, _clips, [], _model.SelectClip, false);
            SyncRows(_takeButtons, _takes, [], SelectTake, false);
        }
        _exportPanel?.Refresh();
        _montageTools?.Refresh();
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
        if (ScenarioVisible || _review is not null || _tracks is not null || _workingReview is not null) Overlays.Dispose();
        if (!ScenarioVisible) _scenario?.Dispose();
        base.Dispose();
    }
}
