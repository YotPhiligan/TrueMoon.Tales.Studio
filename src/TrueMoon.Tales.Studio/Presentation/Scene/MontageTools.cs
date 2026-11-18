using System.Globalization;
using TrueMoon.Argentis;
using TrueMoon.Tales.Studio.Models;

namespace TrueMoon.Tales.Studio.Presentation.Scene;

internal sealed class MontageTools : ContentControl
{
    private readonly SceneViewModel _model;
    private readonly Button _split, _remove, _undo, _redo, _move, _saveJoin, _removeJoin;
    private readonly TextBox _start = Number("Начало клипа, кадр"), _fadeOut = Number("Затемнение, кадры"),
        _pause = Number("Чёрная пауза, кадры"), _fadeIn = Number("Появление, кадры");
    private readonly Text _joinInfo = new();
    private readonly Element _clipFields, _joinFields;
    private SceneSnapshot? _shown;
    private bool _confirmRemove;

    internal MontageTools(SceneViewModel model, Action refresh)
    {
        _model = model;
        Button Command(string name, Action execute) => Button.Simple(name).WithRole(SceneView.CommandRole)
            .OnClick(() => { execute(); refresh(); });
        _split = Command("Разрезать по курсору", model.SplitMontageAtCursor);
        _remove = Command("Удалить клип…", () =>
        {
            if (!_confirmRemove) _confirmRemove = true;
            else { _confirmRemove = false; model.RemoveMontageSelection(); }
        });
        _undo = Command("Undo", model.UndoMontageEdit);
        _redo = Command("Redo", model.RedoMontageEdit);
        _move = Command("Перенести к кадру", () =>
        {
            if (TryRead(_start, out var value)) model.SetMontageStart(value);
            else model.Explain("Укажите номер кадра.");
        });
        _saveJoin = Command("Сохранить переход", () =>
        {
            if (TryRead(_fadeOut, out var fadeOut) && TryRead(_pause, out var pause) && TryRead(_fadeIn, out var fadeIn))
                model.SetBlackJoin(fadeOut, pause, fadeIn);
            else model.Explain("Укажите длительности перехода в кадрах.");
        });
        _removeJoin = Command("Убрать переход", () => model.SetBlackJoin(0, 0, 0));
        _joinFields = Stack(Text.Plain("Переход к следующему клипу").WithRole(SceneView.CaptionRole), _joinInfo,
            Field(_fadeOut), Field(_pause), Field(_fadeIn), _saveJoin, _removeJoin);
        _clipFields = Stack(Field(_start), _move, _split, _remove, _joinFields);
        SetContent(Stack(Text.Plain("Операции монтажа").WithRole(SceneView.CaptionRole),
            HStack.Create().Configure(row => row.Spacing = 8).WithChildren(_undo, _redo), _clipFields));
    }

    internal bool CancelDelete()
    {
        if (!_confirmRemove) return false;
        _confirmRemove = false;
        Refresh();
        return true;
    }
    internal void Refresh()
    {
        var scene = _model.Scene;
        var join = scene?.NextJoin;
        if (!ReferenceEquals(scene, _shown))
        {
            _shown = scene;
            _confirmRemove = false;
            _start.Value = Format(scene?.Clip.StartFrame ?? 0);
            _fadeOut.Value = Format(join?.FadeOut ?? 0);
            _pause.Value = Format(join?.Pause ?? 0);
            _fadeIn.Value = Format(join?.FadeIn ?? 0);
        }
        _clipFields.IsVisible = scene is not null;
        _joinFields.IsVisible = join is not null;
        _joinInfo.Value = join is null ? "" : $"Промежуток · {join.Gap} к.";
        _move.IsEnabled = _model.CanEditMontage && _model.CanBeginMove;
        _split.IsEnabled = _model.CanSplitMontage;
        _remove.IsEnabled = _model.CanRemoveMontage;
        if (!_remove.IsEnabled) _confirmRemove = false;
        _remove.Value = _confirmRemove ? "Подтвердить удаление" : "Удалить клип…";
        _undo.IsEnabled = _model.CanUndoMontage;
        _redo.IsEnabled = _model.CanRedoMontage;
        _saveJoin.IsEnabled = _removeJoin.IsEnabled = _model.CanEditMontage && join is not null;
    }

    private static TextBox Number(string label) => new() { AccessibleName = label, MinWidth = 60 };
    private static string Format(int value) => value.ToString(CultureInfo.InvariantCulture);
    private static bool TryRead(TextBox input, out int value) =>
        int.TryParse(input.Value.Trim(), NumberStyles.None, CultureInfo.InvariantCulture, out value);
    private static Element Field(TextBox input) => Stack(Text.Plain(input.AccessibleName).WithRole(SceneView.MutedRole), input);
    private static VStack Stack(params Element[] children) => VStack.Create().WithRole(SceneView.StackRole).WithChildren(children);
}
