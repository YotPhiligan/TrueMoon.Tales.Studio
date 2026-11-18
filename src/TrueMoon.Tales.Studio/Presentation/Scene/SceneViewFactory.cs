using TrueMoon;
using TrueMoon.Argentis;
using TrueMoon.Tales.Studio.Presentation.Shell;
using TrueMoon.Tales.Studio.Presentation.Scenario;

namespace TrueMoon.Tales.Studio.Presentation.Scene;

// The presentation host calls this on its UI owner and owns the returned window.
internal sealed class SceneViewFactory(SceneViewModel model, IViewStyle<SceneView> style,
    IFactory<StudioTitleBar, StudioTitleBarArguments> headers, Func<SceneExport>? createExport = null,
    Action<string?>? initialOpen = null, Func<ScenarioWorkspace>? createScenario = null) : IFactory<SceneView>
{
    public SceneView Create() => new(model, style, headers, createExport?.Invoke(), initialOpen, createScenario?.Invoke());
    public SceneView Create<TData>(TData? data = default) => data is null ? Create()
        : throw new ArgumentException("Studio window does not accept runtime arguments.", nameof(data));
}
