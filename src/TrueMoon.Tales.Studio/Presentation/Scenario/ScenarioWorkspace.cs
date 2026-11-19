using TrueMoon.Alloy.Rendering.Skia;
using TrueMoon.Alloy.Runtime;
using TrueMoon.Argentis;
using TrueMoon.Tales.Studio.Backend;
using TrueMoon.Tales.Studio.Contracts.Projects;
using TrueMoon.Tales.Studio.Presentation.Shell;

namespace TrueMoon.Tales.Studio.Presentation.Scenario;

internal sealed partial class ScenarioWorkspace : Border, IUiFrameParticipant
{
    private readonly BackendConnection _backend;
    private readonly CancellationTokenSource _closing = new();
    private readonly ContentControl _body = new();
    private readonly SplitView _browser;
    private readonly VStack _segments = Stack(), _actions = Stack(), _sources = Stack(), _takes = Stack(), _clips = Stack(), _bindings = Stack();
    private readonly Text _title = new("Сценарий и материалы"), _status = new(), _actionInfo = new(), _sourceInfo = new();
    private readonly TextBox _search = new() { AccessibleName = "Поиск источника" };
    private readonly TextBox _previewFrame = new() { AccessibleName = "Кадр дубля", Value = "0" };
    private readonly Button _back, _reload, _commit, _cancel, _newSegment, _editSegment, _newAction, _editAction,
        _link, _place, _assign, _import, _addTake, _relink, _frameA, _frameB;
    private readonly FrameSlot _first = new("A"), _second = new("B");
    private Task<Action>? _pending;
    private OpenedBackendProject? _baseline;
    private Func<ScenarioCommand>? _draft;
    private bool _stale;
    private string _query = "";
    private string? _segmentId, _actionId, _sourceId, _takeId, _clipId;

    internal bool Busy => _pending is not null;
    internal bool HasDraft => _draft is not null;
    internal string Status => _status.Value;
    internal Action? ReturnToMontage { get; set; }
    private ScenarioSegmentContract? Segment => _baseline?.Project.Segments.FirstOrDefault(row => row.Id == _segmentId);
    private StoryActionContract? Story => Segment?.Actions.FirstOrDefault(row => row.Id == _actionId);
    private SourceContract? Source => _baseline?.Project.Sources.FirstOrDefault(row => row.Id == _sourceId);
    private TakeContract? Take => Source?.Takes.FirstOrDefault(row => row.Id == _takeId);
    private ClipContract? Clip => _baseline?.Project.Montage.Clips.FirstOrDefault(row => row.Id == _clipId && row.ActionId == _actionId);

    internal ScenarioWorkspace(BackendConnection backend)
    {
        _backend = backend;
        Padding = new Thickness(16, 40, 16, 16);
        _back = ButtonFor("К монтажу", () => ReturnToMontage?.Invoke());
        _reload = ButtonFor("Перечитать", Reload);
        _commit = ButtonFor("Сохранить", Save);
        _commit.Tone = ControlTone.Accent;
        _cancel = ButtonFor("Отменить", Cancel);
        _newSegment = ButtonFor("Новый сегмент", () => SegmentForm(null));
        _editSegment = ButtonFor("Править сегмент", () => SegmentForm(Segment));
        _newAction = ButtonFor("Новое действие", () => ActionForm(null));
        _editAction = ButtonFor("Править действие", () => ActionForm(Story));
        _link = ButtonFor("Связать источник", LinkForm);
        _place = ButtonFor("Разместить клип", PlaceForm);
        _assign = ButtonFor("Назначить дубль клипу", () =>
        {
            var clipId = Clip!.Id;
            var take = Take!;
            Form("Назначить дубль клипу", [Text.Plain($"Выбран дубль «{BackendScenarioEdit.TakeName(take)}». Монтажный участок сохранится.")],
                () => new AssignTake(clipId, take.Id));
        });
        _import = ButtonFor("Новый источник", () => ImportForm(null));
        _addTake = ButtonFor("Добавить дубль", () => ImportForm(Source));
        _relink = ButtonFor("Перепривязать файл", RelinkForm);
        _frameA = ButtonFor("Кадр A", () => Preview(_first));
        _frameB = ButtonFor("Кадр B", () => Preview(_second));

        var story = Stack(Caption("Сценарий"), Row(_newSegment, _editSegment), _segments,
            Row(_newAction, _editAction), _actions, _actionInfo, _bindings,
            Caption("Клипы действия"), _clips, _link, _place, _assign);
        var find = ButtonFor("Найти", () => { _query = _search.Value.Trim(); RefreshLists(); });
        var library = Stack(Caption("Библиотека"), new StudioHeading(_search, find), Row(_import, _addTake),
            _sources, Caption("Дубли источника"), _sourceInfo, _takes, _relink,
            new StudioHeading(Text.Plain("Кадр дубля"), _previewFrame), Row(_frameA, _frameB),
            new FramePair(_first.Content, _second.Content));
        _browser = new SplitView { Orientation = SplitOrientation.Horizontal, FirstPaneSize = 420 };
        _browser.FirstPane.MinWidth = 320; _browser.SecondPane.MinWidth = 340;
        _browser.Divider.AccessibleName = "Ширина сценария";
        _browser.FirstPane.SetContent(ScrollViewer.Create().Content(Inset(story)));
        _browser.SecondPane.SetContent(ScrollViewer.Create().Content(Inset(library)));
        _body.SetContent(_browser);
        _title.FontSize = 22;
        SetContent(new WorkspaceLayout(new StudioHeading(_title, _back), _body,
            Stack(_status, Row(_commit, _cancel, _reload))));
        RefreshCommands();
    }

