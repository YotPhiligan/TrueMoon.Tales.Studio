using TrueMoon.Argentis;
using TrueMoon.Tales.Studio.Media;
using TrueMoon.Tales.Studio.Presentation;

namespace TrueMoon.Tales.Studio.Presentation.Shell;

// Application composition only. Shared interactive splitters are requested from Alloy.
internal sealed class StudioLayout : ElementList
{
    private readonly Element[] _regions;
    private readonly StudioPanelState _panels;
    public StudioLayout(StudioPanelState panels, Element notice, Element heading, Element navigation, Element preview, Element inspector, Element timeline, Element footer)
    {
        _panels = panels;
        _regions = [notice, heading, navigation, preview, inspector, timeline, footer];
        foreach (var region in _regions) Add(region);
    }
    private Rect[] Slots(Size size)
    {
        const float gap = 8;
        var width = Math.Max(0, size.Width - 24);
        var chrome = Math.Min(36, size.Height);
        var notice = _regions[0].IsVisible ? Math.Min(86, Math.Max(0, size.Height - chrome)) : 0;
        var header = Math.Min(38, Math.Max(0, size.Height - chrome - notice));
        var status = Math.Min(28, Math.Max(0, size.Height - chrome - notice - header));
        var top = chrome + notice + header;
        var contentHeight = Math.Max(0, size.Height - top - status - 8);
        var track = _panels.Timeline ? Math.Min(150, contentHeight * .3f) : 0;
        var left = _panels.Navigation ? Math.Min(size.Width < 1050 ? 188 : 224, width * .24f) : 0;
        var right = _panels.Inspector ? Math.Min(size.Width < 1050 ? 260 : 282, width * .31f) : 0;
        var center = Math.Max(0, width - left - right - (_panels.Navigation ? gap : 0) - (_panels.Inspector ? gap : 0));
        var x = 12 + left + (_panels.Navigation ? gap : 0);
        var previewHeight = Math.Max(0, contentHeight - track - (_panels.Timeline ? gap : 0));
        return [new(12, chrome, width, notice), new(12, chrome + notice, width, header),
            new(12, top, left, contentHeight), new(x, top, center, previewHeight),
            new(12 + width - right, top, right, contentHeight),
            new(x, top + previewHeight + gap, center, track),
            new(0, size.Height - status, size.Width, status)];
    }
    protected override Size MeasureCore(Size available, ITextLayoutService text)
    {
        var slots = Slots(available);
        for (var i = 0; i < _regions.Length; i++) _regions[i].Measure(new(slots[i].Width, slots[i].Height), text);
        return available;
    }
    protected override void ArrangeCore(Rect area)
    {
        var slots = Slots(new(area.Width, area.Height));
        for (var i = 0; i < _regions.Length; i++) _regions[i].Arrange(slots[i] with { X = area.X + slots[i].X, Y = area.Y + slots[i].Y });
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
