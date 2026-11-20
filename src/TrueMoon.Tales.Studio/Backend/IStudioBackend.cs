namespace TrueMoon.Tales.Studio.Backend;

/// <summary>Same-build project service. Requests cannot select filesystem paths or grant write authority.</summary>
public interface IStudioBackend
{
    /// <summary>Reads an inclusive line range from an allowed UTF-8 Markdown chapter.</summary>
    /// <param name="sessionKey">Launch capability.</param>
    /// <param name="reference">Logical chapter reference under the configured texts mount.</param>
    /// <param name="firstLine">First one-based line.</param>
    /// <param name="lastLine">Last included line.</param>
    /// <param name="cancellationToken">Call deadline.</param>
    /// <returns>The observed text SHA256 and fragment or a refusal.</returns>
    Task<BackendTextReply> ReadAdaptationTextAsync(string sessionKey, string reference, int firstLine, int lastLine,
        CancellationToken cancellationToken = default);

    /// <summary>Starts, checks or cancels the session-owned temporary CPU review.</summary>
    /// <param name="sessionKey">Launch capability.</param>
    /// <param name="operation">Start, status or cancel.</param>
    /// <param name="key">Submission key on start, opaque review ID otherwise.</param>
    /// <param name="revision">Project revision on start, job revision on cancel.</param>
    /// <param name="fingerprint">Exact project SHA256 on start.</param>
    /// <param name="startFrame">Inclusive project frame for a bounded review, otherwise zero.</param>
    /// <param name="endFrame">Exclusive project frame for a bounded review, otherwise zero.</param>
    /// <param name="cancellationToken">Call deadline.</param>
    /// <returns>The temporary job status or a classified refusal.</returns>
    Task<BackendExportReply> WorkingReviewAsync(string sessionKey, string operation, string key, long revision, string fingerprint,
        int startFrame, int endFrame, CancellationToken cancellationToken = default);

    /// <summary>Checks the saved montage for blocking problems without starting a render.</summary>
    /// <param name="sessionKey">Launch capability.</param>
    /// <param name="expectedRevision">Observed project revision.</param>
    /// <param name="fingerprint">Observed exact project SHA256.</param>
    /// <param name="cancellationToken">Call deadline.</param>
    /// <returns>The bounded readiness report or a classified refusal.</returns>
    Task<BackendExportReply> CheckReadinessAsync(string sessionKey, long expectedRevision, string fingerprint,
        CancellationToken cancellationToken = default);

    /// <summary>Applies one bounded scenario, library or placement command through the exclusive project writer.</summary>
    /// <param name="sessionKey">Launch capability.</param>
    /// <param name="commandJson">Closed command envelope for the selected project.</param>
    /// <param name="expectedRevision">Observed project revision.</param>
    /// <param name="fingerprint">Observed exact project SHA256.</param>
    /// <param name="cancellationToken">Call deadline.</param>
    /// <returns>The committed or unchanged baseline, or a classified refusal.</returns>
    Task<BackendProjectReply> EditScenarioAsync(string sessionKey, string commandJson, long expectedRevision, string fingerprint,
        CancellationToken cancellationToken = default);

    /// <summary>Checks saved export parameters without creating a job.</summary>
    /// <param name="sessionKey">Launch capability.</param><param name="profile">Encoding profile.</param>
    /// <param name="expectedRevision">Observed project revision.</param><param name="fingerprint">Observed project SHA256.</param>
    /// <param name="cancellationToken">Call deadline.</param><returns>Output parameters or a refusal.</returns>
    Task<BackendExportReply> CheckExportAsync(string sessionKey, string profile, long expectedRevision, string fingerprint,
        CancellationToken cancellationToken = default);

    /// <summary>Reads export status by its submission key without accepting a new request.</summary>
    /// <param name="sessionKey">Launch capability.</param><param name="clientKey">Durable submission key.</param>
    /// <param name="cancellationToken">Call deadline.</param><returns>Job status or a refusal.</returns>
    Task<BackendExportReply> FindExportAsync(string sessionKey, string clientKey, CancellationToken cancellationToken = default);

    /// <summary>Accepts one immutable CPU export request bound to the observed project.</summary>
    /// <param name="sessionKey">Launch capability.</param><param name="clientKey">Durable submission key.</param>
    /// <param name="profile">Export encoding profile.</param><param name="expectedRevision">Observed project revision.</param>
    /// <param name="fingerprint">Observed project SHA256.</param><param name="cancellationToken">Call deadline.</param>
    /// <returns>The accepted job, its existing status after duplicate submission, or a refusal.</returns>
    Task<BackendExportReply> StartExportAsync(string sessionKey, string clientKey, string profile, long expectedRevision,
        string fingerprint, CancellationToken cancellationToken = default);

    /// <summary>Reads the durable status of one export owned by the selected project.</summary>
    /// <param name="sessionKey">Launch capability.</param><param name="id">Export job identity.</param>
    /// <param name="cancellationToken">Call deadline.</param><returns>Job status or a refusal.</returns>
    Task<BackendExportReply> ExportStatusAsync(string sessionKey, string id, CancellationToken cancellationToken = default);

    /// <summary>Requests cancellation of the owned export if its job revision still matches.</summary>
    /// <param name="sessionKey">Launch capability.</param><param name="id">Export job identity.</param>
    /// <param name="expectedJobRevision">Observed job revision, independent of the project revision.</param>
    /// <param name="cancellationToken">Call deadline.</param><returns>The persisted cancellation request or a refusal.</returns>
    Task<BackendExportReply> CancelExportAsync(string sessionKey, string id, long expectedJobRevision, CancellationToken cancellationToken = default);

