using TrueMoon.Alloy.Runtime;
using TrueMoon.Argentis;
using TrueMoon.Tales.Studio.Presentation.Scene;
using TrueMoon.Tales.Studio.Presentation.Shell;
using TrueMoon.Tales.Studio.Tests.Support;
using Xunit;

namespace TrueMoon.Tales.Studio.Tests.Presentation.Scene;

public sealed class ContextMenuTests
{
    private static IEnumerable<Element> Walk(Element element) => new[] { element }.Concat(element.Children.SelectMany(Walk));

    [Theory]
    [InlineData("Дубль B", "take.preview")]
    [InlineData("Клип A", "clip.open")]
    public void ShiftF10_UsesFocusedRow_AndEscapeRestoresSameFocus(string label, string action)
    {
        using var fixture = new InteractionFixture();
        var view = new SceneView(fixture.Model, new SceneViewStyle(), new StudioTitleBarFactory(new StudioTitleBarStyle()));
        using var session = new UiSession(view, new HeadlessSurface(), new(1280, 820));
        session.Update();
        var row = Assert.Single(Walk(view.Navigation).OfType<Button>(), button => button.Value.Contains(label));
        session.Focus(row);
        Assert.True(session.HandleInput(new(InputKind.KeyDown, Key: UiKey.F10, Shift: true)).Handled);
        Assert.Same(row, view.Overlays.PopupOwner);
        var popup = Assert.IsType<MenuPopup>(view.Overlays.Popup);
        Assert.Contains(popup.Children.OfType<MenuItem>(), item => item.ActionId == action);
        Assert.Empty(fixture.Server.Posts);
        session.HandleInput(new(InputKind.KeyDown, Key: UiKey.Escape));
        Assert.Null(view.Overlays.Popup);
        Assert.True(row.IsFocused);
        Assert.Equal(0, fixture.Model.FrameIndex);
    }

    [Fact]
    public void ShiftF10_OnTimelineRestoresTimelineFocus()
    {
        using var fixture = new InteractionFixture();
        var view = new SceneView(fixture.Model, new SceneViewStyle(), new StudioTitleBarFactory(new StudioTitleBarStyle()));
        using var session = new UiSession(view, new HeadlessSurface(), new(1280, 820));
        session.Update();
        var strip = Assert.Single(Walk(view.Timeline).OfType<SceneStrip>());
        session.Focus(strip);
        session.HandleInput(new(InputKind.KeyDown, Key: UiKey.F10, Shift: true));
        Assert.Same(strip, view.Overlays.PopupOwner);
        session.HandleInput(new(InputKind.KeyDown, Key: UiKey.Escape));
        Assert.True(strip.IsFocused);
        Assert.Empty(fixture.Server.Posts);
    }
}
