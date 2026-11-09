using TrueMoon;
using TrueMoon.Argentis;
using TrueMoon.Tales.Studio.Presentation.Shell;

namespace TrueMoon.Tales.Studio.Presentation.Scene;

// The presentation host calls this on its UI owner and owns the returned window.
internal sealed class SceneViewFactory(SceneViewModel model, IViewStyle<SceneView> style,
    IFactory<StudioTitleBar, StudioTitleBarArguments> headers) : IFactory<SceneView>
{
    public SceneView Create() => new(model, style, headers);
    public SceneView Create<TData>(TData? data = default) => data is null ? Create()
        : throw new ArgumentException("Studio window does not accept runtime arguments.", nameof(data));
}
