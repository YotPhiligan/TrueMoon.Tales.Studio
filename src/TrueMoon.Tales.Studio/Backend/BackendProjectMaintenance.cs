using TrueMoon.Dependencies;

namespace TrueMoon.Tales.Studio.Backend;

internal static class BackendProjectMaintenance
{
    internal static async Task<OpenedBackendProject> ReadOrApplyAsync(BackendConnection backend, BackendOptions options, CancellationToken token)
    {
        if (options.Action == "recover") return await backend.RecoverAsync(token).ConfigureAwait(false);
        var opened = await backend.OpenAsync(token).ConfigureAwait(false);
        if (options.Action != "migrate") return opened;
        if (options.InitialFingerprint is { } fingerprint &&
            !string.Equals(opened.Fingerprint, fingerprint, StringComparison.OrdinalIgnoreCase))
            throw new BackendCommandException("revision_conflict");
        opened = await backend.AcquireWriterAsync(opened, token).ConfigureAwait(false);
        return await backend.MigrateAsync(opened, token).ConfigureAwait(false);
    }

    internal static async Task<OpenedBackendProject> ApplyAsync(BackendOptions options, CancellationToken token)
    {
        await using var app = App.Build(context => context.UseProjectBackend(options, child: false));
        try
        {
            await app.StartAsync(token).ConfigureAwait(false);
            return await ReadOrApplyAsync(app.Services.Resolve<BackendConnection>()!, options, token).ConfigureAwait(false);
        }
        finally { await app.StopAsync().ConfigureAwait(false); }
    }
}
