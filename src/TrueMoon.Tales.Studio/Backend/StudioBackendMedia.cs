using TrueMoon.Tales.Studio.Persistence;

namespace TrueMoon.Tales.Studio.Backend;

internal sealed partial class StudioBackendService
{
    private readonly SemaphoreSlim _mediaGate = new(1, 1);
    private readonly CancellationTokenSource _mediaStopping = new();
    public Task<BackendMediaReply> MediaInfoAsync(string sessionKey, string assetId, long expectedRevision, string fingerprint, CancellationToken cancellationToken = default) =>
        ReadMediaAsync(sessionKey, assetId, -1, expectedRevision, fingerprint, cancellationToken);

    public Task<BackendMediaReply> FrameAsync(string sessionKey, string assetId, int frame, long expectedRevision, string fingerprint, CancellationToken cancellationToken = default)
    {
        Authorize(sessionKey);
        return frame < 0 ? Task.FromResult(new BackendMediaReply { ErrorCode = "invalid_frame" }) :
            ReadMediaAsync(sessionKey, assetId, frame, expectedRevision, fingerprint, cancellationToken);
    }

    private async Task<BackendMediaReply> ReadMediaAsync(string key, string assetId, int frame, long revision, string fingerprint, CancellationToken token)
    {
        Authorize(key);
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (!await _mediaGate.WaitAsync(0, token).ConfigureAwait(false)) return new() { ErrorCode = "media_busy" };
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(token, _mediaStopping.Token);
        timeout.CancelAfter(TimeSpan.FromSeconds(10));
        try
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            var document = await ObserveAsync(revision, fingerprint, timeout.Token).ConfigureAwait(false);
            if (document.SchemaVersion != 2) return new() { ErrorCode = "migration_required" };
            if (media is null) return new() { ErrorCode = "decoder_unavailable" };
            var reply = await media.ReadAsync(document.Project, assetId, frame, timeout.Token).ConfigureAwait(false);
            if (reply.ErrorCode.Length != 0) return reply;
            // A concurrent move cannot silently label a frame with a different project baseline.
            await ObserveAsync(revision, fingerprint, timeout.Token).ConfigureAwait(false);
            reply.Fingerprint = document.Fingerprint;
            return reply;
        }
        catch (ProjectRevisionConflictException) { return new() { ErrorCode = "revision_conflict" }; }
        catch (ProjectRecoveryRequiredException) { return new() { ErrorCode = "recovery_required" }; }
        catch (UnauthorizedAccessException) { return new() { ErrorCode = "access_denied" }; }
        catch (OperationCanceledException) when (!token.IsCancellationRequested) { return new() { ErrorCode = "media_timeout" }; }
        catch (ArgumentException) { return new() { ErrorCode = "invalid_request" }; }
        catch (IOException) { return new() { ErrorCode = "project_io_error" }; }
        finally { _mediaGate.Release(); }
    }
}
