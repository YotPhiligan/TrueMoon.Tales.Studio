using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using TrueMoon.Tales.Studio.Persistence;
using static TrueMoon.Tales.Studio.Backend.Export.BackendExportStorage;

namespace TrueMoon.Tales.Studio.Backend.Export;

internal sealed class BackendExportJobs : IDisposable
{
    internal const int ChunkSize = 256 * 1024;
    internal const long MaxArtifactSize = 1024L * 1024 * 1024;

    private readonly Lazy<BackendExportStorage> _storage;
    private readonly SemaphoreSlim _gate = new(1, 1);
    private readonly CancellationTokenSource _shutdown = new();
    private readonly Func<ProjectFileDocument, string, string, CancellationToken, Task<BackendExportPlan>> _plan;
    private readonly Func<string, string, CancellationToken, Task> _run;
    private ActiveJob? _active;
    private int _disposed;

    internal BackendExportJobs(BackendOptions options,
        Func<ProjectFileDocument, string, string, CancellationToken, Task<BackendExportPlan>>? plan = null,
        Func<string, string, CancellationToken, Task>? run = null)
    {
        _storage = new(() => new BackendExportStorage(options));
        _plan = plan ?? ((document, profile, directory, token) => BackendExportPlan.CreateAsync(document.Project,
            document.Fingerprint, profile, directory, new BackendMediaResolver(options), BackendMediaReader.Decoder(options), token));
        _run = run ?? ((directory, sha, token) => BackendExportWorker.RunAsync(options.ConfigPath, directory, sha, token));
    }

    internal async Task<BackendExportReply> StartAsync(string clientKey, string profile, long expectedRevision, string fingerprint,
        Func<CancellationToken, Task<ProjectFileDocument>> observe, CancellationToken token)
    {
        if (clientKey is not { Length: >= 16 and <= 80 } || !clientKey.All(c => char.IsAsciiLetterOrDigit(c) || c is '_' or '-') ||
            !ValidProfile(profile) || expectedRevision < 0 || !ValidHash(fingerprint))
            throw new BackendExportException("invalid_export");
        var id = "export-" + Hash(Encoding.ASCII.GetBytes(clientKey))[..32].ToLowerInvariant();
        using var scope = CancellationTokenSource.CreateLinkedTokenSource(token, _shutdown.Token);
        await _gate.WaitAsync(scope.Token).ConfigureAwait(false);
        try
        {
            ObjectDisposedException.ThrowIf(_disposed != 0, this);
            var storage = _storage.Value;
            var directory = storage.JobDirectory(id);
            if (Directory.Exists(directory)) return Existing(storage, directory, id, profile, expectedRevision, fingerprint);
            if (_active is not null) throw new BackendExportException("export_busy");

            FileStream? slot = storage.AcquireSlot();
            try
            {
                if (Directory.Exists(storage.Check(directory))) return Existing(storage, directory, id, profile, expectedRevision, fingerprint);
                var baseline = await observe(scope.Token).ConfigureAwait(false);
                var plan = await _plan(baseline, profile, directory, scope.Token).ConfigureAwait(false);
                if (plan.Request.Length is < 1 or > MaxRequestBytes || plan.Frames <= 0 || !double.IsFinite(plan.Fps) || plan.Fps <= 0)
                    throw new BackendExportException("invalid_export");
                await observe(scope.Token).ConfigureAwait(false);
                var receipt = new BackendExportStatus(1, id, baseline.Project.Id, baseline.Project.Revision,
                    baseline.Fingerprint, profile, 1, "running", Hash(plan.Request), plan.Frames, plan.Fps);
                scope.Token.ThrowIfCancellationRequested();
                storage.Create(directory, plan.Request, receipt);
                // The rename is the acceptance point. Losing the response cannot submit another worker.
                var active = new ActiveJob(receipt, directory, slot);
                _active = active;
                slot = null;
                active.Completion = Task.Run(() => RunAsync(storage, active));
                return Reply(receipt);
            }
            finally { slot?.Dispose(); }
        }
        finally { _gate.Release(); }
    }

