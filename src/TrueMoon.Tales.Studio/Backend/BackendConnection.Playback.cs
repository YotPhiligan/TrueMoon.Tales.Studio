using System.Security.Cryptography;
using TrueMoon.Tales.Studio.Editing;
using TrueMoon.Tales.Studio.Models;

namespace TrueMoon.Tales.Studio.Backend;

internal interface IBackendPlaybackClient
{
    Task<BackendPlaybackReply> OpenPlaybackAsync(SceneSnapshot scene, string kind, int localFrame, CancellationToken token);
    Task<BackendPlaybackReply> ReadPlaybackAsync(BackendPlaybackReply opened, CancellationToken token);
    Task ClosePlaybackAsync(string sessionId, CancellationToken token);
}

internal sealed partial class BackendConnection : IBackendPlaybackClient
{
    public async Task<BackendPlaybackReply> OpenPlaybackAsync(SceneSnapshot scene, string kind, int localFrame, CancellationToken token)
    {
        ValidateScene(scene, kind, localFrame);
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(12));
        await PlaybackIdentityAsync(deadline.Token).ConfigureAwait(false);
        token.ThrowIfCancellationRequested();
        // A lost Open reply has no resource ID to close: the backend's 20-second idle lease
        // bounds that ambiguity. Do not replay Open or guess/close another resource.
        var reply = await TransportAsync(() => service.OpenPlaybackAsync(options.SessionKey, scene.Clip.Id,
            kind, localFrame, scene.Revision, scene.Fingerprint!, deadline.Token)).ConfigureAwait(false);
        try { ValidatePlaybackOpen(reply, scene, kind, localFrame); }
        catch
        {
            // A malformed successful envelope may still represent a live decoder. Release only its capability.
            if (reply is not null && ValidPlaybackId(reply.SessionId))
            {
                try { await ClosePlaybackAsync(reply.SessionId, CancellationToken.None).ConfigureAwait(false); }
                catch (Exception error) when (error is IOException or OperationCanceledException) { }
            }
            throw;
        }
        return reply;
    }

    public async Task<BackendPlaybackReply> ReadPlaybackAsync(BackendPlaybackReply opened, CancellationToken token)
    {
        if (!ValidPlaybackId(opened.SessionId)) throw new InvalidDataException("Invalid playback capability.");
        token.ThrowIfCancellationRequested();
        // Keep caller cancellation out of writes on the shared pipe. The owner closes the
        // identified session even when the caller stops waiting for its samples or frames.
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(opened.Kind == "audio" ? 50 : 12));
        await PlaybackIdentityAsync(deadline.Token).ConfigureAwait(false);
        token.ThrowIfCancellationRequested();
        var reply = await TransportAsync(() => service.ReadPlaybackAsync(options.SessionKey, opened.SessionId, deadline.Token)).ConfigureAwait(false);
        token.ThrowIfCancellationRequested();
        ValidatePlaybackRead(reply, opened);
        return reply;
    }

    public async Task ClosePlaybackAsync(string sessionId, CancellationToken token)
    {
        if (!ValidPlaybackId(sessionId)) throw new InvalidDataException("Invalid playback capability.");
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(token);
        deadline.CancelAfter(TimeSpan.FromSeconds(8));
        // Open already authenticated this owned pipe and launch-scoped resource. Close must
        // reach the server's interrupt path without waiting for an extra readiness round trip.
        var reply = await TransportAsync(() => service.ClosePlaybackAsync(options.SessionKey, sessionId, deadline.Token)).ConfigureAwait(false);
        CheckPlaybackError(reply);
        if (reply.SessionId != sessionId || !reply.Completed || reply.Data is null || reply.Data.Length != 0 || reply.DataSha256 != "")
            throw new InvalidDataException("Invalid playback close acknowledgement.");
    }

    private async Task PlaybackIdentityAsync(CancellationToken token)
    {
        var identity = await TransportAsync(() => service.HelloAsync(options.SessionKey, token)).ConfigureAwait(false);
        ValidateIdentity(identity, processes.Owned?.IsServing(identity.ProcessId) == true);
    }

    internal static void ValidateScene(SceneSnapshot scene, string kind, int localFrame)
    {
        if (kind is not ("audio" or "video") || localFrame < 0 || localFrame >= scene.Clip.DurationFrames ||
            kind == "audio" && localFrame != 0 || !double.IsFinite(scene.Fps) || scene.Fps is <= 0 or > 240 ||
            scene.Clip.DurationFrames / scene.Fps is <= 0 or > 600 || scene.Clip.StartFrame < 0 ||
            (long)scene.Clip.StartFrame + scene.Clip.DurationFrames > int.MaxValue || scene.Revision < 0 ||
            string.IsNullOrEmpty(scene.Clip.Id) || scene.Clip.Id.Length > 160 || scene.Fingerprint is not { Length: 64 } || !scene.Fingerprint.All(Uri.IsHexDigit))
            throw new InvalidDataException("Invalid playback baseline or clip extent.");
    }

    internal static bool ValidPlaybackId(string? value) => value is { Length: 32 } && value.All(Uri.IsHexDigit);

    internal static void ValidatePlaybackOpen(BackendPlaybackReply reply, SceneSnapshot scene, string kind, int localFrame)
    {
        CheckPlaybackError(reply);
        var samples = AudioTimeline.Sample(scene.Clip.StartFrame + scene.Clip.DurationFrames, scene.Fps) - AudioTimeline.Sample(scene.Clip.StartFrame, scene.Fps);
        if (!ValidPlaybackId(reply.SessionId) || reply.Kind != kind || reply.ClipId != scene.Clip.Id || reply.Revision != scene.Revision ||
            !string.Equals(reply.Fingerprint, scene.Fingerprint, StringComparison.OrdinalIgnoreCase) || reply.Fps != scene.Fps ||
            !double.IsFinite(reply.SourceFps) || reply.SourceFps is <= 0 or > 240 || reply.DurationFrames != scene.Clip.DurationFrames ||
            reply.StartFrame != localFrame || reply.TotalSamples != samples || reply.Completed ||
            reply.LocalFrame != -1 || reply.NativeFrame != -1 || reply.SampleOffset != 0 ||
            reply.Data is null || reply.Data.Length != 0 || reply.DataSha256 != "")
            throw new InvalidDataException("Invalid playback open envelope.");
    }

    internal static void ValidatePlaybackRead(BackendPlaybackReply reply, BackendPlaybackReply opened)
    {
        CheckPlaybackError(reply);
        if (reply.SessionId != opened.SessionId || reply.Kind != opened.Kind || reply.ClipId != opened.ClipId || reply.Revision != opened.Revision ||
            !string.Equals(reply.Fingerprint, opened.Fingerprint, StringComparison.OrdinalIgnoreCase) || reply.Fps != opened.Fps ||
            reply.SourceFps != opened.SourceFps || reply.DurationFrames != opened.DurationFrames || reply.StartFrame != opened.StartFrame ||
            reply.TotalSamples != opened.TotalSamples || reply.Data is null || reply.DataSha256 is null)
            throw new InvalidDataException("Playback stream identity changed.");
        if (reply.Completed)
        {
            if (reply.Data.Length != 0 || reply.DataSha256.Length != 0 || reply.LocalFrame != -1 || reply.NativeFrame != -1 ||
                reply.SampleOffset != (reply.Kind == "audio" ? reply.TotalSamples : 0))
                throw new InvalidDataException("Invalid playback completion envelope.");
            return;
        }
        if (reply.Kind == "video")
        {
            if (reply.Data.Length != BackendMediaDecoder.FrameBytes || reply.LocalFrame < opened.StartFrame || reply.LocalFrame >= opened.DurationFrames ||
                reply.NativeFrame is < 0 or >= BackendMediaDecoder.MaxFrames || reply.SampleOffset != 0)
                throw new InvalidDataException("Invalid playback video frame.");
        }
        else if (reply.Kind != "audio" || reply.LocalFrame != -1 || reply.NativeFrame != -1 || reply.Data.Length is <= 0 or > 32768 ||
                 reply.Data.Length % (sizeof(float) * AudioTimeline.Channels) != 0 || reply.SampleOffset < 0 ||
                 reply.SampleOffset > reply.TotalSamples - reply.Data.Length / (sizeof(float) * AudioTimeline.Channels))
            throw new InvalidDataException("Invalid playback PCM chunk.");
        if (!string.Equals(Convert.ToHexString(SHA256.HashData(reply.Data)), reply.DataSha256, StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException("Invalid playback payload fingerprint.");
    }

    private static void CheckPlaybackError(BackendPlaybackReply? reply)
    {
        if (reply is null || reply.ErrorCode is null) throw new InvalidDataException("Null playback reply.");
        if (reply.ErrorCode.Length != 0) throw new BackendCommandException(reply.ErrorCode);
    }
}
