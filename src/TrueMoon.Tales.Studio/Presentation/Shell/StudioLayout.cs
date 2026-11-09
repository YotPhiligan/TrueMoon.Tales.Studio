using TrueMoon.Argentis;
using TrueMoon.Tales.Studio.Media;
using TrueMoon.Tales.Studio.Presentation;

namespace TrueMoon.Tales.Studio.Presentation.Shell;

// Application composition. Resizable navigation/inspector/timeline use shared Alloy SplitViews;
// SplitView v1 has no collapse, so panel visibility rewires the split chain while every pane
// host, divider and content subtree stays retained (no recreation, no dispose).
internal sealed class StudioLayout : ElementList
{
    // Defaults match the initial 1280x820 window: chrome 36, width 1256, content 710.
    private const float DefaultNavigation = 224, DefaultInspector = 282, DefaultTimeline = 150, Divider = 8;

    private readonly StudioPanelState _panels;
    private readonly Element _notice, _heading, _navigation, _preview, _inspector, _timeline, _footer;
    private readonly ContentControl _center = new();
    private readonly SplitView _navigationSplit = new(), _inspectorSplit = new(), _workspaceSplit = new();
    private bool _synced;
    private (bool Navigation, bool Inspector, bool Timeline) _shown = (true, true, true);

    public StudioLayout(StudioPanelState panels, Element notice, Element heading, Element navigation, Element preview, Element inspector, Element timeline, Element footer)
    {
        _panels = panels;
        _notice = notice; _heading = heading; _navigation = navigation; _preview = preview;
        _inspector = inspector; _timeline = timeline; _footer = footer;
        ConfigureSplit(_navigationSplit, SplitOrientation.Horizontal, DefaultNavigation);
        _navigationSplit.FirstPane.MinWidth = 150; _navigationSplit.FirstPane.MaxWidth = 460;
        _navigationSplit.Divider.AccessibleName = "Ширина навигации";
        ConfigureSplit(_inspectorSplit, SplitOrientation.Horizontal, 1256 - DefaultNavigation - Divider - DefaultInspector - Divider);
        _inspectorSplit.FirstPane.MinWidth = 240;
        _inspectorSplit.SecondPane.MinWidth = 200; _inspectorSplit.SecondPane.MaxWidth = 460;
        _inspectorSplit.Divider.AccessibleName = "Ширина инспектора";
        ConfigureSplit(_workspaceSplit, SplitOrientation.Vertical, 710 - DefaultTimeline - Divider);
        _workspaceSplit.FirstPane.MinHeight = 180;
        _workspaceSplit.SecondPane.MinHeight = 96; _workspaceSplit.SecondPane.MaxHeight = 360;
        _workspaceSplit.Divider.AccessibleName = "Высота монтажа";
        Add(notice); Add(heading); Add(_center); Add(footer);
        Rebuild();
    }

    private static void ConfigureSplit(SplitView split, SplitOrientation orientation, float firstPaneSize) =>
        (split.Orientation, split.FirstPaneSize) = (orientation, firstPaneSize);

    // Restores preferred pane sizes after user dragging; sizes stay UI state, never project data.
    public void ResetSizes()
    {
        _navigationSplit.FirstPaneSize = DefaultNavigation;
        _inspectorSplit.FirstPaneSize = Math.Max(0, 1256 - DefaultNavigation - Divider) - DefaultInspector - Divider;
        _workspaceSplit.FirstPaneSize = 710 - DefaultTimeline - Divider;
    }

    // Called from the shared refresh path; rebuilds the chain only when panel flags changed.
    public void Sync()
    {
        var current = (_panels.Navigation, _panels.Inspector, _panels.Timeline);
        if (_synced && current == _shown) return;
        _synced = true; _shown = current;
        Rebuild();
    }

    private void Rebuild()
    {
        _center.SetContent(null);
        foreach (var split in new[] { _navigationSplit, _inspectorSplit, _workspaceSplit })
        { split.FirstPane.SetContent(null); split.SecondPane.SetContent(null); }
        var content = _panels.Timeline ? (Element)_workspaceSplit : _preview;
        if (_panels.Timeline)
        {
            _workspaceSplit.FirstPane.SetContent(_preview);
            _workspaceSplit.SecondPane.SetContent(_timeline);
        }
        if (_panels.Inspector)
        {
            _inspectorSplit.FirstPane.SetContent(content);
            _inspectorSplit.SecondPane.SetContent(_inspector);
            content = _inspectorSplit;
        }
        if (_panels.Navigation)
        {
            _navigationSplit.FirstPane.SetContent(_navigation);
            _navigationSplit.SecondPane.SetContent(content);
            content = _navigationSplit;
        }
        _center.SetContent(content);
        Invalidate(Invalidation.Layout);
    }

    private Rect[] Slots(Size size)
    {
        var width = Math.Max(0, size.Width - 24);
        var chrome = Math.Min(36, size.Height);
        var notice = _notice.IsVisible ? Math.Min(86, Math.Max(0, size.Height - chrome)) : 0;
        var header = Math.Min(38, Math.Max(0, size.Height - chrome - notice));
        var status = Math.Min(28, Math.Max(0, size.Height - chrome - notice - header));
        var top = chrome + notice + header;
        var contentHeight = Math.Max(0, size.Height - top - status - 8);
        return [new(12, chrome, width, notice), new(12, chrome + notice, width, header),
            new(12, top, width, contentHeight), new(0, size.Height - status, size.Width, status)];
    }

