using TrueMoon.Argentis;

namespace TrueMoon.Tales.Studio.Presentation;

internal enum StudioCommand
{
    Reload, Save, Cancel, ReturnToClip, End, Previous, Next, SetStart, SetEnd, PlayPause,
    Navigation, Inspector, Timeline, FocusPreview, ResetLayout, Theme, Close, SaveClose, DiscardClose, Stay
}

// One command instance is shared by the title bar, buttons, shortcuts and context menus.
internal sealed class StudioCommands(Action updated)
{
    private readonly Dictionary<StudioCommand, DelegateUiCommand> _commands = [];
    public IUiCommand this[StudioCommand id] => _commands[id];
    public void Add(StudioCommand id, Action execute, Func<bool>? enabled = null) =>
        _commands.Add(id, new(() => { execute(); updated(); }, enabled));
    public void Refresh() { foreach (var command in _commands.Values) command.Refresh(); }
    public MenuAction Describe(StudioCommand id) => id switch
    {
        StudioCommand.Reload => new("project.reload", "Перечитать проект", this[id], UiSymbol.Folder),
        StudioCommand.Save => new("clip.save", "Сохранить обрезку", this[id], UiSymbol.Save, "Ctrl+S"),
        StudioCommand.Cancel => new("clip.cancel", "Отменить черновик", this[id], UiSymbol.Undo, "Esc"),
        StudioCommand.ReturnToClip => new("clip.start", "К началу клипа в монтаже", this[id], UiSymbol.Preview, "Home"),
        StudioCommand.End => new("clip.end", "К последнему кадру участка", this[id], UiSymbol.Step, "End"),
        StudioCommand.Previous => new("preview.previous", "Предыдущий кадр", this[id], null, "←"),
        StudioCommand.Next => new("preview.next", "Следующий кадр", this[id], UiSymbol.Step, "→"),
        StudioCommand.PlayPause => new("preview.play", "Воспроизвести / пауза", this[id], UiSymbol.Preview, "Space"),
        StudioCommand.SetStart => new("clip.set-start", "Начало по текущему кадру", this[id]),
        StudioCommand.SetEnd => new("clip.set-end", "Конец после текущего кадра", this[id]),
        StudioCommand.Navigation => new("view.navigation", "Показать / скрыть навигацию", this[id], UiSymbol.Objects),
        StudioCommand.Inspector => new("view.inspector", "Показать / скрыть инспектор", this[id], UiSymbol.Info),
        StudioCommand.Timeline => new("view.timeline", "Показать / скрыть монтаж", this[id], UiSymbol.Entity),
        StudioCommand.FocusPreview => new("view.focus", "Режим просмотра / редактора", this[id], UiSymbol.Preview),
        StudioCommand.ResetLayout => new("view.reset", "Восстановить панели", this[id], UiSymbol.Restore),
        StudioCommand.Theme => new("view.theme", "Сменить светлую / тёмную тему", this[id], UiSymbol.Theme),
        StudioCommand.Close => new("app.close", "Закрыть окно", this[id], UiSymbol.Close, "Alt+F4"),
        _ => throw new ArgumentOutOfRangeException(nameof(id))
    };
}

internal sealed class StudioPanelState
{
    public bool Navigation { get; set; } = true;
    public bool Inspector { get; set; } = true;
    public bool Timeline { get; set; } = true;
    private (bool Navigation, bool Inspector, bool Timeline)? _previous;
    public void ToggleFocus()
    {
        if (_previous is { } previous) { (Navigation, Inspector, Timeline) = previous; _previous = null; }
        else { _previous = (Navigation, Inspector, Timeline); Navigation = Inspector = Timeline = false; }
    }
    public void Reset() { Navigation = Inspector = Timeline = true; _previous = null; }
}
