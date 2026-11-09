using TrueMoon.Argentis;
using TrueMoon.Tales.Studio.Configuration;
using TrueMoon.Tales.Studio.Media;
using TrueMoon.Tales.Studio.Presentation.Scene;
using TrueMoon.Tales.Studio.Presentation.Shell;

namespace TrueMoon.Tales.Studio.Infrastructure;

internal static class StudioServices
{
    // Cobalt owns the services; StudioHttpClient owns its transport. The UI host owns factory-created views.
    public static IAppConfigurationContext UseStudioServices(this IAppConfigurationContext context)
    {
        ArgumentNullException.ThrowIfNull(context);
        return context.Services(services => services
            .Singleton<StudioHttpClient>()
            .Singleton<IFrameDecoder>(resolver => new FrameDecoder(resolver.Resolve<StudioOptions>().FfmpegPath))
            .Singleton<IPreviewMedia>(resolver => new FfmpegPreview(resolver.Resolve<StudioOptions>().FfmpegPath, resolver.Resolve<StudioHttpClient>()))
            .Singleton<SceneViewModel>()
            .Singleton<IViewStyle<SceneView>, SceneViewStyle>()
            .Singleton<IViewStyle<StudioTitleBar>, StudioTitleBarStyle>()
            .Singleton<IFactory<StudioTitleBar, StudioTitleBarArguments>, StudioTitleBarFactory>()
            .Singleton<IFactory<SceneView>, SceneViewFactory>());
    }
}