    protected override Size MeasureCore(Size available, ITextLayoutService text)
    {
        var slots = Slots(available);
        _notice.Measure(new(slots[0].Width, slots[0].Height), text);
        _heading.Measure(new(slots[1].Width, slots[1].Height), text);
        _center.Measure(new(slots[2].Width, slots[2].Height), text);
        _footer.Measure(new(slots[3].Width, slots[3].Height), text);
        return available;
    }

    protected override void ArrangeCore(Rect area)
    {
        var slots = Slots(new(area.Width, area.Height));
        var children = new[] { _notice, _heading, _center, _footer };
        for (var i = 0; i < children.Length; i++) children[i].Arrange(slots[i] with { X = area.X + slots[i].X, Y = area.Y + slots[i].Y });
    }
}

internal sealed class StudioHeading : ElementList
{
    private readonly Element _left, _right;
    public StudioHeading(Element left, Element right) { _left = left; _right = right; Add(left); Add(right); }
    protected override Size MeasureCore(Size available, ITextLayoutService text)
    {
        _right.Measure(new(Math.Min(260, available.Width * .38f), available.Height), text);
        _left.Measure(new(Math.Max(0, available.Width - _right.DesiredSize.Width - 16), available.Height), text);
        return new(available.Width, Math.Max(_left.DesiredSize.Height, _right.DesiredSize.Height));
    }
    protected override void ArrangeCore(Rect area)
    {
        var right = Math.Min(area.Width * .4f, _right.DesiredSize.Width);
        _left.Arrange(new(area.X, area.Y + Math.Max(0, (area.Height - _left.DesiredSize.Height) / 2),
            Math.Max(0, area.Width - right - 16), Math.Min(area.Height, _left.DesiredSize.Height)));
        _right.Arrange(new(area.X + area.Width - right, area.Y + Math.Max(0, (area.Height - _right.DesiredSize.Height) / 2),
            right, Math.Min(area.Height, _right.DesiredSize.Height)));
    }
}

internal sealed class PreviewLayout : ElementList
{
    private readonly Element _heading, _image, _transport;
    public PreviewLayout(Element heading, Element image, Element transport)
    { _heading = heading; _image = image; _transport = transport; Add(heading); Add(image); Add(transport); }
    protected override Size MeasureCore(Size available, ITextLayoutService text)
    {
        _heading.Measure(new(available.Width, 56), text);
        _transport.Measure(new(available.Width, 68), text);
        _image.Measure(new(available.Width, Math.Max(0, available.Height - 124)), text);
        return available;
    }
    protected override void ArrangeCore(Rect area)
    {
        var top = Math.Min(56, area.Height); var bottom = Math.Min(68, area.Height - top);
        _heading.Arrange(new(area.X, area.Y, area.Width, top));
        _image.Arrange(new(area.X, area.Y + top, area.Width, Math.Max(0, area.Height - top - bottom)));
        _transport.Arrange(new(area.X, area.Y + area.Height - bottom, area.Width, bottom));
    }
}

internal sealed class PreviewSurface : ContentControl
{
    public PreviewSurface(Image image) => SetContent(image);
    public override bool Focusable => true;
    protected override void ArrangeCore(Rect area)
    {
        var scale = Math.Max(0, Math.Min(area.Width / FrameDecoder.Width, area.Height / FrameDecoder.Height));
        var width = FrameDecoder.Width * scale; var height = FrameDecoder.Height * scale;
        Child!.Arrange(new(area.X + (area.Width - width) / 2, area.Y + (area.Height - height) / 2, width, height));
    }
}

internal sealed class TimelineLayout : ElementList
{
    private readonly Element _heading, _strip;
    public TimelineLayout(Element heading, Element strip) { _heading = heading; _strip = strip; Add(heading); Add(strip); }
    protected override Size MeasureCore(Size available, ITextLayoutService text)
    { _heading.Measure(new(available.Width, 24), text); _strip.Measure(new(available.Width, Math.Max(0, available.Height - 28)), text); return available; }
    protected override void ArrangeCore(Rect area)
    { _heading.Arrange(new(area.X, area.Y, area.Width, 24)); _strip.Arrange(new(area.X, area.Y + 28, area.Width, Math.Max(0, area.Height - 28))); }
}

internal sealed class InspectorLayout : ElementList
{
    private readonly Element _heading, _fields, _actions;
    public InspectorLayout(Element heading, Element fields, Element actions)
    { _heading = heading; _fields = fields; _actions = actions; Add(heading); Add(fields); Add(actions); }
    protected override Size MeasureCore(Size available, ITextLayoutService text)
    {
        _heading.Measure(new(available.Width, 58), text);
        _fields.Measure(new(available.Width, Math.Max(0, available.Height - 134)), text);
        _actions.Measure(new(available.Width, 76), text); return available;
    }
    protected override void ArrangeCore(Rect area)
    {
        var top = Math.Min(58, area.Height); var bottom = Math.Min(76, area.Height - top);
        _heading.Arrange(new(area.X, area.Y, area.Width, top));
        _fields.Arrange(new(area.X, area.Y + top, area.Width, Math.Max(0, area.Height - top - bottom)));
        _actions.Arrange(new(area.X, area.Y + area.Height - bottom + 8, area.Width, Math.Max(0, bottom - 8)));
    }
}
