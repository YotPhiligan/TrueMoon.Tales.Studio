using System.Text.Json;
using TrueMoon.Tales.Studio.Persistence;

namespace TrueMoon.Tales.Studio.Backend;

internal sealed partial class StudioBackendService
{
    /// <inheritdoc />
    public async Task<BackendExportReply> TakeProxyAsync(string sessionKey, string operation, string sourceId, string takeId,
        long expectedRevision, string fingerprint, CancellationToken cancellationToken = default)
    {
        Authorize(sessionKey);
        if (proxies is null) return Error("proxy_cache_required");
        using var scope = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, _mediaStopping.Token);
        scope.CancelAfter(TimeSpan.FromSeconds(10));
        var token = scope.Token;
        var admitted = false;
        try
        {
            if (operation != "status")
            {
                await _gate.WaitAsync(token).ConfigureAwait(false);
                admitted = true;
                RequireWriter();
            }
            ObjectDisposedException.ThrowIf(_disposed, this);
            var current = await ExportBaselineAsync(expectedRevision, fingerprint, token).ConfigureAwait(false);
            var asset = current.Project.Sources.FirstOrDefault(source => source.Id == sourceId)?
                .Takes.FirstOrDefault(take => take.Id == takeId)?.VideoAssetId;
            if (asset is not { Length: > 0 and <= 160 } || BackendScenarioEdit.TryReference(current.Project, asset) is not { } reference)
                return Error("media_not_found");
            var project = current.Project.Id;
            ProxyStatus result;
            if (operation == "cancel") result = proxies.Cancel(asset);
            else if (operation == "original")
            {
                proxies.SelectOriginal(project, asset);
                result = new(asset, "original");
            }
            else
            {
                var path = new BackendMediaResolver(options).Resolve(reference);
                result = operation switch
                {
                    "status" => await proxies.StatusAsync(project, asset, path, token).ConfigureAwait(false),
                    "prepare_4k" or "prepare_manual" => proxies.Start(project, asset, path, reference, operation == "prepare_manual", PublishAsync),
                    _ => throw new ScenarioEditException("invalid_proxy_request")
                };
            }
            return new() { JobJson = JsonSerializer.Serialize(result, BackendAdaptation.Json) };
        }
        catch (WriterRequiredException error) { return Error(error.Message); }
        catch (ScenarioEditException error) { return Error(error.Code); }
        catch (ProjectRevisionConflictException) { return Error("revision_conflict"); }
        catch (ProjectRecoveryRequiredException) { return Error("recovery_required"); }
        catch (ProjectMigrationRequiredException) { return Error("migration_required"); }
        catch (UnauthorizedAccessException) { return Error("access_denied"); }
        catch (Exception error) when (error is FileNotFoundException or DirectoryNotFoundException) { return Error("media_not_found"); }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested) { return Error("proxy_timeout"); }
        catch (Exception error) when (error is IOException or InvalidDataException or JsonException or ArgumentException or InvalidOperationException)
        { return Error("proxy_failed"); }
        finally { if (admitted) _gate.Release(); }

        async Task PublishAsync(Action publish, CancellationToken work)
        {
            using var lifetime = CancellationTokenSource.CreateLinkedTokenSource(work, _mediaStopping.Token);
            await _gate.WaitAsync(lifetime.Token).ConfigureAwait(false);
            try
            {
                ObjectDisposedException.ThrowIf(_disposed, this);
                RequireWriter();
                await ExportBaselineAsync(expectedRevision, fingerprint, lifetime.Token).ConfigureAwait(false);
                lifetime.Token.ThrowIfCancellationRequested();
                publish();
            }
            finally { _gate.Release(); }
        }

        static BackendExportReply Error(string code) => new() { ErrorCode = code };
    }
}
