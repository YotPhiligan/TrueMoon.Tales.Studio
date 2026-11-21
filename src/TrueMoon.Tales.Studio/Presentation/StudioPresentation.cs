using TrueMoon.Alloy.Hosting;
using TrueMoon.Alloy.Runtime;
using TrueMoon.Argentis;
using TrueMoon.Tales.Studio.Configuration;
using TrueMoon.Tales.Studio.Presentation.Scene;

namespace TrueMoon.Tales.Studio.Presentation;

internal static class StudioPresentation
{
    public static IAppConfigurationContext UseStudioWindow(this IAppConfigurationContext context, StudioUiPreferences? preferences = null) =>
        context.UsePresentation(resolver =>
        {
            var scene = resolver.Resolve<IFactory<SceneView>>()!.Create()!;
            var window = new Window { Title = "TrueMoon · Tales Studio", ClientSize = new(1280, 820) };
            window.SetContent(scene);
            window.Closing += reason => Task.FromResult(scene.ConfirmClose(reason));
            preferences?.Attach(window, StudioUiWindow.Editor);
            return window;
        }, options => options.UseSkiaOpenGL().UseSilkWindow(window =>
        {
            window.Title = "TrueMoon · Tales Studio";
            window.Width = 1280;
            window.Height = 820;
            window.Appearance = new WindowAppearance { Decorated = false };
            window.UseSystemAccentBorder = true;
            window.TitleBarOverContent = true;
            window.ConfigureDefaultTitleBar = (title, content, requestClose) =>
            {
                title.SetHeader(((SceneView)((Window)content).Child!).CreateTitleBar(requestClose));
                if (preferences is not null) StudioUiPreferences.ApplyTheme(content, preferences.Current.Theme);
            };
            window.Chrome = new WindowChromeOptions { NativeDrag = true, NativeSnapLayouts = true, MinimumSize = new Size(880, 640) };
        }));
}
