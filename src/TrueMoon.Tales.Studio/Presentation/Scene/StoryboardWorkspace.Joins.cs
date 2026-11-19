using TrueMoon.Alloy.Rendering.Skia;
using TrueMoon.Argentis;
using TrueMoon.Tales.Studio.Backend;
using TrueMoon.Tales.Studio.Contracts.Projects;
using TrueMoon.Tales.Studio.Presentation.Shell;

namespace TrueMoon.Tales.Studio.Presentation.Scene;

internal sealed partial class StoryboardWorkspace
{
    private readonly VStack _joinRows = Stack().Configure(stack => stack.IsVisible = false);
    private readonly JoinSide _exitSide = new("Выход"), _entrySide = new("Вход");
    private readonly RichTextEditor _bridge = Note("Что связывает сцены");
    private readonly Text _joinSummary = new(), _joinTiming = new();
    private readonly List<(Join Choice, Button Button)> _joinChoices = [];
    private Button _editJoin = null!, _saveJoin = null!, _cancelJoin = null!;
    private Join? _selectedJoin;
    private bool _joinMode, _comparePlans, _joinDraft, _joinStale;
    private float _planListWidth, _joinListWidth = 270;
    private VStack _bridgeFields = null!;

    private sealed record JoinTarget(string WindowId, string? ClipId = null, string? ActionId = null)
    {
        internal static JoinTarget ForPlan(StoryboardShot shot) => shot.Position!.ClipId is { } clip
            ? new(shot.Position.Window.Id, ClipId: clip)
            : new(shot.Position.Window.Id, ActionId: shot.ActionId);
    }

    private sealed record JoinEndpoint(string Title, int Frame, StoryboardShot? Shot);
    private sealed record Join((JoinTarget Exit, JoinTarget Entry) Key, JoinEndpoint Exit, JoinEndpoint Entry, SceneJoinNotes? Notes = null);

    private VStack CreateJoinDetails()
    {
        _editJoin = Command("Уточнить стык", () => { _joinDraft = true; SyncJoinCommands(); Show(""); });
        _saveJoin = Command("Сохранить стык", SaveJoin);
        _cancelJoin = Command("Отменить правку", CancelJoin);
        _exitSide.AddNote.OnClick(() => BeginFrameNote(_exitAnchor));
        _entrySide.AddNote.OnClick(() => BeginFrameNote(_entryAnchor));
        var comparison = new SplitView(_exitSide.Content, _entrySide.Content);
        comparison.FirstPane.MinWidth = comparison.SecondPane.MinWidth = 155;
        comparison.Divider.AccessibleName = "Ширина кадров стыка";
        _bridgeFields = Stack(Text.Plain(_bridge.AccessibleName), _bridge);
        return Stack(_joinTiming, _joinSummary, Row(_editJoin, _saveJoin, _cancelJoin), comparison, _bridgeFields)
            .Configure(stack => stack.IsVisible = false);
    }

    private void ToggleJoins(bool plans)
    {
        if (NeedsFrame || _joinDraft || _noteDraft) return;
        if (_noteMode) LeaveFrameNotes();
        SetJoinMode(enabled: !_joinMode || _comparePlans != plans, plans);
        Reload();
    }

    private void SetJoinMode(bool enabled, bool plans)
    {
        if (_joinMode) _joinListWidth = _split.FirstPaneSize;
        else _planListWidth = _split.FirstPaneSize;
        _joinMode = enabled;
        _comparePlans = enabled && plans;
        _split.FirstPaneSize = _joinMode ? _joinListWidth : _planListWidth;
        _split.Divider.AccessibleName = _joinMode ? "Ширина списка стыков" : "Ширина списка планов";
        _joinToggle.Value = _joinMode && !_comparePlans ? "Планы" : "Стыки сцен";
        _planJoinToggle.Value = _comparePlans ? "Планы" : "Стыки планов";
        _joinRows.IsVisible = _joinMode;
        _rows.IsVisible = _planDetails.IsVisible = !_joinMode;
        _joinDetails.IsVisible = false;
        _exitAnchor = _entryAnchor = null;
        _exitSide.ClearPreview();
        _entrySide.ClearPreview();
        _selectedJoin = null;
        _selected = null;
        ClearPreview();
    }

