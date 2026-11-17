namespace TrueMoon.Tales.Studio.Backend;

/// <summary>Same-build project service. Requests cannot select filesystem paths or grant write authority.</summary>
public interface IStudioBackend
{
    /// <summary>Trims one saved clip and projects its montage with one optimistic commit, or no commit for a no-op.</summary>
    /// <param name="sessionKey">Launch capability.</param><param name="clipId">Existing montage clip identity.</param>
    /// <param name="sourceInFrame">Inclusive source position on the project frame grid.</param>
    /// <param name="sourceOutFrame">Exclusive source position on the project frame grid.</param>
    /// <param name="startFrame">Requested absolute montage start.</param><param name="expectedRevision">Observed revision.</param>
    /// <param name="fingerprint">Observed exact-byte SHA256.</param><param name="cancellationToken">Call deadline.</param>
    /// <returns>Committed or unchanged snapshot, or a classified refusal.</returns>
    Task<BackendProjectReply> TrimClipAsync(string sessionKey, string clipId, double sourceInFrame, double sourceOutFrame,
        int startFrame, long expectedRevision, string fingerprint, CancellationToken cancellationToken = default);

    /// <summary>Opens one bounded audio preparation or continuous video resource for a saved clip.</summary>
    /// <param name="sessionKey">Launch capability.</param><param name="clipId">Saved montage clip.</param>
    /// <param name="kind">Audio or video.</param><param name="localFrame">First local video frame; zero for audio.</param>
    /// <param name="expectedRevision">Observed revision.</param><param name="fingerprint">Observed exact-byte SHA256.</param>
    /// <param name="cancellationToken">Call deadline.</param><returns>Baseline-bound resource metadata or refusal.</returns>
    Task<BackendPlaybackReply> OpenPlaybackAsync(string sessionKey, string clipId, string kind, int localFrame, long expectedRevision, string fingerprint, CancellationToken cancellationToken = default);

    /// <summary>Reads the next bounded payload; no implicit restart or replay occurs.</summary>
    /// <param name="sessionKey">Launch capability.</param><param name="sessionId">Owned resource identity.</param>
    /// <param name="cancellationToken">Call deadline.</param><returns>One frame, PCM chunk, completion or refusal.</returns>
    Task<BackendPlaybackReply> ReadPlaybackAsync(string sessionKey, string sessionId, CancellationToken cancellationToken = default);

    /// <summary>Idempotently closes an owned playback resource and reaps its decoder.</summary>
    /// <param name="sessionKey">Launch capability.</param><param name="sessionId">Owned resource identity.</param>
    /// <param name="cancellationToken">Call deadline.</param><returns>Empty completion message.</returns>
    Task<BackendPlaybackReply> ClosePlaybackAsync(string sessionKey, string sessionId, CancellationToken cancellationToken = default);

    /// <summary>Reads metadata for a project-selected video asset without changing writer ownership.</summary>
    /// <param name="sessionKey">Launch capability.</param><param name="assetId">Registered video asset ID.</param>
    /// <param name="expectedRevision">Observed project revision.</param><param name="fingerprint">Observed project SHA256.</param>
    /// <param name="cancellationToken">Local call deadline.</param><returns>Bounded video metadata or a refusal.</returns>
    Task<BackendMediaReply> MediaInfoAsync(string sessionKey, string assetId, long expectedRevision, string fingerprint, CancellationToken cancellationToken = default);

    /// <summary>Reads one bounded CPU frame from a registered video asset at the observed project baseline.</summary>
    /// <param name="sessionKey">Launch capability.</param><param name="assetId">Registered video asset ID.</param>
    /// <param name="frame">Nonnegative source frame index.</param><param name="expectedRevision">Observed project revision.</param>
    /// <param name="fingerprint">Observed project SHA256.</param><param name="cancellationToken">Local call deadline.</param>
    /// <returns>Owned BGRA pixels with SHA256, or a classified refusal.</returns>
    Task<BackendMediaReply> FrameAsync(string sessionKey, string assetId, int frame, long expectedRevision, string fingerprint, CancellationToken cancellationToken = default);

