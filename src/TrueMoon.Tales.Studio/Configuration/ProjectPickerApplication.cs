using TrueMoon.Alloy.Hosting;
using TrueMoon.Alloy.Runtime;
using TrueMoon.Argentis;
using TrueMoon.Dependencies;
using TrueMoon.Tales.Studio.Backend;
using TrueMoon.Tales.Studio.Presentation;
using TrueMoon.Tales.Studio.Presentation.Projects;

namespace TrueMoon.Tales.Studio.Configuration;

internal static class ProjectPickerApplication
{
    internal static async Task RunAsync(string config, CancellationToken token)
    {
        var catalog = new ProjectCatalog(config);
        ProjectEntry? previous = null;
        string? message = null;
        while (!token.IsCancellationRequested)
        {
            var selected = await SelectAsync(catalog, previous, message, token).ConfigureAwait(false);
            if (selected is null) return;
            previous = selected;
            message = await OpenSelectedAsync(catalog, selected, token).ConfigureAwait(false);
        }
    }

    private static async Task<ProjectEntry?> SelectAsync(ProjectCatalog catalog, ProjectEntry? previous,
        string? message, CancellationToken token)
    {
        ProjectEntry? selected = null;
        await using var app = App.Build(context => context.UsePresentation(resolver =>
        {
            var lifetime = resolver.Resolve<IAppLifetime>()!;
            return new ProjectPickerWindow(catalog, entry =>
            {
                if (Interlocked.CompareExchange(ref selected, entry, null) is null) lifetime.Cancel();
            }, previous, message);
        }, options => options.UseSkiaOpenGL().UseSilkWindow(window =>
        {
            window.Title = "Проекты · TrueMoon Tales Studio";
            window.Width = 1000;
            window.Height = 720;
            window.Chrome = new WindowChromeOptions { MinimumSize = new(740, 480) };
        })));
        try
        {
            await app.StartAsync(token).ConfigureAwait(false);
            await app.Services.Resolve<IAppLifetimeHandler>()!.WaitAsync(token).ConfigureAwait(false);
            return Volatile.Read(ref selected);
        }
        finally { await app.StopAsync().ConfigureAwait(false); }
    }

    internal static async Task<string?> OpenSelectedAsync(ProjectCatalog catalog, ProjectEntry selected,
        CancellationToken token, Action<IApp>? externalHost = null, string? executablePath = null)
    {
        try
        {
            var options = await catalog.PrepareAsync(selected, token).ConfigureAwait(false);
            return await EditAsync(options with { ExecutablePath = executablePath }, () => catalog.Remember(selected),
                token, externalHost).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested) { throw; }
        catch (Exception error) { return $"Не удалось открыть «{selected.Name}» — {error.Message}"; }
    }

    private static async Task<string?> EditAsync(BackendOptions options, Action remember,
        CancellationToken token, Action<IApp>? externalHost)
    {
        var opened = new TaskCompletionSource<string?>(TaskCreationOptions.RunContinuationsAsynchronously);
        await using var app = App.Build(context =>
        {
            context.UseProjectBackend(options, child: false).UseBackendScene(error => opened.TrySetResult(error));
            if (externalHost is null) context.UseStudioWindow();
        });
        try
        {
            await app.StartAsync(token).ConfigureAwait(false);
            externalHost?.Invoke(app);
            var stopped = app.Services.Resolve<IAppLifetimeHandler>()!.WaitAsync(token);
            var closed = externalHost is null ? app.Services.Resolve<HostedUiWindow>()!.Completion : stopped;
            var closing = Task.WhenAny(closed, stopped);
            using var readiness = CancellationTokenSource.CreateLinkedTokenSource(token);
            readiness.CancelAfter(TimeSpan.FromSeconds(45));
            await Task.WhenAny(opened.Task, closing).WaitAsync(readiness.Token).ConfigureAwait(false);
            if (!opened.Task.IsCompleted)
            {
                var ended = await closing.ConfigureAwait(false);
                await ended.ConfigureAwait(false);
                return null;
            }

            var failure = await opened.Task.ConfigureAwait(false);
            if (failure is not null) return failure;
            string? notice = null;
            try { remember(); }
            catch (Exception error) when (ProjectCatalog.IsDataError(error))
            { notice = $"Проект открыт. Список недавних не сохранён — {error.Message}"; }
            var finished = await closing.WaitAsync(token).ConfigureAwait(false);
            await finished.ConfigureAwait(false);
            return notice;
        }
        catch (OperationCanceledException) when (!token.IsCancellationRequested)
        { return "Истекло время ожидания редактора. Попробуйте открыть проект снова."; }
        finally { await app.StopAsync().ConfigureAwait(false); }
    }
}
