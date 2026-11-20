using TrueMoon.Alloy.Rendering.Skia;
using TrueMoon.Alloy.Runtime;
using TrueMoon.Argentis;
using TrueMoon.Tales.Studio.Backend;
using TrueMoon.Tales.Studio.Contracts.Projects;
using TrueMoon.Tales.Studio.Presentation.Scene;
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
    private readonly ChapterPane _chapter = new();
    private readonly RichTextEditor _scenePurpose = ReadOnlyText("Назначение сцены", 60);
    private readonly RichTextEditor _visibleAction = ReadOnlyText("Что видим", 72);
    private readonly RichTextEditor _audibleAction = ReadOnlyText("Что слышим", 60);
    private readonly Element _sceneNotes, _actionNotes, _chapterDetails, _library;
    private readonly TextBox _search = new() { AccessibleName = "Поиск источника" };
    private readonly TextBox _previewFrame = new() { AccessibleName = "Кадр дубля", Value = "0" };
    private readonly Button _back, _reload, _commit, _cancel, _newSegment, _editSegment, _newAction, _editAction,
        _link, _place, _assign, _import, _addTake, _relink, _frameA, _frameB;
    private readonly Button _segmentText, _actionText, _animaticStill, _order, _chapterTab, _libraryTab;
    private readonly FrameSlot _first = new("A"), _second = new("B");
    private Task<Action>? _pending;
    private OpenedBackendProject? _baseline;
    private Func<ScenarioCommand>? _draft;
    private Action? _refreshDraftChapter;
    private Button? _storyboardFocus;
    private bool _stale;
    private enum Pane { Chapter, Library, References }
    private Pane? _pane;
    private string _query = "";
    private string? _segmentId, _actionId, _sourceId, _takeId, _clipId;

    internal bool Busy => _pending is not null;
    internal bool HasDraft => _draft is not null || _transferDraft is not null;
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
        _order = ButtonFor("Переставить", OrderForm);
        _order.AccessibleName = "Изменить порядок планов";
        _chapterTab = ButtonFor("Текст главы", () => PickPane(Pane.Chapter));
        _libraryTab = ButtonFor("Библиотека", () => PickPane(Pane.Library));
        _segmentText = ButtonFor("Текст сегмента", () => TextForm(Segment!, null));
        _actionText = ButtonFor("Текст действия", () => TextForm(Segment!, Story));
        _animaticStill = ButtonFor("Кадр аниматика", AnimaticStillForm);
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

        _sceneNotes = Field(_scenePurpose);
        _actionNotes = Stack(Field(_visibleAction), Field(_audibleAction));
        _chapterDetails = Stack(_actionNotes, _chapter);
        var story = Stack(Caption("Сценарий"), Row(_newSegment, _editSegment), _segments,
            Row(_segmentText, _actionText), _sceneNotes,
            new StudioHeading(Caption("Порядок планов"), _order), Row(_newAction, _editAction), _actions, _actionInfo,
            _animaticStill, _bindings,
            Caption("Клипы действия"), _clips, _link, _place, _assign);
        var find = ButtonFor("Найти", () => { _query = _search.Value.Trim(); RefreshLists(); });
        _library = Stack(new StudioHeading(_search, find), Row(_import, _addTake),
            _sources, Caption("Дубли источника"), _sourceInfo, _takes, _relink, CreateTakePairPanel(), CreateTransferPanel(),
            new StudioHeading(Text.Plain("Кадр дубля"), _previewFrame), Row(_frameA, _frameB),
            new FramePair(_first.Content, _second.Content));
        _browser = new SplitView { Orientation = SplitOrientation.Horizontal, FirstPaneSize = 420 };
        _browser.FirstPane.MinWidth = 320; _browser.SecondPane.MinWidth = 340;
        _browser.Divider.AccessibleName = "Ширина сценария";
        _browser.FirstPane.SetContent(ScrollViewer.Create().Content(Inset(story)));
        var references = CreateReferencePane();
        _browser.SecondPane.SetContent(new WorkspaceLayout(Inset(Row(_chapterTab, _libraryTab, _referenceTab)),
            ScrollViewer.Create().Content(Inset(Stack(_chapterDetails, _library, references))), new Text { IsVisible = false }));
        _body.SetContent(_browser);
        _title.FontSize = 22;
        SetContent(new WorkspaceLayout(new StudioHeading(_title, _back), _body,
            Stack(_status, Row(_commit, _cancel, _reload))));
        ShowPane(Pane.Library);
        RefreshCommands();
    }

    internal void Reload() => Reload(null);

    internal void OpenStoryboard(OpenedBackendProject snapshot, StoryboardShot shot)
    {
        _baseline = snapshot;
        _stale = false;
        _pane = null;
        _segmentId = shot.SegmentId;
        _actionId = shot.ActionId;
        _clipId = shot.Position?.ClipId;
        if (Clip is { } clip)
        {
            _sourceId = clip.SourceId;
            _takeId = clip.TakeId;
            _query = _search.Value = "";
        }
        Show("");
        RefreshLists();
        var index = Array.FindIndex(Segment?.Actions ?? [], row => row.Id == shot.ActionId);
        _storyboardFocus = index < 0 ? null : (Button)_actions.Items[index];
    }

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
        _shownPair = null;
        _first.Clear();
        _second.Clear();
    }

    internal void ExplainClose() => Show(HasDraft ? "Сохраните или отмените черновик." :
        Busy ? "Дождитесь результата операции." : "Откройте монтаж, чтобы подтвердить остановку экспорта.", error: true);

    public bool NeedsFrame => Busy || _storyboardFocus is not null;

    public void AdvanceFrame(TimeSpan elapsed, IInputContext context)
    {
        if (_storyboardFocus is { } focus)
        {
            if (focus.IsDisposed) _storyboardFocus = null;
            else if (focus.Bounds is { Width: > 0, Height: > 0 } && focus.IsEffectivelyEnabled)
            {
                _storyboardFocus = null;
                context.Focus(focus);
            }
        }
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
                "scenario_order_timing" => "В таком порядке временные планы не помещаются между сохранёнными клипами. Измените порядок или отмените черновик.",
                "invalid_text_reference" => "Укажите Markdown из настроенного каталога @texts.",
                "invalid_text_range" => "Проверьте номера строк и длину фрагмента.",
                "text_changed" => "Исходный текст изменился. Прочитайте новую версию перед сохранением.",
                "text_unavailable" or "text_timeout" => "Не удалось прочитать исходный текст.",
                "invalid_animatic_still" => "Укажите доступный PNG или JPEG из настроенного каталога.",
                "invalid_reference" => "Проверьте название, категорию, метку и путь опоры.",
                "invalid_reference_target" => "Эталон, версия или назначение отсутствуют. Перечитайте проект.",
                "reference_name_exists" => "Эталон с таким названием уже есть. Выберите его и добавьте версию.",
                "reference_version_exists" => "Этот файл уже зарегистрирован. Выберите отдельный файл для новой версии.",
                "reference_input_recorded" => "Фактическая опора дубля уже записана.",
                "reference_changed" => "Содержимое файла изменилось. Для новой версии используйте отдельный файл.",
                "invalid_take_pair" => "Выберите два разных готовых дубля одного источника.",
                "take_pair_recorded" => "Связь этого финала уже записана. Для другого варианта добавьте новый дубль.",
                "take_pair_timing" => "Для Full Transfer оба дубля должны иметь 24 кадра/с и одинаковое число кадров.",
                "take_pair_changed" => "Видео отличается от записанной версии. Для сравнения нужен исходный файл.",
                "invalid_transfer" => "Проверьте черновик, роли изображений, Guide 0 и поля Full Transfer.",
                "transfer_timing" => "Нужен MP4 на 24 кадра/с с длительностью Omni.v2, от 56 до 192 кадров по сетке 17k+5.",
                "transfer_invalid_profile" => "Проверьте шесть разделов промпта, метки Picture и Video 1 и длительность MP4.",
                "transfer_prompt_sound" => "Для Full Transfer оба звуковых раздела должны содержать N/A.",
                "transfer_unavailable" or "transfer_worker_failed" => "Проверьте Python с PyAV, путь ComfyUI и файлы моделей Omni.v2.",
                "transfer_missing" => "Один из входов Full Transfer отсутствует.",
                "transfer_timeout" => "Операция Full Transfer остановилась по таймауту. Проверьте входы и попробуйте ещё раз.",
                "transfer_input_changed" => "Вход изменился. Сверьте черновик и опоры перед подготовкой.",
                "transfer_interrupted" or "transfer_key_conflict" => "У этой попытки уже есть файлы. Откройте форму заново для нового задания.",
                "transfer_damaged" => "Файлы подготовки не прошли проверку SHA.",
                "transfer_stage_path_changed" => "Путь входа или каталог ComfyUI изменился. Подготовьте задание заново.",
                "transfer_stage_conflict" => "Файл с таким именем в ComfyUI/input имеет другое содержимое. Он сохранён.",
                "transfer_not_staged" => "Сначала скопируйте и проверьте все входы Full Transfer.",
                "transfer_already_launched" => "У задания уже была попытка запуска. Обновите статус или подготовьте новое задание.",
                "transfer_run_busy" => "Другой Full Transfer выполняется в этой Studio.",
                "transfer_run_incomplete" => "Финальный MP4 ещё не готов к сохранению.",
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
        if (_baseline is not { } baseline || _stale || Busy) return;
        if (_transferDraft is not null) { PrepareTransfer(baseline); return; }
        if (_draft is null) return;
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
        if (_shownPair is not null) ClearPreview();
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
            case AddReferenceVersion edit:
                var entries = BackendReferenceLibrary.Read(project).Entries;
                var entry = edit.ReferenceId is null ? entries.Last() : entries.Single(row => row.Id == edit.ReferenceId);
                _referenceId = entry.Id;
                _referenceVersion = entry.Versions[^1].Number;
                break;
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
        var bound = segment is null ? null : SavedChapter(segment.Id, action?.Id);
        _chapter.Show(bound);
        _refreshDraftChapter?.Invoke();
        _scenePurpose.Text = segment?.Description ?? "";
        _visibleAction.Text = action?.Description ?? "";
        _audibleAction.Text = action?.SoundNotes ?? "";
        ShowPane(_pane ?? (bound is null ? Pane.Library : Pane.Chapter));
        _sourceInfo.Value = Take?.VideoAssetId is { } asset ? ShortReference(BackendScenarioEdit.TryReference(project, asset) ?? "Файл не указан") : "";
        RefreshTakePairs();
        RefreshTransfer();
        RefreshReferences(project);
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
        _order.IsEnabled = _editAction.IsEnabled && Segment?.Actions.Length > 1;
        _segmentText.IsEnabled = ready && Segment is not null;
        _actionText.IsEnabled = _animaticStill.IsEnabled = ready && Story is not null;
        _link.IsEnabled = _editAction.IsEnabled && Source is not null;
        _place.IsEnabled = _link.IsEnabled && video && Story!.Sources.Any(row => row.SourceId == _sourceId);
        _assign.IsEnabled = ready && video && Clip is { } clip && clip.SourceId == _sourceId && clip.TakeId != _takeId;
        _addTake.IsEnabled = ready && Source is not null;
        _relink.IsEnabled = _frameA.IsEnabled = _frameB.IsEnabled = ready && video;
        _linkTakePair.IsEnabled = ready && _baseline?.SchemaVersion == 2;
        _compareTakePair.IsEnabled = ready && _takePair is not null;
        _openTransfer.IsEnabled = ready && _baseline?.SchemaVersion == 2;
        _inspectTransfer.IsEnabled = _stageTransfer.IsEnabled = _openTransfer.IsEnabled;
        _launchTransfer.IsEnabled = _refreshTransferRun.IsEnabled = _stopTransfer.IsEnabled = _collectTransfer.IsEnabled = _openTransfer.IsEnabled;
        _transferId.IsEnabled = _openTransfer.IsEnabled;
        RefreshReferenceCommands(ready && _baseline?.SchemaVersion == 2);
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
