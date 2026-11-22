using System.Text.Json;

namespace TrueMoon.Tales.Studio.Backend;

internal sealed partial class BackendConnection
{
    internal Task<OpenedBackendProject> CollectFullTransferAsync(string id, string requestSha, OpenedBackendProject baseline,
        CancellationToken token) => CollectFullTransferAsync(id, requestSha, baseline.Project.Revision, baseline.Fingerprint, token);

    internal Task<OpenedBackendProject> CollectFullTransferAsync(string id, string requestSha, long revision, string fingerprint,
        CancellationToken token) => CommandAsync(ct => service.CollectFullTransferAsync(options.SessionKey, id, requestSha, revision,
            fingerprint, ct), token);

    internal Task<FullTransferRunState> FullTransferRunAsync(string id, string requestSha, string operation,
        OpenedBackendProject baseline, CancellationToken token) =>
        FullTransferRunAsync(id, requestSha, operation, baseline.Project.Revision, baseline.Fingerprint, token);

    internal async Task<FullTransferRunState> FullTransferRunAsync(string id, string requestSha, string operation,
        long revision, string fingerprint, CancellationToken token)
    {
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(token);
        deadline.CancelAfter(TimeSpan.FromSeconds(52));
        var identity = await TransportAsync(() => service.HelloAsync(options.SessionKey, deadline.Token)).ConfigureAwait(false);
        ValidateIdentity(identity, processes.Owned?.IsServing(identity.ProcessId) == true);
        var reply = await TransportAsync(() => service.FullTransferRunAsync(options.SessionKey, id, requestSha, operation,
            revision, fingerprint, deadline.Token)).ConfigureAwait(false);
        if (reply is { ErrorCode.Length: > 0 }) throw new BackendCommandException(reply.ErrorCode);
        if (reply?.ReceiptJson is not { Length: > 0 and <= 4096 } json ||
            JsonSerializer.Deserialize<FullTransferRunState>(json, BackendAdaptation.Json) is not { } state ||
            !MatchesTransferRun(state, id, requestSha))
            throw new InvalidDataException("Invalid Full Transfer run state.");
        return state;
    }

    internal static bool MatchesTransferRun(FullTransferRunState state, string id, string requestSha) =>
        state is { Version: 1, DraftTakeId.Length: > 0 } && state.Id == id &&
        string.Equals(state.RequestSha256, requestSha, StringComparison.OrdinalIgnoreCase) &&
        state.State is ("prepared" or "staged" or "running" or "stopping" or "cancelled" or "timed_out" or
                        "interrupted" or "failed" or "completed") &&
        (state.State == "completed" ? IsHash(state.OutputSha256) : state.OutputSha256 is null);

    internal Task<FullTransferReceipt> FullTransferJobAsync(string id, string? expectedRequestSha, OpenedBackendProject baseline,
        CancellationToken token) => FullTransferJobAsync(id, expectedRequestSha, baseline.Project.Revision, baseline.Fingerprint, token);

    internal async Task<FullTransferReceipt> FullTransferJobAsync(string id, string? expectedRequestSha, long revision, string fingerprint,
        CancellationToken token)
    {
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(token);
        deadline.CancelAfter(TimeSpan.FromSeconds(52));
        var identity = await TransportAsync(() => service.HelloAsync(options.SessionKey, deadline.Token)).ConfigureAwait(false);
        ValidateIdentity(identity, processes.Owned?.IsServing(identity.ProcessId) == true);
        var reply = await TransportAsync(() => service.FullTransferJobAsync(options.SessionKey, id, expectedRequestSha,
            revision, fingerprint, deadline.Token)).ConfigureAwait(false);
        if (reply is { ErrorCode.Length: > 0 }) throw new BackendCommandException(reply.ErrorCode);
        if (reply?.ReceiptJson is not { Length: > 0 and <= 4096 } json ||
            JsonSerializer.Deserialize<FullTransferReceipt>(json, BackendAdaptation.Json) is not { } receipt ||
            !MatchesTransferJob(receipt, id, expectedRequestSha, revision, fingerprint))
            throw new InvalidDataException("Invalid Full Transfer job receipt.");
        return receipt;
    }

    internal Task<FullTransferReceipt> PrepareFullTransferAsync(FullTransferForm form, OpenedBackendProject baseline,
        CancellationToken token) => PrepareFullTransferAsync(form, baseline.Project.Revision, baseline.Fingerprint, token);

    internal async Task<FullTransferReceipt> PrepareFullTransferAsync(FullTransferForm form, long revision, string fingerprint,
        CancellationToken token)
    {
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(token);
        deadline.CancelAfter(TimeSpan.FromSeconds(52));
        var identity = await TransportAsync(() => service.HelloAsync(options.SessionKey, deadline.Token)).ConfigureAwait(false);
        ValidateIdentity(identity, processes.Owned?.IsServing(identity.ProcessId) == true);
        var json = JsonSerializer.Serialize(form, BackendAdaptation.Json);
        var reply = await TransportAsync(() => service.PrepareFullTransferAsync(options.SessionKey, json, revision,
            fingerprint, deadline.Token)).ConfigureAwait(false);
        if (reply is { ErrorCode.Length: > 0 }) throw new BackendCommandException(reply.ErrorCode);
        if (reply?.ReceiptJson is not { Length: > 0 and <= 4096 } receiptJson ||
            JsonSerializer.Deserialize<FullTransferReceipt>(receiptJson, BackendAdaptation.Json) is not { } receipt ||
            !MatchesTransferPreparation(receipt, form, revision, fingerprint))
            throw new InvalidDataException("Invalid Full Transfer preparation receipt.");
        return receipt;
    }

    internal static bool MatchesTransferJob(FullTransferReceipt receipt, string id, string? expectedRequestSha, long revision,
        string fingerprint) => ValidTransferReceipt(receipt) && receipt.Id == id && receipt.Revision <= revision &&
        (expectedRequestSha is null || receipt.Staged && receipt.Revision == revision &&
            string.Equals(receipt.Fingerprint, fingerprint, StringComparison.OrdinalIgnoreCase) &&
            string.Equals(receipt.RequestSha256, expectedRequestSha, StringComparison.OrdinalIgnoreCase));

    internal static bool MatchesTransferPreparation(FullTransferReceipt receipt, FullTransferForm form, long revision,
        string fingerprint) => ValidTransferReceipt(receipt) && !receipt.Staged &&
        receipt.Id == BackendFullTransfer.JobId(form.ClientKey) && receipt.DraftTakeId == form.DraftTakeId?.Trim() &&
        receipt.Revision == revision && string.Equals(receipt.Fingerprint, fingerprint, StringComparison.OrdinalIgnoreCase);

    private static bool ValidTransferReceipt(FullTransferReceipt receipt) =>
        receipt is { Version: 1, Width: 1280, Height: 736, Fps: 24, Frames: >= 56 and <= 192, Enqueued: false, Revision: >= 0,
            DraftTakeId.Length: > 0 } && (receipt.Frames - 5) % 17 == 0 &&
        receipt.State == (receipt.Staged ? "staged" : "prepared") && IsHash(receipt.Fingerprint) &&
        IsHash(receipt.DraftSha256) && IsHash(receipt.RequestSha256) && IsHash(receipt.ConfigSha256) && IsHash(receipt.BuildSha256);
}