    /// <summary>Checks readiness and the identity of the owned service.</summary>
    /// <param name="sessionKey">The per-launch capability.</param><param name="cancellationToken">The local call deadline.</param>
    /// <returns>The role and contract identity.</returns>
    Task<BackendIdentity> HelloAsync(string sessionKey, CancellationToken cancellationToken = default);

    /// <summary>Reads the startup-selected project without migration, recovery or writes.</summary>
    /// <param name="sessionKey">The per-launch capability.</param><param name="cancellationToken">The local call deadline.</param>
    /// <returns>A normalized snapshot or a classified failure.</returns>
    Task<BackendProjectReply> OpenProjectAsync(string sessionKey, CancellationToken cancellationToken = default);

    /// <summary>Explicitly claims the sole writer after checking the observed revision and exact bytes.</summary>
    /// <param name="sessionKey">Launch capability.</param><param name="expectedRevision">Observed revision.</param>
    /// <param name="fingerprint">Observed exact-byte SHA256.</param><param name="cancellationToken">Call deadline.</param>
    /// <returns>Current snapshot or a classified refusal; no migration is performed.</returns>
    Task<BackendProjectReply> AcquireWriterAsync(string sessionKey, long expectedRevision, string fingerprint, CancellationToken cancellationToken = default);

    /// <summary>Moves one clip inside its existing window with one optimistic commit, or no commit for a no-op.</summary>
    /// <param name="sessionKey">Launch capability.</param><param name="clipId">Existing clip identity.</param>
    /// <param name="startFrame">Requested absolute start in frames.</param><param name="expectedRevision">Observed revision.</param>
    /// <param name="fingerprint">Observed exact-byte SHA256.</param><param name="cancellationToken">Call deadline.</param>
    /// <returns>Committed snapshot, no-op snapshot, or classified refusal.</returns>
    Task<BackendProjectReply> MoveClipAsync(string sessionKey, string clipId, int startFrame, long expectedRevision, string fingerprint, CancellationToken cancellationToken = default);

    /// <summary>Explicitly migrates the observed schema 1 project; requires writer ownership.</summary>
    /// <param name="sessionKey">Launch capability.</param><param name="expectedRevision">Observed revision.</param>
    /// <param name="fingerprint">Observed exact-byte SHA256.</param><param name="cancellationToken">Call deadline.</param>
    /// <returns>The migrated snapshot or a classified refusal.</returns>
    Task<BackendProjectReply> MigrateProjectAsync(string sessionKey, long expectedRevision, string fingerprint, CancellationToken cancellationToken = default);

    /// <summary>Explicitly claims the writer and completes only a validated durable pending transaction.</summary>
    /// <param name="sessionKey">Launch capability.</param><param name="cancellationToken">Call deadline.</param>
    /// <returns>The recovered snapshot; never replays a move.</returns>
    Task<BackendProjectReply> RecoverProjectAsync(string sessionKey, CancellationToken cancellationToken = default);
}

/// <summary>Identity negotiated before accepting project data.</summary>
public sealed class BackendIdentity
{
    /// <summary>The service role.</summary>
    public string Role { get; set; } = "";
    /// <summary>The backend protocol version, independent of the product version.</summary>
    public int ProtocolVersion { get; set; }
    /// <summary>The normalized project contract version.</summary>
    public int ProjectContractVersion { get; set; }
    /// <summary>The product build version.</summary>
    public string ProductVersion { get; set; } = "";
    /// <summary>The serving process.</summary>
    public int ProcessId { get; set; }
    /// <summary>The owning UI or inspection process.</summary>
    public int ParentProcessId { get; set; }
}

/// <summary>Bounded project transport; the JSON is ProjectContract v1, never the storage envelope.</summary>
public sealed class BackendProjectReply
{
    /// <summary>Empty on success, otherwise a stable error code.</summary>
    public string ErrorCode { get; set; } = "";
    /// <summary>The storage schema observed without changing it.</summary>
    public int SchemaVersion { get; set; }
    /// <summary>SHA256 of the exact project bytes.</summary>
    public string Fingerprint { get; set; } = "";
    /// <summary>The validated, normalized project transport snapshot.</summary>
    public string ProjectJson { get; set; } = "";
    /// <summary>Whether the retained legacy scenario controls ordering, gaps and anchors.</summary>
    public bool ScenarioActive { get; set; }
}
