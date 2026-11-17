using System.Collections.ObjectModel;
using TrueMoon.Diagnostics;
using TrueMoon.Mithril;
using TrueMoon.Mithril.Units;

namespace TrueMoon.Tales.Studio.Backend;

// The controller owns handles returned by this factory; the factory only observes its latest child.
internal sealed class BackendProcessFactory(BackendOptions options, IEventsSource<UnitsController> events)
    : IFactory<IUnitHandle, IUnitConfiguration>
{
    internal BackendProcess? Owned { get; private set; }
    public IUnitHandle Create(IUnitConfiguration arguments) => Owned = new(arguments, events, options);
}

internal sealed class BackendProcess(IUnitConfiguration configuration, IEventsSource events, BackendOptions options)
    : ChildProcessUnitHandle(configuration, events)
{
    protected override string GetProcessFilePath() => options.ExecutablePath ?? base.GetProcessFilePath();
    internal bool IsAlive
    {
        get
        {
            try { return Process is { HasExited: false }; }
            catch (InvalidOperationException) { return false; }
        }
    }

    internal bool IsServing(int processId)
    {
        try { return Process is { HasExited: false } process && process.Id == processId; }
        catch (InvalidOperationException) { return false; }
    }

    protected override void ProcessLaunchArguments(Collection<string> arguments)
    {
        arguments.Add("--backend");
        arguments.Add(options.ProjectDirectory);
        arguments.Add(options.ConfigPath);
        arguments.Add(options.Endpoint);
        arguments.Add(options.SessionKey);
        if (options.WriteEnabled) arguments.Add("--writer");
    }
}
