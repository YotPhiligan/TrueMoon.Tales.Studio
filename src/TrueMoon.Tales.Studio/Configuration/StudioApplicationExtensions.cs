using System.Text.Json;
using TrueMoon.Dependencies;
using TrueMoon.Mithril;
using TrueMoon.Tales.Studio.Backend;
using TrueMoon.Tales.Studio.Backend.Http;
using TrueMoon.Tales.Studio.Infrastructure;
using TrueMoon.Tales.Studio.Presentation;
using TrueMoon.Tales.Studio.Presentation.Project;

namespace TrueMoon.Tales.Studio.Configuration;

internal static class StudioApplicationExtensions
{
    internal static IAppConfigurationContext UseStudioUi(this IAppConfigurationContext context, string[] args) =>
        StudioOptions.TryParse(args, out _) ? context.UseStudioServices().UseStudioWindow() : context;

    public static async Task<int> RunStudioAsync(this IAppBuilder builder, string[] args, CancellationToken token = default)
    {
        // Preserve the existing configurable HTTP runner, including exception propagation and ownership.
        if (StudioOptions.TryParse(args, out var httpOptions) && Environment.GetEnvironmentVariable("-u") is null &&
            Environment.GetEnvironmentVariable("-p") is null && Environment.GetEnvironmentVariable(UnitParentProcessEventsHandler.ControlPipeKey) is null)
        {
            await builder.RunAsync(context => context.Services(services => services.Instance(httpOptions)), token);
            return 0;
        }
        try
        {
            var launch = UnitBootstrap.Parse([], new Dictionary<string, string?>
            {
                ["-u"] = Environment.GetEnvironmentVariable("-u"),
                ["-p"] = Environment.GetEnvironmentVariable("-p"),
                [UnitParentProcessEventsHandler.ControlPipeKey] = Environment.GetEnvironmentVariable(UnitParentProcessEventsHandler.ControlPipeKey),
                ["TRUEMOON_MITHRIL__DISABLED"] = Environment.GetEnvironmentVariable("TRUEMOON_MITHRIL__DISABLED")
            });
            if (args is ["--projects", "--config", var config] && launch.UnitId is null &&
                launch.ParentProcessId is null && launch.ControlPipe is null && !launch.UnitsDisabled)
            {
                await ProjectPickerApplication.RunAsync(config, token).ConfigureAwait(false);
                return 0;
            }
            if (BackendOptions.TryParse(args, launch, out var backendOptions, out var child, out var inspect))
            {
                builder.Setup(context => context.UseProjectBackend(backendOptions!, child));
                if (!child && !inspect) builder.Setup(context =>
                {
                    if (backendOptions!.Action == "edit" || backendOptions.Action == "serve" && backendOptions.HttpWithUi) context.UseBackendScene().UseStudioWindow();
                    else if (backendOptions.Action != "serve") context.UseProjectWindow();
                });
                await using var app = builder.Build();
                await using var http = !child && backendOptions!.Action == "serve"
                    ? new BackendHttpServer(app.Services.Resolve<BackendConnection>()!, backendOptions.HttpPort) : null;
                try
                {
                    await app.StartAsync(token).ConfigureAwait(false);
                    if (http is not null)
                    {
                        await http.StartAsync(token).ConfigureAwait(false);
                        Console.WriteLine(JsonSerializer.Serialize(http.Ready));
                    }
                    if (inspect)
                    {
                        var connection = app.Services.Resolve<BackendConnection>()!;
                        var opened = await BackendProjectMaintenance.ReadOrApplyAsync(connection, backendOptions!, token).ConfigureAwait(false);
                        Console.WriteLine(JsonSerializer.Serialize(new
                        {
                            opened.Identity, opened.SchemaVersion, opened.Fingerprint, opened.Project
                        }));
                    }
                    else await app.Services.Resolve<IAppLifetimeHandler>()!.WaitAsync(token).ConfigureAwait(false);
                }
                finally
                {
                    try { if (http is not null) await http.DisposeAsync().ConfigureAwait(false); }
                    finally { await app.StopAsync().ConfigureAwait(false); }
                }
                return 0;
            }
            Console.Error.WriteLine(StudioOptions.Usage + Environment.NewLine +
                "Список проектов: TrueMoon.Tales.Studio --projects --config <Studio.config.json>" + Environment.NewLine +
                "Чтение через backend: TrueMoon.Tales.Studio --project <каталог> --config <Studio.config.json>" + Environment.NewLine +
                "Без окна: TrueMoon.Tales.Studio --inspect-project <каталог> --config <Studio.config.json>" + Environment.NewLine +
                "Запись: --edit-project, явные --migrate-project / --recover-project <каталог> --config <Studio.config.json>" + Environment.NewLine +
                "HTTP/CLI: --serve-project <каталог> --config <Studio.config.json> [--http-port <0..65535>] [--with-ui]");
            return 2;
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested) { return 0; }
        catch (Exception error)
        {
            Console.Error.WriteLine($"Studio: {error.Message}");
            return 1;
        }
    }
}