    internal void Reload() => Reload(null);

    internal void Reload(BackendReadinessIssue? issue)
    {
        Start("Читаю проект…", async token =>
        {
            var opened = await _backend.OpenAsync(token).ConfigureAwait(false);
            return () =>
            {
                _baseline = opened;
                _stale = false;
                if (issue is not null && !issue.Matches(opened.Project.Revision, opened.Fingerprint))
                    Show("Монтаж изменился. Проверьте готовность снова.", error: true);
                else if (issue is not null && !SelectIssueAsset(opened.Project, issue))
                    Show("Этот ассет отсутствует в библиотеке дублей.", error: true);
                else Show(HasDraft ? "Проект перечитан. Проверьте черновик перед сохранением." : "");
                RefreshLists();
            };
        });
    }

    private bool SelectIssueAsset(ProjectContract project, BackendReadinessIssue issue)
    {
        var clip = project.Montage.Clips.FirstOrDefault(row => row.Id == issue.ClipId);
        var source = project.Sources.FirstOrDefault(row => row.Id == clip?.SourceId &&
            row.Takes.Any(take => take.Id == clip.TakeId && take.VideoAssetId == issue.AssetId)) ??
            project.Sources.FirstOrDefault(row => row.Takes.Any(take => take.VideoAssetId == issue.AssetId));
        if (source is null) return false;
        _sourceId = source.Id;
        _takeId = source.Takes.FirstOrDefault(take => take.Id == clip?.TakeId && take.VideoAssetId == issue.AssetId)?.Id ??
            source.Takes.First(take => take.VideoAssetId == issue.AssetId).Id;
        _query = _search.Value = source.Title;
        return true;
    }

    internal void ClearPreview()
    {
        _first.Clear();
        _second.Clear();
    }

    internal void ExplainClose() => Show(HasDraft ? "Сохраните или отмените черновик." :
        Busy ? "Дождитесь результата операции." : "Откройте монтаж, чтобы подтвердить остановку экспорта.", error: true);

    public bool NeedsFrame => Busy;

