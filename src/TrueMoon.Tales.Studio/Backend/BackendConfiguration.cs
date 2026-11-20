using TrueMoon.Enerit;
using TrueMoon.Enerit.IO.Pipes;
using TrueMoon.Diagnostics;
using TrueMoon.Mithril;
using TrueMoon.Mithril.Units;
using TrueMoon.Tales.Studio.Persistence;

namespace TrueMoon.Tales.Studio.Backend;

internal static class BackendConfiguration
{
    internal static IAppConfigurationContext UseProjectBackend(this IAppConfigurationContext context, BackendOptions options, bool child)
    {
        var storage = BackendProjectAccess.Read(options);
        context.Services(r => r.Instance(options));
        context.UseDiagnostics(diagnostics => diagnostics.OnEvent(message =>
        {
            if (!child && message.Payload is UnitProcessDiagnostic diagnostic && diagnostic.Stage is
                UnitProcessStage.Started or UnitProcessStage.Stopped or UnitProcessStage.ProcessReleased)
                Console.Error.WriteLine($"Studio backend: {diagnostic.Stage} pid={diagnostic.ProcessId}");
        }));
        context.PipesServiceTransport(_ => options.Endpoint, new PipesTransportOptions
        {
            RequestTimeout = TimeSpan.FromSeconds(12), MaxPendingRequests = 4, MaxQueuedRequests = 4,
            MaxConcurrentRequests = 4, MaxBufferedRequestBytes = 64 * 1024, MaxConnections = 2
        });
        context.AddUnit(unit => unit
            .Services(r => r.Instance(storage).Singleton<ProjectStore>(resolver => new(resolver.Resolve<ProjectStoreOptions>()))
                .Singleton<IFactory<BackendMediaResolver>, BackendMediaResolverFactory>()
                .Singleton<BackendMediaDecoder>(_ => BackendMediaReader.Decoder(options))
                .Singleton<BackendProxyStore>(resolver => new(options, resolver.Resolve<BackendMediaDecoder>()!))
                .Singleton<BackendMediaReader>()
                .Singleton<IStudioBackend, StudioBackendService>())
            .ListenInvocationService<IStudioBackend, StudioBackendService>(), configuration =>
        {
            configuration.Name = "studio-backend";
            configuration.HostingPolicy = UnitHostingPolicy.ChildProcess;
            configuration.StartupPolicy = UnitStartupPolicy.Immediate;
            configuration.LifetimePolicy = UnitLifetimePolicy.App;
            configuration.RestartPolicy = UnitRestartPolicy.Never;
            configuration.TerminationDelay = 3000;
            configuration.IsControlAppLifetime = false;
        });
        if (!child)
        {
            context.Services(r => r.Singleton<BackendProcessFactory>()
                .Singleton<IFactory<IUnitHandle, IUnitConfiguration>>(resolver => resolver.Resolve<BackendProcessFactory>())
                .Singleton<BackendConnection>()
                .Singleton<IBackendProjectReader>(resolver => resolver.Resolve<BackendConnection>()));
            context.UseInvocationService<IStudioBackend>();
        }
        return context;
    }
}