    /// <summary>Reconciles persisted job state with verified local output without relaunching any worker.</summary>
    /// <param name="sessionKey">Launch capability.</param><param name="id">Export job identity.</param>
    /// <param name="cancellationToken">Call deadline.</param><returns>The reconciled job or a refusal.</returns>
    Task<BackendExportReply> RecoverExportAsync(string sessionKey, string id, CancellationToken cancellationToken = default);

    /// <summary>Reads a bounded MP4 range belonging to the completed job and expected artifact SHA256.</summary>
    /// <param name="sessionKey">Launch capability.</param><param name="id">Export job identity.</param>
    /// <param name="artifactSha256">Published MP4 digest.</param><param name="artifactOffset">First byte of the range.</param>
    /// <param name="cancellationToken">Call deadline.</param><returns>Artifact range and job status, or a refusal.</returns>
    Task<BackendExportReply> ExportChunkAsync(string sessionKey, string id, string artifactSha256, long artifactOffset, CancellationToken cancellationToken = default);

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

    /// <summary>Reads an editing frame using the selected verified proxy when available.</summary>
    Task<BackendMediaReply> PreviewFrameAsync(string sessionKey, string assetId, int frame, long expectedRevision, string fingerprint,
        CancellationToken cancellationToken = default);

    /// <summary>Prepares, inspects, cancels, or disables the cache proxy for a selected take.</summary>
    Task<BackendExportReply> TakeProxyAsync(string sessionKey, string operation, string sourceId, string takeId, long expectedRevision,
        string fingerprint, CancellationToken cancellationToken = default);

    /// <summary>Reads one SHA-verified side of a saved draft/final comparison.</summary>
    /// <param name="sessionKey">Launch capability.</param><param name="sourceId">Owning library source.</param>
    /// <param name="finalTakeId">Final take with the saved derivation.</param><param name="draft">Selects the draft side.</param>
    /// <param name="finalFrame">Frame used to locate the comparison time.</param>
    /// <param name="expectedRevision">Observed project revision.</param><param name="fingerprint">Observed project SHA256.</param>
    /// <param name="cancellationToken">Local call deadline.</param><returns>Bounded verified pixels or a refusal.</returns>
    Task<BackendMediaReply> TakePairFrameAsync(string sessionKey, string sourceId, string finalTakeId, bool draft, int finalFrame,
        long expectedRevision, string fingerprint, CancellationToken cancellationToken = default);

    /// <summary>Builds and verifies a CPU-only Omni.v2 Full Transfer preview.</summary>
    /// <param name="sessionKey">Launch capability.</param><param name="formJson">Source, draft and authored profile inputs.</param>
    /// <param name="expectedRevision">Observed project revision.</param><param name="fingerprint">Observed project SHA256.</param>
    /// <param name="cancellationToken">Caller deadline.</param><returns>Prepared receipt or classified refusal.</returns>
    Task<BackendPreparationReply> PrepareFullTransferAsync(string sessionKey, string formJson, long expectedRevision,
        string fingerprint, CancellationToken cancellationToken = default);

    /// <summary>Opens a Full Transfer receipt or explicitly copies its inputs.</summary>
    /// <param name="sessionKey">Launch capability.</param><param name="jobId">Prepared job identifier.</param>
    /// <param name="expectedRequestSha">Request SHA for staging, or null for read-only inspection.</param>
    /// <param name="expectedRevision">Observed project revision.</param><param name="fingerprint">Observed project SHA256.</param>
    /// <param name="cancellationToken">Caller deadline.</param><returns>Verified job state or classified refusal.</returns>
    Task<BackendPreparationReply> FullTransferJobAsync(string sessionKey, string jobId, string? expectedRequestSha,
        long expectedRevision, string fingerprint, CancellationToken cancellationToken = default);

    /// <summary>Reads, explicitly launches or stops a single staged Full Transfer job.</summary>
    Task<BackendPreparationReply> FullTransferRunAsync(string sessionKey, string jobId, string requestSha, string operation,
        long expectedRevision, string fingerprint, CancellationToken cancellationToken = default);

    /// <summary>Registers the verified result as a new take with its original draft provenance.</summary>
    Task<BackendProjectReply> CollectFullTransferAsync(string sessionKey, string jobId, string requestSha, long expectedRevision,
        string fingerprint, CancellationToken cancellationToken = default);

    /// <summary>Returns the assigned storyboard still for a saved action through the bounded frame envelope.</summary>
    Task<BackendMediaReply> StoryboardStillAsync(string sessionKey, string segmentId, string actionId, long expectedRevision,
        string fingerprint, CancellationToken cancellationToken = default);

    /// <summary>Checks readiness and the identity of the owned service.</summary>
    /// <param name="sessionKey">The per-launch capability.</param><param name="cancellationToken">The local call deadline.</param>
    /// <returns>The role and contract identity.</returns>
    Task<BackendIdentity> HelloAsync(string sessionKey, CancellationToken cancellationToken = default);

    /// <summary>Compares a saved project with an immutable history revision without changing either snapshot.</summary>
    Task<BackendExportReply> CheckDependenciesAsync(string sessionKey, long comparedRevision, long expectedRevision,
        string fingerprint, CancellationToken cancellationToken = default);

    /// <summary>Reads the verified inputs of two saved generation jobs from the selected project.</summary>
    Task<BackendExportReply> CompareJobSnapshotsAsync(string sessionKey, string beforeId, string afterId, long expectedRevision,
        string fingerprint, CancellationToken cancellationToken = default);

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
    /// <summary>Whether the current writer can undo a saved montage edit at this fingerprint.</summary>
    public bool CanUndoMontage { get; set; }
    /// <summary>Whether the current writer can redo a saved montage edit at this fingerprint.</summary>
    public bool CanRedoMontage { get; set; }
}