    public void AdvanceFrame(TimeSpan elapsed, IInputContext context)
    {
        if (_pending is not { IsCompleted: true } ready) return;
        _pending = null;
        try { ready.GetAwaiter().GetResult()(); }
        catch (BackendCommandException error)
        {
            if (error.Code == "revision_conflict") _stale = true;
            Show(error.Code switch
            {
                "revision_conflict" => "Проект изменился. Перечитайте его и проверьте черновик.",
                "access_denied" => "Этот файл недоступен по настройкам проекта.",
                "project_not_found" or "media_not_found" => "Файл не найден.",
                "invalid_media" or "invalid_frame" => "Файл или кадр недоступен для просмотра.",
                "decoder_unavailable" => "Настройте FFmpeg и ffprobe.",
                "scenario_window_conflict" => "Проверьте начало и длительность окна сегмента.",
                "scenario_clip_overlap" => "Участок пересекается с другим клипом.",
                "scenario_source_range" => "Участок выходит за длительность дубля.",
                "invalid_scenario_edit" => "Проверьте поля и выбранные связи.",
                _ => error.Message
            }, error: true);
        }
        catch (OperationCanceledException)
        {
            if (HasDraft) _stale = true;
            Show("Ответ не получен. Перечитайте проект перед повторной отправкой.", error: true);
        }
        catch (Exception error) when (error is IOException or InvalidDataException or InvalidOperationException)
        {
            if (HasDraft) _stale = true;
            Show(error.Message, error: true);
        }
        RefreshCommands();
    }

    private void Start(string message, Func<CancellationToken, Task<Action>> operation)
    {
        if (Busy) return;
        Show(message);
        _pending = operation(_closing.Token);
        RefreshCommands();
    }

    private void Save()
    {
        if (_draft is null || _baseline is not { } baseline || _stale || Busy) return;
        ScenarioCommand command;
        try { command = _draft(); }
        catch (ArgumentException error) { Show(error.Message, error: true); return; }
        Start("Сохраняю…", async token =>
        {
            var saved = await _backend.EditScenarioAsync(command, baseline, token).ConfigureAwait(false);
            return () =>
            {
                _baseline = saved;
                if (saved.Fingerprint != baseline.Fingerprint) SelectResult(command, saved.Project);
                Cancel();
                Show(saved.Fingerprint == baseline.Fingerprint ? "Изменений нет" : $"Сохранено · рев. {saved.Project.Revision}");
            };
        });
    }

    private void Preview(FrameSlot slot)
    {
        if (Take is not { VideoAssetId: { } asset } take || _baseline is not { } baseline) return;
        int number;
        try { number = Number(_previewFrame, "номер кадра"); }
        catch (ArgumentException error) { Show(error.Message, error: true); return; }
        var sourceTitle = Source!.Title;
        var takeName = BackendScenarioEdit.TakeName(take);
        Start("Читаю кадр…", async token =>
        {
            var frame = await _backend.FrameAsync(asset, number, baseline.Project.Revision, baseline.Fingerprint, token).ConfigureAwait(false);
            return () => { slot.Show(sourceTitle, takeName, frame); Show(""); };
        });
    }

    private void SelectResult(ScenarioCommand command, ProjectContract project)
    {
        switch (command)
        {
            case SaveSegment { SegmentId: null }: _segmentId = project.Segments.Last().Id; _actionId = null; break;
            case SaveAction { ActionId: null } edit:
                _actionId = project.Segments.Single(row => row.Id == edit.SegmentId).Actions.Last().Id; break;
            case ImportVideo edit:
                var source = edit.SourceId is null ? project.Sources.Last() : project.Sources.Single(row => row.Id == edit.SourceId);
                _sourceId = source.Id; _takeId = source.Takes.Last().Id; _query = _search.Value = ""; break;
            case PlaceClip: _clipId = project.Montage.Clips.Last().Id; break;
        }
    }

