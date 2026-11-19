using TrueMoon.Alloy.Rendering.Skia;
using TrueMoon.Argentis;
using TrueMoon.Tales.Studio.Backend;
using TrueMoon.Tales.Studio.Presentation.Shell;

namespace TrueMoon.Tales.Studio.Presentation.Scene;

internal sealed partial class StoryboardWorkspace
{
    private readonly Button _noteToggle, _addNote;
    private readonly HStack _framePicker;
    private readonly TextBox _frameNumber = new() { Width = 100, AccessibleName = "Кадр плана" };
    private readonly VStack _noteDetails, _noteRows = Stack().Configure(stack => stack.IsVisible = false);
    private readonly Text _notePosition = new(), _noteStatus = new();
    private readonly RichTextEditor _noteText = Note("Текст замечания");
    private readonly Image _noteImage = new();
    private readonly List<(FrameNote Note, Button Button)> _noteChoices = [];
    private Button _editFrameNote = null!, _saveFrameNote = null!, _cancelFrameNote = null!, _jumpFrameNote = null!;
    private PreviewSurface _noteSurface = null!;
    private SkiaImageSource? _notePixels;
    private StoryboardShot? _noteShot;
    private FrameNoteAnchor? _planAnchor, _exitAnchor, _entryAnchor, _newNoteAnchor;
    private string? _noteId;
    private bool _noteMode, _noteDraft, _noteStale;

    private VStack CreateFrameNoteDetails()
    {
        _noteSurface = new(_noteImage) { Height = 224, IsVisible = false };
        _editFrameNote = Command("Править замечание", () => { _noteDraft = true; SyncCommands(); });
        _saveFrameNote = Command("Сохранить замечание", SaveFrameNote);
        _cancelFrameNote = Command("Отменить замечание", CancelFrameNote);
        _jumpFrameNote = Command("К кадру на шкале", JumpToNote);
        return Stack(_notePosition, _noteStatus, Row(_editFrameNote, _saveFrameNote, _cancelFrameNote, _jumpFrameNote),
            _noteSurface, Text.Plain(_noteText.AccessibleName), _noteText).Configure(stack => stack.IsVisible = false);
    }

    private void ToggleFrameNotes()
    {
        if (NeedsFrame || _joinDraft || _noteDraft) return;
        if (_noteMode) LeaveFrameNotes();
        else EnterFrameNotes();
        Reload();
    }

    private void EnterFrameNotes()
    {
        if (_joinMode)
        {
            _joinListWidth = _split.FirstPaneSize;
            _joinMode = false;
            _joinToggle.Value = "Стыки сцен";
            _joinRows.IsVisible = _joinDetails.IsVisible = false;
            _exitSide.ClearPreview();
            _entrySide.ClearPreview();
            _selectedJoin = null;
            _exitAnchor = _entryAnchor = null;
            _split.FirstPaneSize = _planListWidth;
        }
        _noteMode = true;
        _noteToggle.Value = "Планы";
        _split.Divider.AccessibleName = "Ширина списка замечаний";
        _rows.IsVisible = _planDetails.IsVisible = false;
        _noteRows.IsVisible = true;
        _selected = null;
        ClearPreview();
    }

    private void LeaveFrameNotes()
    {
        _noteMode = false;
        _noteToggle.Value = "Замечания";
        _split.Divider.AccessibleName = "Ширина списка планов";
        _noteRows.IsVisible = _noteDetails.IsVisible = false;
        _rows.IsVisible = _planDetails.IsVisible = true;
        _noteId = null;
        ClearNotePreview();
    }

    private void BeginFrameNote(FrameNoteAnchor? anchor)
    {
        if (anchor is null || NeedsFrame || _joinDraft || _noteDraft || _snapshot is not { SchemaVersion: 2 } snapshot) return;
        EnterFrameNotes();
        _noteId = null;
        _newNoteAnchor = anchor;
        _noteDraft = true;
        _noteText.Text = "";
        RefreshFrameNotes(snapshot, StoryboardShot.Read(snapshot.Project));
    }

