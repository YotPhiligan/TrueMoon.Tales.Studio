using System.Text.Json;

namespace TrueMoon.Tales.Studio.Backend;

internal sealed partial class BackendConnection
{
    internal Task<OpenedBackendProject> CollectFullTransferAsync(string id, string requestSha, OpenedBackendProject baseline,
        CancellationToken token) => CommandAsync(ct => service.CollectFullTransferAsync(options.SessionKey,
            id, requestSha, baseline.Project.Revision, baseline.Fingerprint, ct), token);

    internal async Task<FullTransferRunState> FullTransferRunAsync(string id, string requestSha, string operation,
        OpenedBackendProject baseline, CancellationToken token)
    {
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(token);
        deadline.CancelAfter(TimeSpan.FromSeconds(52));
        var identity = await TransportAsync(() => service.HelloAsync(options.SessionKey, deadline.Token)).ConfigureAwait(false);
        ValidateIdentity(identity, processes.Owned?.IsServing(identity.ProcessId) == true);
        var reply = await TransportAsync(() => service.FullTransferRunAsync(options.SessionKey, id, requestSha, operation,
            baseline.Project.Revision, baseline.Fingerprint, deadline.Token)).ConfigureAwait(false);
        if (reply is { ErrorCode.Length: > 0 }) throw new BackendCommandException(reply.ErrorCode);
        if (reply?.ReceiptJson is not { Length: > 0 and <= 4096 } json ||
            JsonSerializer.Deserialize<FullTransferRunState>(json, BackendAdaptation.Json) is not { Version: 1, DraftTakeId.Length: > 0 } state ||
            state.Id != id || !string.Equals(state.RequestSha256, requestSha, StringComparison.OrdinalIgnoreCase) ||
            state.State is not ("prepared" or "staged" or "running" or "stopping" or "cancelled" or "timed_out" or
                               "interrupted" or "failed" or "completed") ||
            (state.State == "completed" ? !IsHash(state.OutputSha256) : state.OutputSha256 is not null))
            throw new InvalidDataException("Invalid Full Transfer run state.");
        return state;
    }

    internal async Task<FullTransferReceipt> FullTransferJobAsync(string id, string? expectedRequestSha, OpenedBackendProject baseline,
        CancellationToken token)
    {
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(token);
        deadline.CancelAfter(TimeSpan.FromSeconds(52));
        var identity = await TransportAsync(() => service.HelloAsync(options.SessionKey, deadline.Token)).ConfigureAwait(false);
        ValidateIdentity(identity, processes.Owned?.IsServing(identity.ProcessId) == true);
        var reply = await TransportAsync(() => service.FullTransferJobAsync(options.SessionKey, id, expectedRequestSha,
            baseline.Project.Revision, baseline.Fingerprint, deadline.Token)).ConfigureAwait(false);
        if (reply is { ErrorCode.Length: > 0 }) throw new BackendCommandException(reply.ErrorCode);
        if (reply?.ReceiptJson is not { Length: > 0 and <= 4096 } json ||
            JsonSerializer.Deserialize<FullTransferReceipt>(json, BackendAdaptation.Json) is not
                { Version: 1, Width: 1280, Height: 736, Fps: 24, Frames: >= 56 and <= 192, Enqueued: false } receipt ||
            receipt.Id != id || !IsHash(receipt.Fingerprint) || receipt.Revision > baseline.Project.Revision ||
            receipt.State != (receipt.Staged ? "staged" : "prepared") ||
            expectedRequestSha is not null && (!receipt.Staged || receipt.Revision != baseline.Project.Revision || receipt.Fingerprint != baseline.Fingerprint ||
                                               !string.Equals(expectedRequestSha, receipt.RequestSha256, StringComparison.OrdinalIgnoreCase)) ||
            !IsHash(receipt.DraftSha256) || !IsHash(receipt.RequestSha256) || !IsHash(receipt.ConfigSha256) || !IsHash(receipt.BuildSha256))
            throw new InvalidDataException("Invalid Full Transfer job receipt.");
        return receipt;
    }

    internal async Task<FullTransferReceipt> PrepareFullTransferAsync(FullTransferForm form, OpenedBackendProject baseline,
        CancellationToken token)
    {
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(token);
        deadline.CancelAfter(TimeSpan.FromSeconds(52));
        var identity = await TransportAsync(() => service.HelloAsync(options.SessionKey, deadline.Token)).ConfigureAwait(false);
        ValidateIdentity(identity, processes.Owned?.IsServing(identity.ProcessId) == true);
        var json = JsonSerializer.Serialize(form, BackendAdaptation.Json);
        var reply = await TransportAsync(() => service.PrepareFullTransferAsync(options.SessionKey, json, baseline.Project.Revision,
            baseline.Fingerprint, deadline.Token)).ConfigureAwait(false);
        if (reply is { ErrorCode.Length: > 0 }) throw new BackendCommandException(reply.ErrorCode);
        if (reply?.ReceiptJson is not { Length: > 0 and <= 4096 } receiptJson ||
            JsonSerializer.Deserialize<FullTransferReceipt>(receiptJson, BackendAdaptation.Json) is not
                { Version: 1, State: "prepared", Width: 1280, Height: 736, Fps: 24, Frames: >= 56 and <= 192, Staged: false, Enqueued: false } receipt ||
            receipt.Id != BackendFullTransfer.JobId(form.ClientKey) || receipt.DraftTakeId != form.DraftTakeId ||
            receipt.Revision != baseline.Project.Revision || receipt.Fingerprint != baseline.Fingerprint ||
            !IsHash(receipt.DraftSha256) || !IsHash(receipt.RequestSha256) || !IsHash(receipt.ConfigSha256) || !IsHash(receipt.BuildSha256))
            throw new InvalidDataException("Invalid Full Transfer preparation receipt.");
        return receipt;
    }
}