    private void RefreshLists()
    {
        var project = _baseline?.Project;
        if (project is null) return;
        if (Segment is null) _segmentId = project.Segments.FirstOrDefault()?.Id;
        var segment = Segment;
        if (Story is null) _actionId = segment?.Actions.FirstOrDefault()?.Id;
        if (Source is null) _sourceId = project.Sources.FirstOrDefault()?.Id;
        if (Take is null) _takeId = Source?.Takes.FirstOrDefault()?.Id;
        var action = Story;
        var clips = action is null ? [] : project.Montage.Clips.Where(row => row.ActionId == action.Id).OrderBy(row => row.StartFrame).ToArray();
        if (Clip is null) _clipId = clips.FirstOrDefault()?.Id;
        Rows(_segments, project.Segments.Select(row => (row.Id, row.Name)), _segmentId,
            id => { _segmentId = id; _actionId = null; RefreshLists(); });
        Rows(_actions, (segment?.Actions ?? []).Select(row => (row.Id, row.Title)), _actionId,
            id => { _actionId = id; RefreshLists(); });
        Rows(_sources, project.Sources.Where(row => Matches(row.Title) || Matches(row.Label) ||
            row.Takes.Any(take => Matches(BackendScenarioEdit.TakeName(take)))).Select(row => (row.Id, row.Title)), _sourceId,
            id => { _sourceId = id; _takeId = null; RefreshLists(); });
        Rows(_takes, (Source?.Takes ?? []).Select(row => (row.Id, BackendScenarioEdit.TakeName(row))), _takeId,
            id => { _takeId = id; RefreshLists(); });
        Rows(_bindings, (action?.Sources ?? []).Select(binding =>
        {
            var source = project.Sources.Single(row => row.Id == binding.SourceId);
            return (source.Id, binding.Purpose is { } purpose ? $"{source.Title} · {purpose}" : source.Title);
        }), _sourceId, id => { _sourceId = id; _takeId = null; _query = _search.Value = ""; RefreshLists(); });
        Rows(_clips, clips.Select(row =>
        {
            var source = project.Sources.Single(candidate => candidate.Id == row.SourceId);
            return (row.Id, $"{row.StartFrame} · {source.Title} · {row.DurationFrames} к.");
        }), _clipId, id => { _clipId = id; RefreshLists(); });
        _actionInfo.Value = action is null ? "" :
            $"План {action.PlannedDurationFrames?.ToString() ?? "—"} · монтаж {clips.Sum(row => (long)row.DurationFrames)} к. · источников {action.Sources.Length}";
        _sourceInfo.Value = Take?.VideoAssetId is { } asset ? ShortReference(BackendScenarioEdit.TryReference(project, asset) ?? "Файл не указан") : "";
        RefreshCommands();
    }

    private bool Matches(string text) => text.Contains(_query, StringComparison.CurrentCultureIgnoreCase);
    private static string ShortReference(string reference) => reference.Length <= 44 ? reference : "…" + reference[^43..];

    private void RefreshCommands()
    {
        var editing = HasDraft;
        var ready = !Busy && !_stale && _baseline is not null && !editing;
        var video = Take?.VideoAssetId is not null;
        _back.IsEnabled = !Busy && !editing;
        _reload.IsEnabled = !Busy;
        _browser.IsEnabled = !Busy;
        if (editing && _body.Child is { } form) form.IsEnabled = !Busy;
        _commit.IsVisible = _cancel.IsVisible = editing;
        _commit.IsEnabled = editing && !Busy && !_stale;
        _cancel.IsEnabled = !Busy;
        _newSegment.IsEnabled = _import.IsEnabled = ready;
        _editSegment.IsEnabled = _newAction.IsEnabled = ready && Segment is not null;
        _editAction.IsEnabled = ready && Story is not null;
        _link.IsEnabled = _editAction.IsEnabled && Source is not null;
        _place.IsEnabled = _link.IsEnabled && video && Story!.Sources.Any(row => row.SourceId == _sourceId);
        _assign.IsEnabled = ready && video && Clip is { } clip && clip.SourceId == _sourceId && clip.TakeId != _takeId;
        _addTake.IsEnabled = ready && Source is not null;
        _relink.IsEnabled = _frameA.IsEnabled = _frameB.IsEnabled = ready && video;
    }

    private static void Rows(VStack target, IEnumerable<(string Id, string Name)> rows, string? selection, Action<string> select)
    {
        foreach (var old in target.Items.ToArray()) { target.Items.Remove(old); old.Dispose(); }
        foreach (var (id, name) in rows)
        {
            var button = ButtonFor(name, () => select(id));
            button.Tone = selection == id ? ControlTone.Accent : ControlTone.Neutral;
            target.Items.Add(button);
        }
    }

    private void Show(string message, bool error = false)
    {
        _status.Value = message;
        _status.HasError = error;
        _status.IsVisible = message.Length > 0;
    }