    private void RefreshFrameNotes(OpenedBackendProject snapshot, StoryboardShot[] shots)
    {
        var notes = BackendFrameNotes.Read(snapshot.Project);
        _snapshot = snapshot;
        _noteStale = false;
        foreach (var row in _noteRows.Items.ToArray()) { _noteRows.Items.Remove(row); row.Dispose(); }
        _noteChoices.Clear();
        foreach (var note in notes)
        {
            var location = BackendFrameNotes.Locate(snapshot.Project, note.Anchor);
            var end = note.Text.IndexOf('\n');
            var firstLine = end < 0 ? note.Text : note.Text[..end];
            var label = $"{location?.Frame ?? note.Anchor.OriginalFrame:0000} · {firstLine}";
            var button = Command(label, () => SelectFrameNote(note, snapshot, shots));
            button.AccessibleName = label;
            _noteRows.Add(button);
            _noteChoices.Add((note, button));
        }
        _count.Value = $"Замечания: {notes.Length} · ревизия {snapshot.Project.Revision}";
        if (_newNoteAnchor is { } draft)
            PreviewFrameNote(draft, snapshot, shots);
        else if (notes.SingleOrDefault(row => row.Id == _noteId) is { } selected)
            SelectFrameNote(selected, snapshot, shots);
        else if (_noteDraft)
        {
            _noteStale = true;
            ClearNotePreview();
            ReportUnverifiedFrame("Замечание отсутствует в текущей ревизии.");
            SyncCommands();
        }
        else
        {
            _noteDetails.IsVisible = false;
            Show(notes.Length == 0 ? "Откройте кадр дубля или опору, чтобы оставить замечание." : "Выберите замечание.");
            SyncCommands();
        }
    }

    private void SelectFrameNote(FrameNote note, OpenedBackendProject snapshot, StoryboardShot[] shots)
    {
        if (NeedsFrame) return;
        _noteId = note.Id;
        if (!_noteDraft) _noteText.Text = note.Text;
        PreviewFrameNote(note.Anchor, snapshot, shots);
    }

    private void PreviewFrameNote(FrameNoteAnchor anchor, OpenedBackendProject snapshot, StoryboardShot[] shots)
    {
        ClearNotePreview();
        _noteDetails.IsVisible = true;
        _notePosition.Value = $"Исходная позиция · кадр {anchor.OriginalFrame}";
        _noteStatus.Value = "";
        var location = BackendFrameNotes.Locate(snapshot.Project, anchor);
        var shot = location is null ? null : shots.FirstOrDefault(row => row.Position is { } position &&
            position.Window.Id == anchor.WindowId && position.ClipId == anchor.ClipId &&
            (anchor.ClipId is not null || row.ActionId == anchor.ActionId));
        if (location is null || shot is null)
        {
            ReportUnverifiedFrame("Кадр вышел из плана или материал был заменён.");
            SyncCommands();
            return;
        }
        Start("Читаю закреплённый кадр…", async token =>
        {
            var (frame, unavailable) = await ReadSideAsync(shot, location.Frame, snapshot, token).ConfigureAwait(false);
            return () =>
            {
                if (unavailable || frame is null || !anchor.Matches(frame))
                {
                    ReportUnverifiedFrame(unavailable || frame is null ? "Материал сейчас недоступен." : "Версия кадра изменилась.");
                    return;
                }
                if (_newNoteAnchor is not null && location.Frame != anchor.OriginalFrame)
                {
                    ReportUnverifiedFrame("Выберите новую позицию перед сохранением.");
                    return;
                }
                _notePixels = SkiaImageSource.CopyBgraPixels(frame.Width, frame.Height, frame.Pixels);
                _noteImage.Source = _notePixels;
                _noteImage.AccessibleName = $"Закреплённый кадр · {location.Frame}";
                _noteSurface.IsVisible = true;
                _notePosition.Value = $"{shot.Title} · кадр {location.Frame}";
                _noteStatus.Value = location.Frame == anchor.OriginalFrame ? "" : $"Перенесено с кадра {anchor.OriginalFrame}";
                _noteShot = shot with { Position = shot.Position! with { PreviewOffset = location.Offset } };
                Show("");
            };
        });
    }

    private void JumpToNote()
    {
        if (NeedsFrame || _noteDraft || _noteShot is not { Position: { } position } shot ||
            _snapshot is not { } snapshot || _noteId is null) return;
        var anchor = _noteChoices.Single(row => row.Note.Id == _noteId).Note.Anchor;
        Start("Сверяю сохранённый монтаж…", async token =>
        {
            var (frame, _) = await ReadSideAsync(shot, position.CursorFrame, snapshot, token).ConfigureAwait(false);
            if (frame is null || !anchor.Matches(frame))
                return () =>
                {
                    ClearNotePreview();
                    _notePosition.Value = $"Исходная позиция · кадр {anchor.OriginalFrame}";
                    ReportUnverifiedFrame("Версия кадра изменилась.");
                };
            var actual = await _backend.OpenAsync(token).ConfigureAwait(false);
            if (actual.Project.Revision != snapshot.Project.Revision || actual.Fingerprint != snapshot.Fingerprint)
                throw new BackendCommandException("revision_conflict");
            return () => _navigate(shot, actual, false);
        });
    }

