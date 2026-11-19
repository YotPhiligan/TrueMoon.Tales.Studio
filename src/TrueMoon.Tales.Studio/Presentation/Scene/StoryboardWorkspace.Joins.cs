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
    private bool _joinMode, _joinDraft, _joinStale;
    private float _planListWidth, _joinListWidth = 270;
    private VStack _bridgeFields = null!;

    private sealed record Join(SceneJoinWindowPair Pair, StoryboardShot? ExitShot, StoryboardShot? EntryShot, SceneJoinNotes Notes);

    private VStack CreateJoinDetails()
    {
        _editJoin = Command("Уточнить стык", () => { _joinDraft = true; SyncJoinCommands(); Show(""); });
        _saveJoin = Command("Сохранить стык", SaveJoin);
        _cancelJoin = Command("Отменить правку", CancelJoin);
        var comparison = new SplitView(_exitSide.Content, _entrySide.Content);
        comparison.FirstPane.MinWidth = comparison.SecondPane.MinWidth = 155;
        comparison.Divider.AccessibleName = "Ширина кадров стыка";
        _bridgeFields = Stack(Text.Plain(_bridge.AccessibleName), _bridge);
        return Stack(_joinTiming, _joinSummary, Row(_editJoin, _saveJoin, _cancelJoin), comparison, _bridgeFields)
            .Configure(stack => stack.IsVisible = false);
    }

    private void ToggleJoins()
    {
        if (NeedsFrame || _joinDraft) return;
        if (_joinMode) _joinListWidth = _split.FirstPaneSize;
        else _planListWidth = _split.FirstPaneSize;
        _joinMode = !_joinMode;
        _split.FirstPaneSize = _joinMode ? _joinListWidth : _planListWidth;
        _split.Divider.AccessibleName = _joinMode ? "Ширина списка стыков" : "Ширина списка планов";
        _joinToggle.Value = _joinMode ? "Планы" : "Стыки сцен";
        _joinRows.IsVisible = _joinMode;
        _rows.IsVisible = _planDetails.IsVisible = !_joinMode;
        _joinDetails.IsVisible = false;
        _exitSide.ClearPreview();
        _entrySide.ClearPreview();
        _selectedJoin = null;
        _selected = null;
        ClearPreview();
        Reload();
    }

    private void RefreshJoins(OpenedBackendProject snapshot, StoryboardShot[] shots)
    {
        var notes = BackendSceneJoins.Read(snapshot.Project);
        var scenes = snapshot.Project.Segments.ToDictionary(scene => scene.Id);
        var windowShots = shots.Where(shot => shot.Position is not null).ToLookup(shot => shot.Position!.Window.Id);
        var previous = _selectedJoin?.Notes;
        _snapshot = snapshot;
        _joinStale = false;
        foreach (var row in _joinRows.Items.ToArray()) { _joinRows.Items.Remove(row); row.Dispose(); }
        _joinChoices.Clear();
        Join? retained = null;
        foreach (var pair in BackendSceneJoins.Pairs(snapshot.Project))
        {
            var key = (pair.Left.Id, pair.Right.Id);
            var join = new Join(pair, At(pair.Left, pair.ExitFrame), At(pair.Right, pair.EntryFrame),
                notes.GetValueOrDefault(key) ?? new(key.Item1, key.Item2, "", "", ""));
            var label = $"{pair.ExitFrame:0000} / {pair.EntryFrame:0000} · {scenes[pair.Right.SegmentId!].Name}";
            var button = Command(label, () => SelectJoin(_joinChoices.Single(choice => choice.Choice.Pair == pair).Choice));
            button.AccessibleName = label;
            _joinRows.Add(button);
            _joinChoices.Add((join, button));
            if (previous?.LeftWindowId == key.Item1 && previous.RightWindowId == key.Item2) retained = join;
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
        Show(_joinChoices.Count == 0 ? "На шкале нет соседних сцен." : "Выберите стык.");

        StoryboardShot? At(SegmentWindowContract window, int frame) => windowShots[window.Id]
            .FirstOrDefault(shot => shot.Position is { } at && at.Frame <= frame && frame < (long)at.Frame + at.Duration);
    }

    private void SelectJoin(Join join, bool preserveDraft = false)
    {
        if (NeedsFrame || _snapshot is not { } snapshot) return;
        _selectedJoin = join;
        _joinDetails.IsVisible = true;
        var scenes = snapshot.Project.Segments;
        var pair = join.Pair;
        var gap = pair.EntryFrame - pair.ExitFrame - 1;
        _joinTiming.Value = $"Кадры {pair.ExitFrame} / {pair.EntryFrame} · " + (gap == 0 ? "Без паузы" : $"Пауза {gap} к.");
        _exitSide.Set(scenes.Single(scene => scene.Id == pair.Left.SegmentId).Name, pair.ExitFrame, join.ExitShot);
        _entrySide.Set(scenes.Single(scene => scene.Id == pair.Right.SegmentId).Name, pair.EntryFrame, join.EntryShot);
        if (!preserveDraft) SetJoinNotes(join.Notes);
        else _joinSummary.Value = join.Notes.Summary;
        Start("Читаю кадры стыка…", async token =>
        {
            var exit = await ReadSideAsync(join.ExitShot, pair.ExitFrame, snapshot, token).ConfigureAwait(false);
            var entry = await ReadSideAsync(join.EntryShot, pair.EntryFrame, snapshot, token).ConfigureAwait(false);
            var actual = await _backend.OpenAsync(token).ConfigureAwait(false);
            if (actual.Project.Revision != snapshot.Project.Revision || !string.Equals(actual.Fingerprint, snapshot.Fingerprint, StringComparison.OrdinalIgnoreCase))
                throw new BackendCommandException("revision_conflict");
            return () =>
            {
                _exitSide.ShowFrame(exit.Frame, exit.Unavailable);
                _entrySide.ShowFrame(entry.Frame, entry.Unavailable);
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
            "media_unavailable" or "media_timeout" or "access_denied" or "decoder_unavailable")
        {
            return (null, true);
        }
    }

    private void SaveJoin()
    {
        if (NeedsFrame || _joinStale || !_joinDraft || _snapshot is not { } snapshot || _selectedJoin is not { } join) return;
        var pair = join.Pair;
        var command = new SaveSceneJoin(pair.Left.Id, pair.Right.Id, _exitSide.State.Text, _entrySide.State.Text, _bridge.Text);
        Start("Сохраняю стык…", async token =>
        {
            var saved = await _backend.EditScenarioAsync(command, snapshot, token).ConfigureAwait(false);
            var notes = BackendSceneJoins.Read(saved.Project).GetValueOrDefault((pair.Left.Id, pair.Right.Id)) ??
                new(pair.Left.Id, pair.Right.Id, "", "", "");
            return () =>
            {
                _snapshot = saved;
                var updated = join with { Notes = notes };
                // Selection callbacks read the current list entry so reopening a join uses its latest notes.
                var index = _joinChoices.FindIndex(choice => choice.Choice.Pair == pair);
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
            if (_selectedJoin is { } join) SetJoinNotes(join.Notes);
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

    private void JoinCount() => _count.Value = $"Стыки: {_joinChoices.Count} · ревизия {_snapshot!.Project.Revision}";

    private void SyncJoinCommands()
    {
        var editable = _joinDraft && !NeedsFrame;
        _exitSide.State.Options = _exitSide.State.Options with { IsReadOnly = !editable };
        _entrySide.State.Options = _entrySide.State.Options with { IsReadOnly = !editable };
        _bridge.Options = _bridge.Options with { IsReadOnly = !editable };
        _bridgeFields.IsVisible = _joinDraft || _bridge.Text.Length > 0;
        _editJoin.IsVisible = !_joinDraft;
        _editJoin.IsEnabled = _selectedJoin is not null && !_joinStale && !NeedsFrame && _snapshot?.SchemaVersion == 2;
        _saveJoin.IsVisible = _cancelJoin.IsVisible = _joinDraft;
        _saveJoin.IsEnabled = editable && !_joinStale;
        _cancelJoin.IsEnabled = editable;
        foreach (var (join, button) in _joinChoices)
        {
            button.IsEnabled = !NeedsFrame && !_joinDraft;
            button.Tone = join.Pair == _selectedJoin?.Pair ? ControlTone.Accent : ControlTone.Neutral;
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
        private readonly Text _heading = new(), _caption = new();
        private readonly Image _image = new();
        private readonly PreviewSurface _surface;
        private readonly RichTextEditor _placeholder;
        private SkiaImageSource? _pixels;

        internal JoinSide(string side)
        {
            _side = side;
            State = Note($"Состояние · {side}");
            _surface = new(_image) { Height = 122, IsVisible = false };
            _placeholder = Note($"Кадр · {side}");
            _placeholder.Height = 122;
            Content = Stack(_heading, _surface, _placeholder, _caption, Text.Plain(State.AccessibleName), State);
        }

        internal VStack Content { get; }
        internal RichTextEditor State { get; }

        internal void Set(string scene, int frame, StoryboardShot? shot)
        {
            ClearPreview();
            _heading.Value = $"{_side} · {scene}";
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
        }

        internal void ClearPreview()
        {
            _image.Source = null;
            _pixels?.Dispose();
            _pixels = null;
            _surface.IsVisible = false;
            _placeholder.IsVisible = true;
            _placeholder.Text = "Кадр не загружен.";
        }
    }
}
