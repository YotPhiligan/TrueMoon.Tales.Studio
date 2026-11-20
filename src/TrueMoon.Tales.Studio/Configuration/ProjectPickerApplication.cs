using TrueMoon.Alloy.Hosting;
using TrueMoon.Alloy.Runtime;
using TrueMoon.Argentis;
using TrueMoon.Dependencies;
using TrueMoon.Tales.Studio.Backend;
using TrueMoon.Tales.Studio.Presentation;
using TrueMoon.Tales.Studio.Presentation.Project;
using TrueMoon.Tales.Studio.Presentation.Projects;

namespace TrueMoon.Tales.Studio.Configuration;

internal static class ProjectPickerApplication
{
    internal static async Task RunAsync(string config, bool openEmptyWorkspace, CancellationToken token)
    {
        var catalog = new ProjectCatalog(config);
        ProjectEntry? previous = null;
        string? message = null;
        if (openEmptyWorkspace && !token.IsCancellationRequested)
        {
            (previous, message) = await PrepareEmptyWorkspaceAsync(catalog, token).ConfigureAwait(false);
            if (previous is not null)
                message = await OpenSelectedAsync(catalog, previous, token).ConfigureAwait(false);
        }
        while (!token.IsCancellationRequested)
        {
            var selected = await SelectAsync(catalog, previous, message, token).ConfigureAwait(false);
            if (selected is null) return;
            previous = selected;
            message = await OpenSelectedAsync(catalog, selected, token).ConfigureAwait(false);
        }
    }

    internal static async Task<(ProjectEntry? Project, string? Notice)> PrepareEmptyWorkspaceAsync(ProjectCatalog catalog,
        CancellationToken token)
    {
        try
        {
            var listing = await catalog.ReadAsync(token).ConfigureAwait(false);
            if (listing is not { Items.Length: 0, Notices.Length: 0 }) return (null, null);
            var project = await catalog.CreateAsync(new("Без названия", 1280, 736, 24), token).ConfigureAwait(false);
            return (project, null);
        }
        catch (Exception error) when (ProjectCatalog.IsDataError(error))
        { return (null, $"Не удалось начать новый проект — {error.Message}"); }
    }

    private static async Task<ProjectEntry?> SelectAsync(ProjectCatalog catalog, ProjectEntry? previous,
        string? message, CancellationToken token)
    {
        ProjectEntry? selected = null;
        await using var app = BuildPicker(catalog,
            entry => Interlocked.CompareExchange(ref selected, entry, null) is null, previous, message);
        try
        {
            await app.StartAsync(token).ConfigureAwait(false);
            await app.Services.Resolve<IAppLifetimeHandler>()!.WaitAsync(token).ConfigureAwait(false);
            return Volatile.Read(ref selected);
        }
        finally { await app.StopAsync().ConfigureAwait(false); }
    }

    internal static IApp BuildPicker(ProjectCatalog catalog, Func<ProjectEntry, bool> trySelect,
        ProjectEntry? previous = null, string? message = null) => App.Build(context => context.UsePresentation(resolver =>
        {
            var lifetime = resolver.Resolve<IAppLifetime>()!;
            return new ProjectPickerWindow(catalog, entry =>
            {
                if (trySelect(entry)) lifetime.Cancel();
            }, previous, message);
        }, options => options.UseSkiaOpenGL().UseSilkWindow(ConfigurePickerWindow)));

    internal static void ConfigurePickerWindow(SilkUiWindowOptions window)
    {
        window.Title = "Проекты · TrueMoon Tales Studio";
        window.Width = 1000;
        window.Height = 720;
        window.Appearance = new WindowAppearance { Decorated = false };
        window.Chrome = new WindowChromeOptions { NativeDrag = true, MinimumSize = new(740, 480) };
    }

    internal static async Task<string?> OpenSelectedAsync(ProjectCatalog catalog, ProjectEntry selected,
        CancellationToken token, Action<IApp>? externalHost = null, string? executablePath = null)
    {
        try
        {
            var options = await catalog.PrepareAsync(selected, token).ConfigureAwait(false);
            return await OpenProjectAsync(options with { ExecutablePath = executablePath }, () => catalog.Remember(selected),
                token, externalHost).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested) { throw; }
        catch (Exception error) { return $"Не удалось открыть «{selected.Name}» — {error.Message}"; }
    }

    private static async Task<string?> OpenProjectAsync(BackendOptions options, Action remember,
        CancellationToken token, Action<IApp>? externalHost)
    {
        var opened = new TaskCompletionSource<string?>(TaskCreationOptions.RunContinuationsAsynchronously);
        await using var app = App.Build(context =>
        {
            context.UseProjectBackend(options, child: false);
            if (options.WriteEnabled)
            {
                context.UseBackendScene(error => opened.TrySetResult(error));
                if (externalHost is null) context.UseStudioWindow();
            }
            else context.UseProjectWindow(error => opened.TrySetResult(error), nativeWindow: externalHost is null);
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
        { return "Истекло время открытия проекта. Попробуйте открыть его снова."; }
        finally { await app.StopAsync().ConfigureAwait(false); }
    }
}