    private void RefreshJoins(OpenedBackendProject snapshot, StoryboardShot[] shots)
    {
        var previous = _selectedJoin?.Key;
        _snapshot = snapshot;
        _joinStale = false;
        foreach (var row in _joinRows.Items.ToArray()) { _joinRows.Items.Remove(row); row.Dispose(); }
        _joinChoices.Clear();
        Join? retained = null;
        var joins = _comparePlans ? PlanJoins(shots) : SceneJoins(snapshot.Project, shots);
        foreach (var join in joins)
        {
            var label = $"{join.Exit.Frame:0000} / {join.Entry.Frame:0000} · {join.Entry.Title}";
            var button = Command(label, () => SelectJoin(_joinChoices.Single(choice => choice.Choice.Key == join.Key).Choice));
            button.AccessibleName = label;
            _joinRows.Add(button);
            _joinChoices.Add((join, button));
            if (previous == join.Key) retained = join;
        }
        JoinCount();
        if (retained is not null)
        {
            SelectJoin(retained, preserveDraft: _joinDraft);
            return;
        }
        if (_joinDraft)
        {
            _joinStale = true;
            _exitSide.ClearPreview();
            _entrySide.ClearPreview();
            Show("Сцены больше не соседствуют. Черновик остался в полях.", error: true);
            return;
        }
        _selectedJoin = null;
        _joinDetails.IsVisible = false;
        Show(_joinChoices.Count == 0
            ? _comparePlans ? "На шкале нет соседних планов внутри сцены." : "На шкале нет соседних сцен."
            : "Выберите стык.");
    }

    private static IEnumerable<Join> PlanJoins(StoryboardShot[] shots) => StoryboardPlanJoin.Read(shots).Select(pair => new Join(
        (JoinTarget.ForPlan(pair.Exit), JoinTarget.ForPlan(pair.Entry)), new(pair.Exit.Title, pair.ExitFrame, pair.Exit),
        new(pair.Entry.Title, pair.EntryFrame, pair.Entry)));

    private static IEnumerable<Join> SceneJoins(ProjectContract project, StoryboardShot[] shots)
    {
        var notes = BackendSceneJoins.Read(project);
        var scenes = project.Segments.ToDictionary(scene => scene.Id);
        var windowShots = shots.Where(shot => shot.Position is not null).ToLookup(shot => shot.Position!.Window.Id);
        foreach (var pair in BackendSceneJoins.Pairs(project))
        {
            var key = (pair.Left.Id, pair.Right.Id);
            yield return new((new(pair.Left.Id), new(pair.Right.Id)),
                new(scenes[pair.Left.SegmentId!].Name, pair.ExitFrame, At(pair.Left, pair.ExitFrame)),
                new(scenes[pair.Right.SegmentId!].Name, pair.EntryFrame, At(pair.Right, pair.EntryFrame)),
                notes.GetValueOrDefault(key) ?? new(key.Item1, key.Item2, "", "", ""));
        }

        StoryboardShot? At(SegmentWindowContract window, int frame) => windowShots[window.Id]
            .FirstOrDefault(shot => shot.Position is { } at && at.Frame <= frame && frame < (long)at.Frame + at.Duration);
    }

    private void SelectJoin(Join join, bool preserveDraft = false)
    {
        if (NeedsFrame || _snapshot is not { } snapshot) return;
        _selectedJoin = join;
        _joinDetails.IsVisible = true;
        var (left, right) = (join.Exit, join.Entry);
        var showStates = join.Notes is not null;
        _exitAnchor = _entryAnchor = null;
        var gap = right.Frame - left.Frame - 1;
        _joinTiming.Value = $"Кадры {left.Frame} / {right.Frame} · " + (gap == 0 ? "Без паузы" : $"Пауза {gap} к.");
        _exitSide.Set(left.Title, left.Frame, left.Shot, showStates);
        _entrySide.Set(right.Title, right.Frame, right.Shot, showStates);
        _joinSummary.Value = join.Notes?.Summary ?? right.Shot!.SegmentName;
        if (join.Notes is { } notes && !preserveDraft) SetJoinNotes(notes);
        Start("Читаю кадры стыка…", async token =>
        {
            var exit = await ReadSideAsync(left.Shot, left.Frame, snapshot, token).ConfigureAwait(false);
            var entry = await ReadSideAsync(right.Shot, right.Frame, snapshot, token).ConfigureAwait(false);
            var actual = await _backend.OpenAsync(token).ConfigureAwait(false);
            if (actual.Project.Revision != snapshot.Project.Revision || !string.Equals(actual.Fingerprint, snapshot.Fingerprint, StringComparison.OrdinalIgnoreCase))
                throw new BackendCommandException("revision_conflict");
            return () =>
            {
                _exitSide.ShowFrame(exit.Frame, exit.Unavailable);
                _entrySide.ShowFrame(entry.Frame, entry.Unavailable);
                _exitAnchor = CaptureAnchor(left.Shot, left.Frame, exit.Frame, snapshot);
                _entryAnchor = CaptureAnchor(right.Shot, right.Frame, entry.Frame, snapshot);
                Show(preserveDraft ? "Проект перечитан. Сверьте кадры и черновик перед сохранением." : "");
            };
        });
    }

