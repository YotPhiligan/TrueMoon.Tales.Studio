using TrueMoon.Argentis;

namespace TrueMoon.Tales.Studio.Presentation.Scene;

internal sealed class SceneViewStyle : IViewStyle<SceneView>
{
    public void Configure(IViewStyleBuilder<SceneView> styles)
    {
        styles.Root().Background(theme => theme.Surface).FontSize(13);
        styles.Role(SceneView.PaneRole).Background(theme => theme.Control).BorderWidth(0).Padding(new Thickness(16));
        styles.Role(SceneView.CaptionRole).FontSize(12).Foreground(theme => theme.Disabled);
        styles.Role(SceneView.MutedRole).FontSize(12).Foreground(theme => theme.Disabled);
        styles.Role(SceneView.StackRole).Spacing(8);
        styles.Role(SceneView.CommandRole).FontSize(12).Padding(new Thickness(10, 6, 10, 6)).Height(30);
        styles.Part(view => view.Heading).Background(theme => theme.Surface).Padding(new Thickness(0, 4, 0, 4));
        styles.Part(view => view.CloseNotice).Background(theme => theme.Control).Padding(new Thickness(12, 6, 12, 6));
        styles.Part(view => view.Preview).Background(theme => theme.Input).Padding(new Thickness(12));
        styles.Part(view => view.Timeline).Background(theme => theme.Control).Padding(new Thickness(16, 12, 16, 12));
        styles.Part(view => view.Footer).Padding(new Thickness(16, 0, 16, 0)).FontSize(11);
        styles.Part(view => view.StatusText).FontSize(12).Foreground(theme => theme.Foreground);
        styles.Part(view => view.ProjectTitle).FontSize(14);
        styles.Part(view => view.SaveButton).Tone(ControlTone.Accent);
    }
}
