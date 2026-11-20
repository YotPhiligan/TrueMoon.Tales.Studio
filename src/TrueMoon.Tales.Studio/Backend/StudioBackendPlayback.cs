using System.ComponentModel;
using System.Text.Json;
using TrueMoon.Tales.Studio.Backend.Export;
using TrueMoon.Tales.Studio.Persistence;

namespace TrueMoon.Tales.Studio.Backend;

internal sealed partial class StudioBackendService
{
    private readonly PlaybackSlot _playback = new();
    private readonly PlaybackSlot _reviewAudio = new();

    private sealed class PlaybackSlot
    {
        internal readonly SemaphoreSlim Gate = new(1, 1);
        // Close may interrupt a read before it acquires the gate.
        internal volatile BackendPlaybackSession? Session;

        internal async Task CloseAsync()
        {
            try
            {
                if (Session is { } session) await session.DisposeAsync().ConfigureAwait(false);
            }
            finally { Session = null; }
        }
    }

    private PlaybackSlot SlotForSession(string id) => _reviewAudio.Session?.Id == id ? _reviewAudio : _playback;

    public Task<BackendPlaybackReply> OpenPlaybackAsync(string sessionKey, string clipId, string kind, int localFrame,
        long expectedRevision, string fingerprint, CancellationToken cancellationToken = default)
    {
        var slot = kind == BackendPlaybackSession.ReviewAudio ? _reviewAudio : _playback;
        return PlaybackOperationAsync(sessionKey, slot, async token =>
        {
            if (slot.Session is { Expired: true }) await slot.CloseAsync().ConfigureAwait(false);
            if (slot.Session is not null) return new() { ErrorCode = "media_busy" };
            if (slot == _reviewAudio && !BackendRenderedMontage.IsReview(clipId)) return new() { ErrorCode = "invalid_request" };
            var baseline = await ObserveAsync(expectedRevision, fingerprint, token).ConfigureAwait(false);
            if (baseline.SchemaVersion != 2) return new() { ErrorCode = "migration_required" };
            if (media is null) return new() { ErrorCode = "decoder_unavailable" };
            var opened = BackendRenderedMontage.IsReview(clipId)
                ? await OpenReviewedPlaybackAsync(baseline, clipId, kind, localFrame, token).ConfigureAwait(false)
                : await media.OpenPlaybackAsync(baseline.Project, clipId, kind, localFrame, fingerprint, token, _mediaStopping.Token).ConfigureAwait(false);
            slot.Session = opened;
            await ObserveAsync(expectedRevision, fingerprint, token).ConfigureAwait(false);
            return opened.Metadata();
        }, cancellationToken);
    }

    private async Task<BackendPlaybackSession> OpenReviewedPlaybackAsync(ProjectFileDocument baseline, string key, string kind, int localFrame,
        CancellationToken token)
    {
        var (id, start) = BackendRenderedMontage.Parse(key);
        var (job, file) = await _exports.OpenReviewAsync(id, baseline.Project.Revision, baseline.Fingerprint, token).ConfigureAwait(false);
        try
        {
            var review = BackendRenderedMontage.Resolve(key, baseline.Project, job, start);
            return await media!.OpenReviewedAsync(review, file, kind, localFrame, token, _mediaStopping.Token).ConfigureAwait(false);
        }
        catch { await file.DisposeAsync().ConfigureAwait(false); throw; }
    }

    public Task<BackendPlaybackReply> ReadPlaybackAsync(string sessionKey, string sessionId, CancellationToken cancellationToken = default)
    {
        var slot = SlotForSession(sessionId);
        return PlaybackOperationAsync(sessionKey, slot, async token =>
        {
            var session = slot.Session;
            if (session is null || session.Id != sessionId) return new() { ErrorCode = "playback_not_found" };
            await ObserveAsync(session.Revision, session.Fingerprint, token).ConfigureAwait(false);
            session.ValidatePaths();
            var reply = await session.ReadAsync(token).ConfigureAwait(false);
            await ObserveAsync(session.Revision, session.Fingerprint, token).ConfigureAwait(false);
            session.ValidatePaths();
            return reply;
        }, cancellationToken);
    }

    public async Task<BackendPlaybackReply> ClosePlaybackAsync(string sessionKey, string sessionId, CancellationToken cancellationToken = default)
    {
        Authorize(sessionKey);
        var slot = SlotForSession(sessionId);
        // Interrupt a blocked read before waiting for its serialization gate.
        if (slot.Session is not { } pending || pending.Id != sessionId) return new() { SessionId = sessionId, Completed = true };
        pending.Cancel();
        await slot.Gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (slot.Session?.Id == sessionId) await slot.CloseAsync().ConfigureAwait(false);
            return new() { SessionId = sessionId, Completed = true };
        }
        finally { slot.Gate.Release(); }
    }

    private async Task<BackendPlaybackReply> PlaybackOperationAsync(string key, PlaybackSlot slot,
        Func<CancellationToken, Task<BackendPlaybackReply>> operation, CancellationToken token)
    {
        Authorize(key);
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (!await slot.Gate.WaitAsync(0, token).ConfigureAwait(false)) return new() { ErrorCode = "media_busy" };
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(token, _mediaStopping.Token);
        deadline.CancelAfter(TimeSpan.FromSeconds(10));
        try { ObjectDisposedException.ThrowIf(_disposed, this); return await operation(deadline.Token).ConfigureAwait(false); }
        catch (Exception error) when (error is ProjectRevisionConflictException or ProjectRecoveryRequiredException or UnauthorizedAccessException or
            OperationCanceledException or ArgumentException or IOException or InvalidDataException or JsonException or InvalidOperationException or Win32Exception or NotSupportedException or OverflowException)
        {
            await slot.CloseAsync().ConfigureAwait(false);
            if (error is OperationCanceledException && token.IsCancellationRequested) throw;
            return new() { ErrorCode = error switch
            {
                BackendExportException export => export.Code,
                ProjectRevisionConflictException => "revision_conflict", ProjectRecoveryRequiredException => "recovery_required",
                UnauthorizedAccessException => "access_denied", OperationCanceledException => "media_timeout",
                FileNotFoundException or DirectoryNotFoundException => "media_not_found", Win32Exception => "decoder_unavailable",
                ArgumentException => "invalid_request", InvalidDataException or JsonException or InvalidOperationException or NotSupportedException or OverflowException => "invalid_media",
                _ => "media_io_error"
            } };
        }
        finally { slot.Gate.Release(); }
    }

    private static void StopPlayback(PlaybackSlot slot)
    {
        slot.Gate.Wait();
        try { slot.CloseAsync().GetAwaiter().GetResult(); }
        finally { slot.Gate.Release(); }
    }
}