    private async Task<(BackendMediaReply? Frame, bool Unavailable)> ReadSideAsync(StoryboardShot? shot, int at,
        OpenedBackendProject snapshot, CancellationToken token)
    {
        try
        {
            return (await ReadFrameAsync(shot, shot?.Position is { } position ? at - position.Frame : 0, snapshot, token)
                .ConfigureAwait(false), false);
        }
        catch (BackendCommandException error) when (error.Code is "media_not_found" or "invalid_media" or "invalid_frame" or
            "invalid_animatic_still" or "media_io_error" or "media_unavailable" or "media_timeout" or "access_denied" or "decoder_unavailable")
        {
            return (null, true);
        }
    }

    private void SaveJoin()
    {
        if (NeedsFrame || _joinStale || !_joinDraft || _snapshot is not { } snapshot ||
            _selectedJoin is not { Notes: { } previous } join) return;
        var key = (previous.LeftWindowId, previous.RightWindowId);
        var command = new SaveSceneJoin(key.LeftWindowId, key.RightWindowId, _exitSide.State.Text, _entrySide.State.Text, _bridge.Text);
        Start("Сохраняю стык…", async token =>
        {
            var saved = await _backend.EditScenarioAsync(command, snapshot, token).ConfigureAwait(false);
            var notes = BackendSceneJoins.Read(saved.Project).GetValueOrDefault(key) ??
                new(key.LeftWindowId, key.RightWindowId, "", "", "");
            return () =>
            {
                _snapshot = saved;
                var updated = join with { Notes = notes };
                // Selection callbacks read the current list entry so reopening a join uses its latest notes.
                var index = _joinChoices.FindIndex(choice => choice.Choice.Key == join.Key);
                _joinChoices[index] = (updated, _joinChoices[index].Button);
                _selectedJoin = updated;
                _joinDraft = false;
                SetJoinNotes(notes);
                JoinCount();
                Show(saved.Fingerprint == snapshot.Fingerprint ? "Изменений нет" : $"Сохранено · рев. {saved.Project.Revision}");
            };
        });
    }

    private void CancelJoin()
    {
        _joinDraft = false;
        if (_joinStale)
        {
            _selectedJoin = null;
            _joinDetails.IsVisible = false;
            Reload();
        }
        else
        {
            if (_selectedJoin?.Notes is { } notes) SetJoinNotes(notes);
            Show("");
            SyncJoinCommands();
        }
    }

    private void SetJoinNotes(SceneJoinNotes notes)
    {
        _joinSummary.Value = notes.Summary;
        _exitSide.State.Text = notes.ExitState;
        _entrySide.State.Text = notes.EntryState;
        _bridge.Text = notes.Bridge;
    }

    private void JoinCount() => _count.Value = $"{(_comparePlans ? "Стыки планов" : "Стыки")}: {_joinChoices.Count} · ревизия {_snapshot!.Project.Revision}";

    private void SyncJoinCommands()
    {
        var editable = _joinDraft && !NeedsFrame;
        _exitSide.State.Options = _exitSide.State.Options with { IsReadOnly = !editable };
        _entrySide.State.Options = _entrySide.State.Options with { IsReadOnly = !editable };
        _bridge.Options = _bridge.Options with { IsReadOnly = !editable };
        var sceneJoin = _selectedJoin?.Notes is not null;
        _bridgeFields.IsVisible = sceneJoin && (_joinDraft || _bridge.Text.Length > 0);
        _editJoin.IsVisible = sceneJoin && !_joinDraft;
        _editJoin.IsEnabled = sceneJoin && !_joinStale && !NeedsFrame && _snapshot?.SchemaVersion == 2;
        _saveJoin.IsVisible = _cancelJoin.IsVisible = _joinDraft;
        _saveJoin.IsEnabled = editable && !_joinStale;
        _cancelJoin.IsEnabled = editable;
        _exitSide.AddNote.IsVisible = _exitAnchor is not null;
        _entrySide.AddNote.IsVisible = _entryAnchor is not null;
        _exitSide.AddNote.IsEnabled = _entrySide.AddNote.IsEnabled = !NeedsFrame && !_joinDraft && !_joinStale && _snapshot?.SchemaVersion == 2;
        foreach (var (join, button) in _joinChoices)
        {
            button.IsEnabled = !NeedsFrame && !_joinDraft;
            button.Tone = join.Key == _selectedJoin?.Key ? ControlTone.Accent : ControlTone.Neutral;
        }
    }