    public override void Dispose()
    {
        _closing.Cancel();
        _closing.Dispose();
        if (_pending is { } pending)
            _ = pending.ContinueWith(task => _ = task.Exception, CancellationToken.None,
                TaskContinuationOptions.OnlyOnFaulted | TaskContinuationOptions.ExecuteSynchronously, TaskScheduler.Default);
        ClearPreview();
        if (_body.Child != _browser) _browser.Dispose();
        base.Dispose();
    }

    private static Button ButtonFor(string label, Action click) => Button.Simple(label).OnClick(click);
    private static VStack Stack(params Element[] children) => VStack.Create().Configure(stack => stack.Spacing = 8).WithChildren(children);
    private static HStack Row(params Element[] children) => HStack.Create().Configure(stack => stack.Spacing = 8).WithChildren(children);
    private static Border Inset(Element child) => Border.Create().Configure(border => border.Padding = new Thickness(12)).Content(child);
    private static Text Caption(string label)
    {
        var text = Text.Plain(label); text.FontSize = 17; return text;
    }

    private sealed class FrameSlot
    {
        private readonly Text _title;
        private readonly Image _view = new();
        private SkiaImageSource? _image;
        private readonly string _name;
        internal Element Content { get; }
        internal FrameSlot(string name)
        {
            _name = name;
            _title = Text.Plain("");
            Content = Stack(_title, new PreviewSurface(_view) { Height = 130 });
            Content.IsVisible = false;
        }
        internal void Show(string sourceTitle, string takeName, BackendMediaReply frame)
        {
            var image = SkiaImageSource.CopyBgraPixels(frame.Width, frame.Height, frame.Pixels);
            _view.Source = image;
            _image?.Dispose();
            _image = image;
            _title.Value = $"{_name} · {takeName} · {frame.FrameIndex}";
            _view.AccessibleName = $"{_name} · {sourceTitle} / {takeName} · {frame.FrameIndex}/{frame.FrameCount - 1}";
            Content.IsVisible = true;
        }
        internal void Clear()
        {
            _view.Source = null; _image?.Dispose(); _image = null; Content.IsVisible = false;
        }
    }

    private sealed class FramePair : ElementList
    {
        private const float Gap = 8;
        private readonly Element _first, _second;

        internal FramePair(Element first, Element second)
        {
            (_first, _second) = (first, second);
            Add(first); Add(second);
        }
        protected override Size MeasureCore(Size size, ITextLayoutService text)
        {
            var width = ColumnWidth(size.Width);
            _first.Measure(new(width, size.Height), text);
            _second.Measure(new(width, size.Height), text);
            return new(size.Width, Math.Max(_first.DesiredSize.Height, _second.DesiredSize.Height));
        }
        protected override void ArrangeCore(Rect area)
        {
            var width = ColumnWidth(area.Width);
            _first.Arrange(_first.IsVisible ? new(area.X, area.Y, width, area.Height) : default);
            var x = _first.IsVisible ? area.X + width + Gap : area.X;
            _second.Arrange(_second.IsVisible ? new(x, area.Y, width, area.Height) : default);
        }
        private float ColumnWidth(float width) => _first.IsVisible && _second.IsVisible ? Math.Max(0, (width - Gap) / 2) : width;
    }

    private sealed class WorkspaceLayout : ElementList
    {
        private const float Gap = 10;
        private readonly Element _header, _body, _footer;

        internal WorkspaceLayout(Element header, Element body, Element footer)
        {
            (_header, _body, _footer) = (header, body, footer);
            Add(header); Add(body); Add(footer);
        }

        protected override Size MeasureCore(Size available, ITextLayoutService text)
        {
            _header.Measure(available, text); _footer.Measure(available, text);
            _body.Measure(new(available.Width, BodyHeight(available.Height)), text);
            return available;
        }
        protected override void ArrangeCore(Rect area)
        {
            var top = _header.DesiredSize.Height;
            var bottom = _footer.DesiredSize.Height;
            _header.Arrange(new(area.X, area.Y, area.Width, top));
            _body.Arrange(new(area.X, area.Y + top + Gap, area.Width, BodyHeight(area.Height)));
            _footer.Arrange(new(area.X, area.Y + area.Height - bottom, area.Width, bottom));
        }
        private float BodyHeight(float height) => Math.Max(0, height - _header.DesiredSize.Height - _footer.DesiredSize.Height - 2 * Gap);
    }
}
