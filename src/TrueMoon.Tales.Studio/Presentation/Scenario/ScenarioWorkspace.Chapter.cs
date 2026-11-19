using TrueMoon.Argentis;
using TrueMoon.Tales.Studio.Backend;

namespace TrueMoon.Tales.Studio.Presentation.Scenario;

internal sealed partial class ScenarioWorkspace
{
    private AdaptationText? SavedChapter(string segmentId, string? actionId)
    {
        var texts = BackendAdaptation.Read(_baseline!.Project).Texts;
        return texts.FirstOrDefault(row => row.SegmentId == segmentId && row.ActionId == actionId) ??
            texts.FirstOrDefault(row => row.SegmentId == segmentId && row.ActionId is null);
    }

    private void PickChapter(bool show)
    {
        _showChapter = show;
        ShowChapter(show);
    }

    private void ShowChapter(bool show)
    {
        _chapterDetails.IsVisible = show;
        _library.IsVisible = !show;
        _sceneNotes.IsVisible = show && Segment is not null;
        _actionNotes.IsVisible = show && Story is not null;
        _chapterTab.Tone = show ? ControlTone.Accent : ControlTone.Neutral;
        _libraryTab.Tone = show ? ControlTone.Neutral : ControlTone.Accent;
    }

    private static RichTextEditor ReadOnlyText(string label, float height) => new(new() { IsReadOnly = true })
        { AccessibleName = label, Height = height };

    private sealed class ChapterPane : Border
    {
        private readonly Text _path = Text.Plain(""), _version = Text.Plain("");
        private readonly RichTextEditor _excerpt = ReadOnlyText("Сохранённый фрагмент главы", 320);

        internal ChapterPane() => SetContent(Stack(_path, _version, _excerpt));

        internal void Show(AdaptationText? binding)
        {
            var fragment = binding?.Fragment;
            _path.Value = fragment is null ? "Фрагмент главы ещё не привязан." : Path.GetFileName(fragment.Reference);
            _path.AccessibleName = fragment?.Reference ?? _path.Value;
            _version.IsVisible = _excerpt.IsVisible = fragment is not null;
            _version.Value = fragment is null ? "" : $"Текст {(binding!.ActionId is null ? "сегмента" : "действия")} · строки {fragment.FirstLine}–{fragment.LastLine} · {fragment.Sha256[..12]}";
            _version.AccessibleName = fragment is null ? "" : $"{_version.Value} · SHA-256 {fragment.Sha256}";
            _excerpt.Text = fragment?.Excerpt ?? "";
        }
    }
}