    private void ShowJoinError(Exception error)
    {
        if (error is BackendCommandException { Code: "invalid_scene_join" })
        {
            Show($"До {BackendSceneJoins.MaxNoteLength} знаков в поле. Для связки заполните оба состояния.", error: true);
            return;
        }
        _joinStale = true;
        _exitAnchor = _entryAnchor = null;
        _exitSide.ClearPreview();
        _entrySide.ClearPreview();
        Show(error is BackendCommandException { Code: "revision_conflict" or "scene_join_unavailable" }
            ? "Проект изменился. Обновите стыки."
            : "Стык недоступен. Обновите проект перед повторной попыткой.", error: true);
    }

    private static RichTextEditor Note(string label) => new(new() { IsReadOnly = true }) { AccessibleName = label, Height = 92, MinWidth = 120 };

    private sealed class JoinSide
    {
        private readonly string _side;
        private readonly Text _heading = new(), _planTitle = new(), _caption = new(), _sample = new();
        private readonly Image _image = new();
        private readonly PreviewSurface _surface;
        private readonly RichTextEditor _placeholder;
        private readonly VStack _stateFields;
        private SkiaImageSource? _pixels;

        internal JoinSide(string side)
        {
            _side = side;
            State = Note($"Состояние · {side}");
            _surface = new(_image) { Height = 122, IsVisible = false };
            _placeholder = Note($"Кадр · {side}");
            _placeholder.Height = 122;
            AddNote = Button.Simple(side == "Выход" ? "Замечание к выходу" : "Замечание ко входу").WithRole(SceneView.CommandRole);
            _stateFields = Stack(Text.Plain(State.AccessibleName), State);
            Content = Stack(_heading, _planTitle, _surface, _placeholder, _caption, _sample, _stateFields, AddNote);
        }

        internal VStack Content { get; }
        internal RichTextEditor State { get; }
        internal Button AddNote { get; }

        internal void Set(string title, int frame, StoryboardShot? shot, bool showState)
        {
            ClearPreview();
            _stateFields.IsVisible = showState;
            _surface.Height = _placeholder.Height = showState ? 122 : 224;
            _heading.Value = showState ? $"{_side} · {title}" : _side;
            _planTitle.Value = title;
            _planTitle.IsVisible = !showState;
            _caption.Value = $"Кадр {frame} · {shot?.Medium ?? "Пауза"}";
            _image.AccessibleName = $"{_side} · кадр {frame}";
            _placeholder.Text = shot is null ? "На границе нет плана." : shot.Video is null && !shot.Still
                ? $"{shot.Title}\n{shot.Description}".TrimEnd() : "Читаю кадр…";
        }

        internal void ShowFrame(BackendMediaReply? frame, bool unavailable)
        {
            if (frame is null)
            {
                if (unavailable) _placeholder.Text = "Кадр недоступен.";
                return;
            }
            _pixels = SkiaImageSource.CopyBgraPixels(frame.Width, frame.Height, frame.Pixels);
            _image.Source = _pixels;
            _surface.IsVisible = true;
            _placeholder.IsVisible = false;
            if (_planTitle.IsVisible)
            {
                _sample.Value = frame.SourceSha256 is { Length: >= 8 } sha
                    ? $"SHA {sha[..8]}"
                    : $"Исходный кадр {frame.FrameIndex}";
                _sample.IsVisible = true;
            }
        }

        internal void ClearPreview()
        {
            _image.Source = null;
            _pixels?.Dispose();
            _pixels = null;
            _surface.IsVisible = false;
            _placeholder.IsVisible = true;
            _placeholder.Text = "Кадр не загружен.";
            _sample.IsVisible = false;
        }
    }
}