    private void SaveFrameNote()
    {
        if (NeedsFrame || !_noteDraft || _noteStale || _snapshot is not { } snapshot ||
            _noteId is null && (_newNoteAnchor is null || _noteShot is null)) return;
        var command = new SaveFrameNote(_noteId, _newNoteAnchor, _noteText.Text);
        Start("Сохраняю замечание…", async token =>
        {
            var saved = await _backend.EditScenarioAsync(command, snapshot, token).ConfigureAwait(false);
            var id = command.Id ?? BackendFrameNotes.Read(saved.Project).Last().Id;
            var shots = StoryboardShot.Read(saved.Project);
            return () =>
            {
                _noteId = id;
                _newNoteAnchor = null;
                _noteDraft = false;
                RefreshFrameNotes(saved, shots);
            };
        });
    }

    private void CancelFrameNote()
    {
        _noteDraft = false;
        _newNoteAnchor = null;
        _noteText.Text = "";
        Reload();
    }

    private void ReportUnverifiedFrame(string reason)
    {
        _noteStatus.Value = "Нужна перепроверка · " + reason;
        _noteStatus.HasError = true;
        Show("");
    }

    private void ShowFrameNoteError(Exception error)
    {
        if (error is BackendCommandException { Code: "invalid_frame_note" or "media_busy" } refusal)
        {
            Show(refusal.Code == "invalid_frame_note"
                ? $"Введите текст замечания длиной до {BackendFrameNotes.MaxTextLength} знаков."
                : "Медиадекодер занят. Повторите попытку.", error: true);
            return;
        }
        _noteStale = true;
        ClearNotePreview();
        if (error is BackendCommandException { Code: "frame_note_unavailable" })
            ReportUnverifiedFrame("Кадр изменился до сохранения.");
        else
            Show(error is BackendCommandException { Code: "revision_conflict" }
                ? "Проект изменился. Обновите замечания."
                : "Кадр или замечание недоступны. Обновите проект.", error: true);
    }

    private void SyncFrameNoteCommands()
    {
        var editing = _noteDraft && !NeedsFrame;
        _noteText.Options = _noteText.Options with { IsReadOnly = !editing };
        _editFrameNote.IsVisible = !_noteDraft && _noteId is not null;
        _editFrameNote.IsEnabled = !NeedsFrame && !_noteStale && _snapshot?.SchemaVersion == 2;
        _saveFrameNote.IsVisible = _cancelFrameNote.IsVisible = _noteDraft;
        _saveFrameNote.IsEnabled = editing && !_noteStale && (_noteId is not null || _noteShot is not null);
        _cancelFrameNote.IsEnabled = editing;
        _jumpFrameNote.IsVisible = _noteShot is not null && !_noteDraft;
        _jumpFrameNote.IsEnabled = !NeedsFrame;
        foreach (var (note, button) in _noteChoices)
        {
            button.IsEnabled = !NeedsFrame && !_noteDraft;
            button.Tone = note.Id == _noteId ? ControlTone.Accent : ControlTone.Neutral;
        }
    }

    private void ClearNotePreview()
    {
        _noteImage.Source = null;
        _notePixels?.Dispose();
        _notePixels = null;
        _noteSurface.IsVisible = false;
        _noteStatus.HasError = false;
        _noteShot = null;
    }

    private static FrameNoteAnchor? CaptureAnchor(StoryboardShot? shot, int timeline, BackendMediaReply? frame, OpenedBackendProject snapshot)
    {
        if (shot?.Position is not { } position || frame is null) return null;
        var offset = timeline - position.Frame;
        if (shot.Video is { } asset && position.ClipId is { } clipId &&
            snapshot.Project.Montage.Clips.Single(clip => clip.Id == clipId).TakeId is { } take)
            return new(position.Window.Id, clipId, null, take, asset, timeline, offset, frame.FrameIndex, frame.Fps, frame.PixelSha256);
        return shot.ActionId is { } action && frame.SourceSha256 is { } sha && BackendConnection.IsHash(sha)
            ? new(position.Window.Id, position.ClipId, action, null, null, timeline, offset, 0, 0, sha)
            : null;
    }
}
