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
        await using var preferences = StudioUiPreferences.OpenDefault();
        await using var app = BuildPicker(catalog,
            entry => Interlocked.CompareExchange(ref selected, entry, null) is null, previous, message, preferences);
        try
        {
            await app.StartAsync(token).ConfigureAwait(false);
            await app.Services.Resolve<IAppLifetimeHandler>()!.WaitAsync(token).ConfigureAwait(false);
            return Volatile.Read(ref selected);
        }
        finally { await app.StopAsync().ConfigureAwait(false); }
    }

    internal static IApp BuildPicker(ProjectCatalog catalog, Func<ProjectEntry, bool> trySelect,
        ProjectEntry? previous = null, string? message = null, StudioUiPreferences? preferences = null) =>
        App.Build(context => context.UsePresentation(resolver =>
        {
            var lifetime = resolver.Resolve<IAppLifetime>()!;
            var picker = new ProjectPickerWindow(catalog, entry =>
            {
                if (trySelect(entry)) lifetime.Cancel();
            }, previous, message ?? preferences?.Notice);
            preferences?.Attach(picker, StudioUiWindow.Projects);
            return picker;
        }, options => options.UseSkiaOpenGL().UseSilkWindow(window =>
        {
            ConfigurePickerWindow(window);
            if (preferences is not null)
                window.ConfigureDefaultTitleBar = (_, content, _) => StudioUiPreferences.ApplyTheme(content, preferences.Current.Theme);
        })));

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
        var current = selected;
        var migrated = false;
        TaskCompletionSource<ProjectEntry>? handoff = null;
        try
        {
            while (true)
            {
                var entry = current;
                var options = await catalog.PrepareAsync(entry, token).ConfigureAwait(false);
                handoff = entry.Availability == ProjectAvailability.MigrationRequired
                    ? new(TaskCreationOptions.RunContinuationsAsynchronously) : null;
                ProjectFormatUpgrade? upgrade = null;
                if (handoff is { } destination)
                {
                    upgrade = new(entry.Directory!,
                        (snapshot, cancellation) => UpgradeAsync(catalog, entry, snapshot, cancellation, executablePath),
                        updated => destination.TrySetResult(updated));
                }
                else if (entry.OpensReadOnly && entry.Schema == 1)
                    upgrade = new(entry.Directory!, null, _ => { });

                var notice = await OpenProjectAsync(options with { ExecutablePath = executablePath }, () => catalog.Remember(entry),
                    token, externalHost, upgrade, handoff?.Task).ConfigureAwait(false);
                if (handoff?.Task.IsCompletedSuccessfully != true)
                    return migrated && notice is not null ? EditorRetry(notice) : notice;

                current = handoff.Task.Result;
                migrated = true;
                if (current is not { Schema: 2, CanOpen: true, OpensReadOnly: false })
                    return EditorRetry(current.Details);
            }
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested) { throw; }
        catch (Exception error)
        {
            return migrated || handoff?.Task.IsCompletedSuccessfully == true ? EditorRetry(error.Message) :
                $"Не удалось открыть «{selected.Name}» — {error.Message}";
        }

        static string EditorRetry(string message) => $"Формат обновлён. Откройте редактор из списка проектов. {message}";
    }

    private static Task<ProjectEntry> UpgradeAsync(ProjectCatalog catalog, ProjectEntry entry, OpenedBackendProject observed,
        CancellationToken token, string? executablePath)
    {
        if (observed.SchemaVersion != 1 || observed.Project.Id != entry.Id)
            throw new InvalidDataException("Открытый проект изменился. Выберите проект заново.");
        return catalog.MaintainAsync(entry with { Revision = observed.Project.Revision, Fingerprint = observed.Fingerprint },
            token, executablePath);
    }

    private static async Task<string?> OpenProjectAsync(BackendOptions options, Action remember,
        CancellationToken token, Action<IApp>? externalHost, ProjectFormatUpgrade? upgrade, Task<ProjectEntry>? upgraded)
    {
        var opened = new TaskCompletionSource<string?>(TaskCreationOptions.RunContinuationsAsynchronously);
        await using var preferences = externalHost is null ? StudioUiPreferences.OpenDefault() : null;
        await using var app = App.Build(context =>
        {
            context.UseProjectBackend(options, child: false);
            if (options.WriteEnabled)
            {
                context.UseBackendScene(error => opened.TrySetResult(error), preferences);
                if (externalHost is null) context.UseStudioWindow(preferences);
            }
            else context.UseProjectWindow(error => opened.TrySetResult(error), nativeWindow: externalHost is null,
                preferences: preferences, upgrade: upgrade);
        });
        try
        {
            await app.StartAsync(token).ConfigureAwait(false);
            externalHost?.Invoke(app);
            var stopped = app.Services.Resolve<IAppLifetimeHandler>()!.WaitAsync(token);
            var closed = externalHost is null ? app.Services.Resolve<HostedUiWindow>()!.Completion : stopped;
            var closing = upgraded is null ? Task.WhenAny(closed, stopped) : Task.WhenAny(closed, stopped, upgraded);
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
