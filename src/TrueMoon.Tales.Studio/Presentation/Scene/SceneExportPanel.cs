using TrueMoon.Argentis;

namespace TrueMoon.Tales.Studio.Presentation.Scene;

internal sealed class SceneExportPanel : Border
{
    private readonly SceneExport _export;
    private readonly SceneViewModel _scene;
    private readonly Text _summary = new(), _status = new();
    private readonly ProgressBar _progress = new() { AccessibleName = "Ход экспорта", Height = 6 };
    private readonly TextBox _destination = new() { IsReadOnly = true, AccessibleName = "Каталог экспорта" };
    private readonly TextBox _name = new() { Value = "montage.mp4", AccessibleName = "Имя нового файла MP4" };
    private readonly Button[] _profiles;
    private readonly Button _check, _start, _statusButton, _cancel, _recover, _save, _close;
    private readonly Element[] _tabOrder;
    private static readonly string[] Profiles = ["draft", "project", "final"];
    protected override Color ThemeBackground => Theme.Surface;

    internal SceneExportPanel(SceneExport export, SceneViewModel scene, Action close)
    {
        _export = export; _scene = scene;
        Width = 510; Padding = new Thickness(16);
        AccessibleName = "Экспорт MP4";
        var labels = new[] { "Черновой", "Размер проекта", "Финальный" };
        _profiles = Profiles.Select((profile, index) => ActionButton(labels[index], () => export.SelectProfile(profile))).ToArray();
        _check = ActionButton("Проверить монтаж", () => { if (CanCheckScene) export.Validate(scene.Scene!); });
        _start = ActionButton("Запустить экспорт", () => { if (CanCheckScene) export.Start(scene.Scene!); });
        _statusButton = ActionButton("Прочитать статус", export.Inspect);
        _cancel = ActionButton("Отменить экспорт", export.Cancel);
        _recover = ActionButton("Восстановить", export.Recover);
        _save = ActionButton("Сохранить MP4", () => export.SaveFile(_name.Value));
        _close = ActionButton("Закрыть", close);
        _tabOrder = [.. _profiles, _check, _start, _statusButton, _cancel, _recover, _destination, _name, _save, _close];
        SetContent(VStack.Create().Configure(stack => stack.Spacing = 8).WithChildren(
            Text.Plain("Экспорт MP4").WithRole(SceneView.CaptionRole),
            Row(_profiles), _summary, _progress, _status,
            Row(_check, _start),
            Row(_statusButton, _cancel, _recover),
            Text.Plain("Каталог экспорта").WithRole(SceneView.MutedRole), _destination,
            Text.Plain("Имя нового файла").WithRole(SceneView.MutedRole), _name,
            Row(_save, _close)));
        Refresh();
    }

    private bool CanCheckScene => _scene.Scene?.Fingerprint is not null && !_scene.Modified &&
        !_scene.Saving && !_scene.Stale && !_scene.Trimming && !_scene.Moving;

    private Button ActionButton(string label, Action execute) => Button.Simple(label).WithRole(SceneView.CommandRole)
        .OnClick(() => { execute(); if (!IsDisposed) Refresh(); });

    private static HStack Row(params Element[] elements) =>
        HStack.Create().Configure(stack => stack.Spacing = 6).WithChildren(elements);

    internal void Refresh()
    {
        for (var index = 0; index < Profiles.Length; index++)
        {
            _profiles[index].IsEnabled = _export.CanCheck;
            _profiles[index].Tone = _export.Profile == Profiles[index] ? ControlTone.Accent : ControlTone.Neutral;
        }
        _check.IsEnabled = CanCheckScene && _export.CanCheck;
        _start.IsEnabled = CanCheckScene && _export.CanStart(_scene.Scene);
        _start.Tone = ControlTone.Accent;
        var job = _export.Job;
        _statusButton.IsEnabled = _export.CanInspect;
        _cancel.IsEnabled = !_export.Busy && job?.State == "running";
        _recover.IsEnabled = !_export.Busy && job?.State is "running" or "stopping" or "completed";
        _name.IsEnabled = _save.IsEnabled = !_export.Busy && job?.State == "completed";
        _destination.Value = _export.OutputDirectory ?? "";
        _destination.IsEnabled = _destination.Value.Length > 0;
        _progress.IsVisible = _export.Running;
        _progress.Value = (job?.ProgressPercent ?? 0) / 100f;
        _summary.Value = _export.Check is { } check && _export.CanStart(_scene.Scene) && CanCheckScene
            ? $"{check.Width} × {check.Height} · {check.Fps:0.###} кадр/с · {check.Frames / check.Fps:0.##} с · рев. {check.ProjectRevision}"
            : job is not null ? $"{SceneExport.StateText(job)} · рев. проекта {job.ProjectRevision}"
            : _scene.Scene is { } scene ? $"Сохранённая ревизия {scene.Revision}" : "Откройте проект";
        _status.Value = !CanCheckScene && job is null && !_export.Busy && !_export.HasError
            ? "Сохраните или отмените черновик и проверьте ревизию." : _export.Message;
        _status.IsVisible = _status.Value.Length > 0;
        _status.HasError = _export.HasError;
        _status.AccessibleName = _status.Value;
    }

    internal bool MoveFocus(UiInput input, IInputContext context)
    {
        if (input.Kind != InputKind.KeyDown || input.Key != UiKey.Tab) return false;
        var controls = _tabOrder.Where(control => control.IsEffectivelyEnabled).ToArray();
        var current = Array.FindIndex(controls, control => control.IsFocused);
        var next = current < 0 ? input.Shift ? controls.Length - 1 : 0 :
            (current + (input.Shift ? -1 : 1) + controls.Length) % controls.Length;
        context.Focus(controls[next]);
        return true;
    }

    internal void FocusFirst(IInputContext context) => context.Focus(_tabOrder.First(control => control.IsEffectivelyEnabled));
}
