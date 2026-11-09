using TrueMoon.Alloy.Hosting;
using TrueMoon.Alloy.Runtime;
using TrueMoon.Argentis;
using TrueMoon.Tales.Studio.Presentation.Scene;

namespace TrueMoon.Tales.Studio.Presentation;

internal static class StudioPresentation
{
    public static IAppConfigurationContext UseStudioWindow(this IAppConfigurationContext context) =>
        context.UsePresentationFactory<SceneView>(options => options.UseSkiaOpenGL().UseSilkWindow(window =>
        {
            window.Title = "TrueMoon · Tales Studio";
            window.Width = 1280;
            window.Height = 820;
            window.Appearance = new WindowAppearance { Decorated = false };
            window.UseSystemAccentBorder = true;
            window.TitleBarOverContent = true;
            window.ConfigureDefaultTitleBar = (title, content, requestClose) => title.SetHeader(((SceneView)content).CreateTitleBar(requestClose));
            window.Chrome = new WindowChromeOptions { NativeDrag = true, NativeSnapLayouts = true, MinimumSize = new Size(880, 640) };
        }));
}
