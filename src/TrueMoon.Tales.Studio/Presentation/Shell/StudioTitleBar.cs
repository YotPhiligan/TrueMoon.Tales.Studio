using TrueMoon;
using TrueMoon.Argentis;
using TrueMoon.Tales.Studio.Presentation;
using TrueMoon.Tales.Studio.Presentation.Scene;

namespace TrueMoon.Tales.Studio.Presentation.Shell;

internal sealed record StudioTitleBarArguments(SceneViewModel Model, OverlayHost Overlays, StudioCommands Commands);

internal sealed class StudioTitleBarFactory(IViewStyle<StudioTitleBar> style) : IFactory<StudioTitleBar, StudioTitleBarArguments>
{
    public StudioTitleBar Create(StudioTitleBarArguments arguments) => new(arguments, style);
}

internal sealed class StudioTitleBar : View<SceneViewModel>, IWindowHeaderActivity
{
    private readonly SceneViewModel _model;
    private readonly StudioCommands _commands;
    internal MenuBar Menu { get; }
    internal Text Caption { get; } = new();
    internal Element Brand { get; } = new BrandMark();
    internal IconButton Save { get; }
    internal IconButton Back { get; }
    public bool KeepsTitleBarActive => Menu.IsExpanded || Menu.IsMenuOpen;

    public StudioTitleBar(StudioTitleBarArguments arguments, IViewStyle<StudioTitleBar> style)
    {
        _model = arguments.Model; _commands = arguments.Commands;
        Menu = new(arguments.Overlays,
        [
            new("file", "Файл", [_commands.Describe(StudioCommand.Reload), _commands.Describe(StudioCommand.Save), _commands.Describe(StudioCommand.Close)]),
            new("edit", "Правка", [_commands.Describe(StudioCommand.Save), _commands.Describe(StudioCommand.Cancel),
                _commands.Describe(StudioCommand.SetStart), _commands.Describe(StudioCommand.SetEnd)]),
            new("view", "Вид", [_commands.Describe(StudioCommand.Navigation), _commands.Describe(StudioCommand.Inspector),
                _commands.Describe(StudioCommand.Timeline), _commands.Describe(StudioCommand.FocusPreview),
                _commands.Describe(StudioCommand.ResetLayout), _commands.Describe(StudioCommand.Theme)]),
            new("clip", "Клип", [_commands.Describe(StudioCommand.PlayPause), _commands.Describe(StudioCommand.ReturnToClip), _commands.Describe(StudioCommand.End),
                _commands.Describe(StudioCommand.Previous), _commands.Describe(StudioCommand.Next)])
        ]);
        Save = new(UiSymbol.Save, "Сохранить обрезку · Ctrl+S");
        Back = new(UiSymbol.Preview, "К началу монтажного клипа · Home");
        Save.Click += () => _commands[StudioCommand.Save].Execute();
        Back.Click += () => _commands[StudioCommand.ReturnToClip].Execute();
        Content(_model, _ => new HeaderLayout(Brand, Menu, Caption, Back, Save));
        this.WithViewStyle(style);
        Refresh();
    }

    public void Refresh()
    {
        Caption.Value = $"{_model.Scene?.ProjectName ?? "Tales Studio"}{(_model.Modified ? " •" : "")} · Tales Studio";
        Caption.AccessibleName = Caption.Value;
        Save.IsEnabled = _commands[StudioCommand.Save].CanExecute;
        Back.IsEnabled = _commands[StudioCommand.ReturnToClip].CanExecute;
    }

    private sealed class BrandMark : Element
    {
        public BrandMark() { WindowRegion = WindowRegionRole.Caption; AccessibleName = "TrueMoon Tales Studio"; }
        protected override Size MeasureCore(Size available, ITextLayoutService text) => new(28, 32);
        protected override void DrawCore(IDrawingContext context) =>
            UiSymbols.Draw(context, UiSymbol.TrueMoon, new(Bounds.X + 4, Bounds.Y + (Bounds.Height - 20) / 2, 20, 20), Foreground);
    }

    private sealed class HeaderLayout : ElementList
    {
        public HeaderLayout(params Element[] elements) { WindowRegion = WindowRegionRole.Caption; foreach (var element in elements) Add(element); }
        protected override Size MeasureCore(Size available, ITextLayoutService text)
        { foreach (var element in Children) element.Measure(available, text); return new(540, 36); }
        protected override void ArrangeCore(Rect area)
        {
            var actions = Math.Min(64, area.Width);
            var brand = Math.Min(28, Math.Max(0, area.Width - actions));
            var menus = Math.Min(Children[1].DesiredSize.Width, Math.Max(0, area.Width - actions - brand));
            Children[0].Arrange(new(area.X, area.Y, brand, area.Height));
            Children[1].Arrange(new(area.X + brand, area.Y + (area.Height - 32) / 2, menus, 32));
            var captionX = area.X + brand + menus + 12;
            Children[2].Arrange(new(captionX, area.Y + (area.Height - 16) / 2,
                Math.Max(0, area.Width - brand - menus - actions - 24), 20));
            for (var i = 0; i < 2; i++) Children[i + 3].Arrange(new(area.X + area.Width - actions + i * actions / 2, area.Y + (area.Height - 32) / 2, actions / 2, 32));
        }
    }
}

internal sealed class StudioTitleBarStyle : IViewStyle<StudioTitleBar>
{
    public void Configure(IViewStyleBuilder<StudioTitleBar> styles)
    {
        styles.Root().Set(UiProperties.WindowRegion, WindowRegionRole.Caption);
        styles.Part(view => view.Brand).Foreground(theme => theme.Accent);
        styles.Part(view => view.Caption).FontSize(14);
        styles.Part(view => view.Save).BorderWidth(0);
        styles.Part(view => view.Back).BorderWidth(0);
    }
}
