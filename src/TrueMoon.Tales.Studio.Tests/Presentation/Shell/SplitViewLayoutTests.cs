using TrueMoon.Alloy.Runtime;
using TrueMoon.Argentis;
using TrueMoon.Tales.Studio.Presentation;
using TrueMoon.Tales.Studio.Presentation.Shell;
using TrueMoon.Tales.Studio.Tests.Support;
using Xunit;

namespace TrueMoon.Tales.Studio.Tests.Presentation.Shell;

// StudioLayout wires retained Alloy SplitViews for navigation, inspector and timeline.
// Sizes are UI state: panel toggles rewire the chain, never recreate or dispose regions.
public sealed class SplitViewLayoutTests
{
    // Initial window 1280x820: chrome 36, header 38, status 28, gap 8 → content (12,74,1256,710).
    // Defaults: navigation 224, inspector 282, timeline 150, dividers 8.
    private static (StudioPanelState Panels, StudioLayout Layout, Text Notice, Text Heading, Text Navigation,
        Text Preview, Text Inspector, Text Timeline, Text Footer) Create()
    {
        var panels = new StudioPanelState();
        Text notice = new("") { IsVisible = false }, heading = new("H"), navigation = new("N"),
            preview = new("P"), inspector = new("I"), timeline = new("T"), footer = new("F");
        var layout = new StudioLayout(panels, notice, heading, navigation, preview, inspector, timeline, footer);
        return (panels, layout, notice, heading, navigation, preview, inspector, timeline, footer);
    }

    private static UiSession Session(Element layout) => new(layout, new HeadlessSurface(), new(1280, 820));

    [Fact]
    public void DefaultChain_PlacesRegions_AtPreferredSizes()
    {
        var (panels, layout, _, _, navigation, preview, inspector, timeline, _) = Create();
        using var session = Session(layout);
        session.Update();
        Assert.Equal(new(12, 74, 224, 710), navigation.Bounds);
        Assert.Equal(new(244, 74, 734, 552), preview.Bounds);
        Assert.Equal(new(244, 634, 734, 150), timeline.Bounds);
        Assert.Equal(new(986, 74, 282, 710), inspector.Bounds);
    }

    [Fact]
    public void NavigationDivider_PointerDragResizesPanes_AndResetSizesRestoresPreferred()
    {
        var (panels, layout, _, _, navigation, preview, _, _, _) = Create();
        using var session = Session(layout);
        session.Update();
        Assert.True(session.HandleInput(new(InputKind.PointerDown, 240, 400)).PointerCaptured);
        session.HandleInput(new(InputKind.PointerMove, 290, 400));
        session.HandleInput(new(InputKind.PointerUp, 290, 400));
        session.Update();
        Assert.Equal(274, navigation.Bounds.Width);
        Assert.Equal(12 + 274 + 8, preview.Bounds.X);
        layout.ResetSizes();
        session.Update();
        Assert.Equal(224, navigation.Bounds.Width);
    }

    [Fact]
    public void TimelineDivider_VerticalDragGrowsTrack_KeepingPreviewAboveIt()
    {
        var (panels, layout, _, _, _, preview, _, timeline, _) = Create();
        using var session = Session(layout);
        session.Update();
        Assert.True(session.HandleInput(new(InputKind.PointerDown, 611, 630)).PointerCaptured);
        session.HandleInput(new(InputKind.PointerMove, 611, 580));
        session.HandleInput(new(InputKind.PointerUp, 611, 580));
        session.Update();
        Assert.Equal(200, timeline.Bounds.Height);
        Assert.Equal(74, preview.Bounds.Y);
        Assert.Equal(502, preview.Bounds.Height);
        Assert.Equal(584, timeline.Bounds.Y);
    }

    [Fact]
    public void HideInspector_RewiresWorkspaceToFullWidth_AndSameRegionInstancesReturn()
    {
        var (panels, layout, _, _, _, preview, inspector, timeline, _) = Create();
        using var session = Session(layout);
        session.Update();
        panels.Inspector = false;
        layout.Sync();
        session.Update();
        Assert.Null(inspector.Parent); // detached, retained by caller; layout does not touch it
        Assert.False(inspector.IsDisposed);
        Assert.Equal(1256 - 224 - 8, preview.Bounds.Width);
        Assert.Equal(734 + 8 + 282, timeline.Bounds.Width);
        panels.Inspector = true;
        layout.Sync();
        session.Update();
        Assert.Equal(new(986, 74, 282, 710), inspector.Bounds);
        Assert.Equal(734, preview.Bounds.Width);
    }

    [Fact]
    public void ToggleFocusMode_ExpandsPreview_ThenRestoresEveryPanel()
    {
        var (panels, layout, _, _, navigation, preview, inspector, timeline, _) = Create();
        using var session = Session(layout);
        session.Update();
        panels.ToggleFocus();
        layout.Sync();
        session.Update();
        Assert.Equal(new(12, 74, 1256, 710), preview.Bounds);
        Assert.Null(navigation.Parent);
        Assert.Null(inspector.Parent);
        Assert.Null(timeline.Parent);
        panels.ToggleFocus();
        layout.Sync();
        session.Update();
        Assert.Equal(new(12, 74, 224, 710), navigation.Bounds);
        Assert.Equal(new(986, 74, 282, 710), inspector.Bounds);
        Assert.Equal(new(244, 634, 734, 150), timeline.Bounds);
    }

    [Fact]
    public void PanelHiddenAndShownAgain_KeepsDividerInteractive_WithoutRecreation()
    {
        var (panels, layout, _, _, navigation, _, _, _, _) = Create();
        using var session = Session(layout);
        session.Update();
        panels.Navigation = false;
        layout.Sync();
        session.Update();
        panels.Navigation = true;
        layout.Sync();
        session.Update();
        Assert.Equal(new(12, 74, 224, 710), navigation.Bounds);
        Assert.True(session.HandleInput(new(InputKind.PointerDown, 240, 400)).PointerCaptured);
        session.HandleInput(new(InputKind.PointerMove, 250, 400));
        session.HandleInput(new(InputKind.PointerUp, 250, 400));
        session.Update();
        Assert.Equal(234, navigation.Bounds.Width);
        Assert.False(navigation.IsDisposed);
    }
}