    private static BackendExportReply Existing(BackendExportStorage storage, string directory, string id, string profile, long revision, string sha)
    {
        var status = Load(storage, directory, id);
        if (status.Profile != profile || status.ProjectRevision != revision || !EqualHash(status.ProjectFingerprint, sha))
            throw new BackendExportException("export_conflict");
        return Reply(WithProgress(storage, directory, status));
    }

    internal async Task<BackendExportReply> StatusAsync(string id, CancellationToken token)
    {
        await _gate.WaitAsync(token).ConfigureAwait(false);
        try
        {
            var storage = _storage.Value;
            var directory = storage.JobDirectory(id);
            return Reply(WithProgress(storage, directory, Load(storage, directory, id)));
        }
        finally { _gate.Release(); }
    }

    internal async Task<BackendExportReply> CancelAsync(string id, long expectedJobRevision, CancellationToken token)
    {
        if (expectedJobRevision < 1) throw new BackendExportException("invalid_export");
        await _gate.WaitAsync(token).ConfigureAwait(false);
        try
        {
            var storage = _storage.Value;
            var directory = storage.JobDirectory(id);
            var status = Load(storage, directory, id);
            if (_active is not { } active || active.Status.Id != id || status.State != "running" || status.Revision != expectedJobRevision)
                throw new BackendExportException("export_conflict");
            token.ThrowIfCancellationRequested();
            var stopping = Advance(status, "stopping");
            storage.Save(directory, stopping);
            active.Status = stopping;
            active.Stop.Cancel();
            return Reply(stopping);
        }
        finally { _gate.Release(); }
    }

    internal async Task<BackendExportReply> RecoverAsync(string id, CancellationToken token)
    {
        await _gate.WaitAsync(token).ConfigureAwait(false);
        try
        {
            var storage = _storage.Value;
            var directory = storage.JobDirectory(id);
            var status = Load(storage, directory, id);
            if (status.State is "cancelled" or "failed" or "interrupted") return Reply(status);
            if (_active?.Status.Id == id) throw new BackendExportException("export_busy");
            using var slot = status.State is "running" or "stopping" ? storage.AcquireSlot() : null;
            using var worker = storage.AcquireWorkerLock(directory);
            // Reload once both owners have been excluded. Reconciliation never submits a process.
            status = Load(storage, directory, id);
            if (status.State is "cancelled" or "failed" or "interrupted") return Reply(status);
            var proof = status.State == "stopping" ? null : await ReadProofAsync(storage, directory, status, token).ConfigureAwait(false);
            if (status.State == "completed" && proof is not null) return Reply(status);
            var reconciled = status.State switch
            {
                "stopping" => Advance(status, "cancelled"),
                "completed" => Advance(status, "failed", "invalid_export_artifact"),
                _ => proof is null ? Advance(status, "interrupted", "export_interrupted") : Complete(status, proof.Value)
            };
            token.ThrowIfCancellationRequested();
            storage.Save(directory, reconciled);
            return Reply(reconciled);
        }
        finally { _gate.Release(); }
    }

    internal async Task<BackendExportReply> ChunkAsync(string id, string artifactSha256, long offset, CancellationToken token)
    {
        if (!ValidHash(artifactSha256) || offset < 0) throw new BackendExportException("invalid_export");
        await _gate.WaitAsync(token).ConfigureAwait(false);
        try
        {
            var storage = _storage.Value;
            var directory = storage.JobDirectory(id);
            var status = Load(storage, directory, id);
            if (status.State != "completed" || status.ArtifactSizeBytes is not { } size)
                throw new BackendExportException("export_not_ready");
            if (!EqualHash(artifactSha256, status.ArtifactSha256)) throw new BackendExportException("export_conflict");
            if (offset >= size) throw new BackendExportException("invalid_export");
            try
            {
                await using var stream = storage.Open(directory, "Draft.mp4");
                if (stream.Length != size) throw new BackendExportException("invalid_export_artifact");
                stream.Position = offset;
                var bytes = new byte[(int)Math.Min(ChunkSize, size - offset)];
                await stream.ReadExactlyAsync(bytes, token).ConfigureAwait(false);
                // A complete stream is independently SHA-checked by the caller. The file is never opened via caller paths.
                var reply = Reply(status);
                reply.Data = bytes;
                return reply;
            }
            catch (Exception error) when (error is FileNotFoundException or DirectoryNotFoundException or EndOfStreamException)
            { throw new BackendExportException("invalid_export_artifact"); }
        }
        finally { _gate.Release(); }
    }

