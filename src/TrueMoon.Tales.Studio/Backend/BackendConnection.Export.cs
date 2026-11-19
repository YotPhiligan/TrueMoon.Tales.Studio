using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using TrueMoon.Tales.Studio.Backend.Export;

namespace TrueMoon.Tales.Studio.Backend;

internal sealed partial class BackendConnection
{
    private static readonly JsonSerializerOptions ExportJson = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower,
        UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow,
        AllowDuplicateProperties = false
    };

    internal async Task<BackendExportCheck> CheckExportAsync(string profile, long revision, string fingerprint, CancellationToken token)
    {
        var reply = await SendExportAsync(ct => service.CheckExportAsync(options.SessionKey, profile, revision, fingerprint, ct), token)
            .ConfigureAwait(false);
        var check = JsonSerializer.Deserialize<BackendExportCheck>(reply.JobJson, ExportJson);
        if (reply.Data.Length != 0 || check is null || check.ProjectRevision != revision || !SameHash(check.ProjectFingerprint, fingerprint) ||
            check.Profile != profile || check.Width is < 2 or > 4096 || check.Height is < 2 or > 4096 || check.Frames <= 0 ||
            !double.IsFinite(check.Fps) || check.Fps is < 1 or > 120)
            throw new InvalidDataException("Invalid export check metadata.");
        return check;
    }

    internal Task<BackendExportStatus> FindExportAsync(string clientKey, CancellationToken token) =>
        ExportJobAsync(BackendExportJobs.JobId(clientKey), ct => service.FindExportAsync(options.SessionKey, clientKey, ct), token);

    internal async Task<BackendExportStatus> StartWorkingReviewAsync(string clientKey, long revision, string fingerprint,
        CancellationToken token, BackendReviewRange? range = null)
    {
        if (range is { IsValid: false }) throw new BackendCommandException("invalid_export");
        var job = await ExportJobAsync(BackendExportJobs.JobId(clientKey),
            ct => service.WorkingReviewAsync(options.SessionKey, "start", clientKey, revision, fingerprint,
                range?.Start ?? 0, range?.End ?? 0, ct), token).ConfigureAwait(false);
        if (job.ProjectRevision != revision || !SameHash(job.ProjectFingerprint, fingerprint) || job.Profile != "draft" ||
            job.RangeStart != range?.Start || range is { } selection && job.Frames != selection.End - selection.Start)
            throw new InvalidDataException("Preview reply does not match the submitted snapshot.");
        return job;
    }

    internal Task<BackendExportStatus> WorkingReviewStatusAsync(string id, CancellationToken token) =>
        ExportJobAsync(id, ct => service.WorkingReviewAsync(options.SessionKey, "status", id, 0, "", 0, 0, ct), token);

    internal Task<BackendExportStatus> CancelWorkingReviewAsync(string id, long revision, CancellationToken token) =>
        ExportJobAsync(id, ct => service.WorkingReviewAsync(options.SessionKey, "cancel", id, revision, "", 0, 0, ct), token);

    internal async Task<BackendExportStatus> StartExportAsync(string clientKey, string profile, long revision,
        string fingerprint, CancellationToken token = default)
    {
        var (job, _) = await ExportAsync(ct => service.StartExportAsync(options.SessionKey, clientKey, profile, revision, fingerprint, ct), token).ConfigureAwait(false);
        if (job.ProjectRevision != revision || !SameHash(job.ProjectFingerprint, fingerprint) || job.Profile != profile)
            throw new InvalidDataException("Export reply does not match the submitted snapshot.");
        return job;
    }

    internal Task<BackendExportStatus> ExportStatusAsync(string id, CancellationToken token = default) =>
        ExportJobAsync(id, ct => service.ExportStatusAsync(options.SessionKey, id, ct), token);

    internal async Task<BackendExportStatus> CancelExportAsync(string id, long revision, CancellationToken token = default)
    {
        var job = await ExportJobAsync(id, ct => service.CancelExportAsync(options.SessionKey, id, revision, ct), token).ConfigureAwait(false);
        if (job.Revision <= revision || job.State is not ("stopping" or "cancelled"))
            throw new InvalidDataException("Invalid export cancellation reply.");
        return job;
    }

    internal Task<BackendExportStatus> RecoverExportAsync(string id, CancellationToken token = default) =>
        ExportJobAsync(id, ct => service.RecoverExportAsync(options.SessionKey, id, ct), token);

    internal async Task<byte[]> ExportChunkAsync(string id, string sha256, long offset, CancellationToken token = default)
    {
        var (job, bytes) = await ExportAsync(ct => service.ExportChunkAsync(options.SessionKey, id, sha256, offset, ct), token,
            chunk: true).ConfigureAwait(false);
        if (job.Id != id || job.State != "completed" || !SameHash(job.ArtifactSha256, sha256) ||
            job.ArtifactSizeBytes is not { } size || offset < 0 || offset >= size ||
            bytes.Length != Math.Min(BackendExportJobs.ChunkSize, size - offset))
            throw new InvalidDataException("Export reply does not match the requested artifact range.");
        return bytes;
    }

    private async Task<BackendExportStatus> ExportJobAsync(string id, Func<CancellationToken, Task<BackendExportReply>> send,
        CancellationToken token)
    {
        var (job, _) = await ExportAsync(send, token).ConfigureAwait(false);
        if (job.Id != id) throw new InvalidDataException("Unexpected export ID.");
        return job;
    }

    private async Task<(BackendExportStatus Job, byte[] Data)> ExportAsync(Func<CancellationToken, Task<BackendExportReply>> send,
        CancellationToken token, bool chunk = false)
    {
        var reply = await SendExportAsync(send, token).ConfigureAwait(false);
        if (chunk ? reply.Data.Length is <= 0 or > BackendExportJobs.ChunkSize : reply.Data.Length != 0)
            throw new InvalidDataException("Invalid export reply.");
        BackendExportStatus? job;
        try { job = JsonSerializer.Deserialize<BackendExportStatus>(reply.JobJson, ExportJson); }
        catch (JsonException error) { throw new InvalidDataException("Invalid export job JSON.", error); }
        if (job is null || job.Version != 1 || !IsExportId(job.Id) || string.IsNullOrWhiteSpace(job.ProjectId) ||
            job.ProjectRevision < 0 || job.Revision < 1 || job.Profile is not ("draft" or "project" or "final") ||
            job.State is not ("running" or "stopping" or "completed" or "cancelled" or "failed" or "interrupted") ||
            !IsHash(job.ProjectFingerprint) || !IsHash(job.RequestSha256) ||
            job.Frames <= 0 || job.RangeStart is < 0 || (long)(job.RangeStart ?? 0) + job.Frames > int.MaxValue ||
            !double.IsFinite(job.Fps) || job.Fps is <= 0 or > 240 ||
            job.ProgressPercent is < 0 or > 100 || !ValidArtifact(job))
            throw new InvalidDataException("Invalid export job metadata.");
        return (job, reply.Data);
    }

    private async Task<BackendExportReply> SendExportAsync(Func<CancellationToken, Task<BackendExportReply>> send, CancellationToken token)
    {
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(token);
        deadline.CancelAfter(TimeSpan.FromSeconds(12));
        var identity = await TransportAsync(() => service.HelloAsync(options.SessionKey, deadline.Token)).ConfigureAwait(false);
        ValidateIdentity(identity, processes.Owned?.IsServing(identity.ProcessId) == true);
        var reply = await TransportAsync(() => send(deadline.Token)).ConfigureAwait(false);
        if (reply is null || reply.ErrorCode is null || reply.JobJson is null || reply.Data is null ||
            Encoding.Unicode.GetByteCount(reply.JobJson) > 16 * 1024)
            throw new InvalidDataException("Invalid export reply.");
        if (reply.ErrorCode.Length > 0)
        {
            if (reply.Data.Length != 0) throw new InvalidDataException("Unexpected bytes in an export refusal.");
            throw new BackendCommandException(reply.ErrorCode, reply.JobJson.Length == 0 ? null : BackendReadinessIssue.Decode(reply.JobJson));
        }
        return reply;
    }

    private static bool ValidArtifact(BackendExportStatus job) => job.State == "completed"
        ? job.ArtifactSizeBytes is > 0 and <= BackendExportJobs.MaxArtifactSize && IsHash(job.ArtifactSha256)
        : job.ArtifactSizeBytes is null && job.ArtifactSha256 is null;

    internal static bool IsExportId(string? id) =>
        id is { Length: 39 } && id.StartsWith("export-", StringComparison.Ordinal) &&
        id.Skip(7).All(ch => ch is >= '0' and <= '9' or >= 'a' and <= 'f');

    internal static bool IsExportClientKey(string? key) =>
        key is { Length: >= 16 and <= 80 } && key.All(ch => char.IsAsciiLetterOrDigit(ch) || ch is '_' or '-');

    internal static bool IsHash(string? value) => value is { Length: 64 } && value.All(Uri.IsHexDigit);
    private static bool SameHash(string? left, string right) => string.Equals(left, right, StringComparison.OrdinalIgnoreCase);
}
