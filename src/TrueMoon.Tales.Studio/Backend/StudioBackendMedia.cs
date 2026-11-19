using TrueMoon.Tales.Studio.Persistence;
using TrueMoon.Tales.Studio.Backend.Export;

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

    public Task<BackendMediaReply> TakePairFrameAsync(string sessionKey, string sourceId, string finalTakeId, bool draft, int finalFrame,
        long expectedRevision, string fingerprint, CancellationToken cancellationToken = default) =>
        ReadMediaAsync(sessionKey, "", finalFrame, expectedRevision, fingerprint, cancellationToken, pair: (sourceId, finalTakeId, draft));

    private async Task<BackendMediaReply> ReadMediaAsync(string key, string assetId, int frame, long revision, string fingerprint,
        CancellationToken token, bool waitForAdmission = false, (string Source, string Final, bool Draft)? pair = null)
    {
        Authorize(key);
        ObjectDisposedException.ThrowIf(_disposed, this);
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(token, _mediaStopping.Token);
        timeout.CancelAfter(TimeSpan.FromSeconds(10));
        var admitted = false;
        try
        {
            if (waitForAdmission)
            { await _mediaGate.WaitAsync(timeout.Token).ConfigureAwait(false); admitted = true; }
            else
            {
                if (!await _mediaGate.WaitAsync(0, timeout.Token).ConfigureAwait(false)) return new() { ErrorCode = "media_busy" };
                admitted = true;
            }
            ObjectDisposedException.ThrowIf(_disposed, this);
            var document = await ObserveAsync(revision, fingerprint, timeout.Token).ConfigureAwait(false);
            if (document.SchemaVersion != 2) return new() { ErrorCode = "migration_required" };
            if (media is null) return new() { ErrorCode = "decoder_unavailable" };
            BackendMediaReply reply;
            if (pair is { } target)
            {
                var (sample, index) = BackendTakePairs.Frame(document.Project, target.Source, target.Final, target.Draft, frame);
                reply = await media.ReadAsync(document.Project, sample.AssetId, index, timeout.Token, sample).ConfigureAwait(false);
            }
            else if (BackendRenderedMontage.IsReview(assetId))
            {
                var (id, start) = BackendRenderedMontage.Parse(assetId);
                var (job, file) = await _exports.OpenReviewAsync(id, revision, fingerprint, timeout.Token).ConfigureAwait(false);
                await using var owned = file;
                var review = BackendRenderedMontage.Resolve(assetId, document.Project, job, start);
                reply = await media.ReadReviewedAsync(review, file.Name, frame, timeout.Token).ConfigureAwait(false);
            }
            else reply = await media.ReadAsync(document.Project, assetId, frame, timeout.Token).ConfigureAwait(false);
            if (reply.ErrorCode.Length != 0) return reply;
            // A concurrent move cannot silently label a frame with a different project baseline.
            await ObserveAsync(revision, fingerprint, timeout.Token).ConfigureAwait(false);
            reply.Fingerprint = document.Fingerprint;
            return reply;
        }
        catch (ProjectRevisionConflictException) { return new() { ErrorCode = "revision_conflict" }; }
        catch (ProjectRecoveryRequiredException) { return new() { ErrorCode = "recovery_required" }; }
        catch (UnauthorizedAccessException) { return new() { ErrorCode = "access_denied" }; }
        catch (BackendExportException error) { return new() { ErrorCode = error.Code }; }
        catch (ScenarioEditException error) { return new() { ErrorCode = error.Code }; }
        catch (FileNotFoundException) { return new() { ErrorCode = "media_not_found" }; }
        catch (InvalidDataException) { return new() { ErrorCode = "invalid_media" }; }
        catch (OperationCanceledException) when (!token.IsCancellationRequested) { return new() { ErrorCode = "media_timeout" }; }
        catch (ArgumentException) { return new() { ErrorCode = "invalid_request" }; }
        catch (IOException) { return new() { ErrorCode = "project_io_error" }; }
        finally { if (admitted) _mediaGate.Release(); }
    }
}