    private async Task RunAsync(BackendExportStorage storage, ActiveJob job)
    {
        using var scope = CancellationTokenSource.CreateLinkedTokenSource(job.Stop.Token, _shutdown.Token);
        scope.CancelAfter(TimeSpan.FromSeconds(310));
        (long Size, string Sha)? proof = null;
        var failure = "invalid_export_artifact";
        try
        {
            await _run(job.Directory, job.Status.RequestSha256, scope.Token).ConfigureAwait(false);
            proof = await ReadProofAsync(storage, job.Directory, job.Status, scope.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException) { failure = "export_timeout"; }
        catch (Exception error) when (error is IOException or InvalidDataException or UnauthorizedAccessException or InvalidOperationException or
            System.ComponentModel.Win32Exception or JsonException or ArgumentException or TimeoutException)
        { failure = error is BackendExportException classified ? classified.Code : "export_failed"; }
        finally
        {
            await _gate.WaitAsync().ConfigureAwait(false);
            try
            {
                var status = job.Status;
                if (status.State == "stopping") storage.Save(job.Directory, Advance(status, "cancelled"));
                else if (proof is { } completed) storage.Save(job.Directory, Complete(status, completed));
                else if (!_shutdown.IsCancellationRequested) storage.Save(job.Directory, Advance(status, "failed", failure));
                // A shutdown can interrupt proof validation after the worker wrote its result. Recovery can still attest it.
            }
            catch (Exception error) when (error is IOException or UnauthorizedAccessException)
            {
                // The immutable receipt remains. The explicit recovery operation can reconcile unfinished state.
                Console.Error.WriteLine("Studio export: terminal status could not be written. Recover the job by ID.");
            }
            finally
            {
                _active = null;
                job.Slot.Dispose();
                job.Stop.Dispose();
                _gate.Release();
            }
        }
    }

    private static async Task<(long Size, string Sha)?> ReadProofAsync(BackendExportStorage storage, string directory,
        BackendExportStatus status, CancellationToken token)
    {
        try
        {
            VerifyRequest(storage, directory, status);
            using var proof = JsonDocument.Parse(storage.Read(directory, "Result.json", MaxStatusBytes));
            var result = proof.RootElement;
            if (result.GetProperty("state").GetString() != "completed" || result.GetProperty("output").GetString() != "Draft.mp4" ||
                !EqualHash(result.GetProperty("request_sha256").GetString(), status.RequestSha256)) return null;
            var video = result.GetProperty("video_info");
            if (video.GetProperty("frames").GetInt32() != status.Frames || video.GetProperty("fps").GetDouble() != status.Fps) return null;
            var expected = result.GetProperty("sha256").GetString();
            if (!ValidHash(expected)) return null;
            await using var stream = storage.Open(directory, "Draft.mp4");
            var length = stream.Length;
            if (length is < 1 or > MaxArtifactSize || status.ArtifactSizeBytes is { } oldLength && oldLength != length) return null;
            var sha = Convert.ToHexString(await SHA256.HashDataAsync(stream, token).ConfigureAwait(false));
            return stream.Length == length && EqualHash(sha, expected) &&
                (status.ArtifactSha256 is null || EqualHash(sha, status.ArtifactSha256)) ? (length, sha) : null;
        }
        catch (Exception error) when (error is IOException or InvalidDataException or JsonException or KeyNotFoundException or FormatException or InvalidOperationException)
        { return null; }
    }

    private static BackendExportStatus Load(BackendExportStorage storage, string directory, string id)
    {
        if (!Directory.Exists(directory)) throw new BackendExportException("export_not_found");
        try
        {
            var receipt = JsonSerializer.Deserialize<BackendExportStatus>(storage.Read(directory, "Receipt.json", MaxStatusBytes), Json);
            var status = JsonSerializer.Deserialize<BackendExportStatus>(storage.Read(directory, "Status.json", MaxStatusBytes), Json);
            if (receipt is null || status is null || !ValidStatus(receipt, id) || !ValidStatus(status, id) || receipt.Revision != 1 || receipt.State != "running" ||
                receipt != status with { Revision = 1, State = "running", ArtifactSizeBytes = null, ArtifactSha256 = null, ErrorCode = null, ProgressPercent = null })
                throw new BackendExportException("invalid_export_artifact");
            VerifyRequest(storage, directory, receipt);
            return status;
        }
        catch (Exception error) when (error is FileNotFoundException or DirectoryNotFoundException or EndOfStreamException or JsonException)
        { throw new BackendExportException("invalid_export_artifact"); }
    }

    private static void VerifyRequest(BackendExportStorage storage, string directory, BackendExportStatus receipt)
    {
        var actual = Hash(storage.Read(directory, "Request.json", MaxRequestBytes));
        if (!EqualHash(actual, receipt.RequestSha256)) throw new BackendExportException("invalid_export_artifact");
    }

    private static bool ValidStatus(BackendExportStatus status, string id)
    {
        if (status.Version != 1 || status.Id != id || status.ProjectId is not { Length: > 0 and <= 256 } ||
            status.ProjectRevision < 0 || !ValidHash(status.ProjectFingerprint) || !ValidProfile(status.Profile) ||
            !ValidHash(status.RequestSha256) || status.Frames <= 0 || !double.IsFinite(status.Fps) || status.Fps <= 0)
            return false;
        if (status.State == "completed")
            return status.Revision == 2 && status.ArtifactSizeBytes is > 0 and <= MaxArtifactSize &&
                ValidHash(status.ArtifactSha256) && status.ProgressPercent == 100 && status.ErrorCode is null;
        if (status.ArtifactSizeBytes is not null || status.ArtifactSha256 is not null || status.ProgressPercent is not null)
            return false;
        return status.State switch
        {
            "running" => status.Revision == 1 && status.ErrorCode is null,
            "stopping" => status.Revision == 2 && status.ErrorCode is null,
            "cancelled" => status.Revision == 3 && status.ErrorCode is null,
            "interrupted" => status.Revision == 2 && status.ErrorCode == "export_interrupted",
            "failed" when status.Revision == 2 => status.ErrorCode is "invalid_export_artifact" or "export_timeout" or "export_unavailable" or "export_failed",
            "failed" => status.Revision == 3 && status.ErrorCode == "invalid_export_artifact",
            _ => false
        };
    }

    private static BackendExportStatus WithProgress(BackendExportStorage storage, string directory, BackendExportStatus status)
    {
        if (status.State is not ("running" or "stopping")) return status;
        try
        {
            using var progress = JsonDocument.Parse(storage.Read(directory, "Progress.json", MaxStatusBytes));
            if (progress.RootElement.TryGetProperty("percent", out var percent) && percent.TryGetDouble(out var value) && double.IsFinite(value))
                return status with { ProgressPercent = (int)Math.Clamp(value, 0, 99) };
        }
        catch (Exception error) when (error is IOException or JsonException or InvalidOperationException) { }
        return status;
    }

    private static bool ValidProfile(string? profile) => profile is "draft" or "project" or "final";

    private static BackendExportStatus Advance(BackendExportStatus status, string state, string? error = null) =>
        status with { Revision = status.Revision + 1, State = state, ErrorCode = error, ArtifactSizeBytes = null, ArtifactSha256 = null, ProgressPercent = null };

    private static BackendExportStatus Complete(BackendExportStatus status, (long Size, string Sha) proof) =>
        Advance(status, "completed") with { ArtifactSizeBytes = proof.Size, ArtifactSha256 = proof.Sha, ProgressPercent = 100 };

    private static BackendExportReply Reply(BackendExportStatus status) => new() { JobJson = JsonSerializer.Serialize(status, Json) };

    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0) return;
        _shutdown.Cancel();
        _gate.Wait();
        Task? worker;
        try { worker = _active?.Completion; }
        finally { _gate.Release(); }
        try { worker?.GetAwaiter().GetResult(); }
        finally { _shutdown.Dispose(); }
    }

    private sealed class ActiveJob(BackendExportStatus status, string directory, FileStream slot)
    {
        internal BackendExportStatus Status { get; set; } = status;
        internal string Directory { get; } = directory;
        internal FileStream Slot { get; } = slot;
        internal CancellationTokenSource Stop { get; } = new();
        internal Task Completion { get; set; } = Task.CompletedTask;
    }
}
