using System.Text;
using System.Text.Json;
using TrueMoon.Tales.Studio.Contracts.Serialization;
using TrueMoon.Tales.Studio.Persistence;

namespace TrueMoon.Tales.Studio.Backend;

internal sealed partial class StudioBackendService
{
    public Task<BackendAgentRequestsReply> ReadBriefAsync(string sessionKey, CancellationToken cancellationToken = default) =>
        ExecuteAsync<BackendAgentRequestsReply>(sessionKey, async () =>
        {
            var current = await store.OpenAsync(cancellationToken).ConfigureAwait(false);
            if (current.SchemaVersion != 2) throw new ProjectMigrationRequiredException();
            var snapshot = new BriefSnapshot(1, current.Project.Id, current.Project.Revision, current.Fingerprint, BackendBrief.Read(current.Project));
            return new() { Json = JsonSerializer.SerializeToUtf8Bytes(snapshot, AgentRequestDraft.Json) };
        }, cancellationToken, BriefFailure);

    public Task<BackendAgentRequestsReply> UpdateBriefAsync(string sessionKey, ReadOnlyMemory<byte> formJson,
        CancellationToken cancellationToken = default) => ExecuteAsync<BackendAgentRequestsReply>(sessionKey, async () =>
    {
        RequireWriter();
        if (formJson.Length is < 1 or > BackendBrief.MaxBodyBytes) return BriefFailure("brief_input_too_large");
        if (JsonSerializer.Deserialize<BriefWrite>(formJson.Span, AgentRequestDraft.Json) is not { Valid: true } write)
            return BriefFailure("invalid_brief_patch");

        var baseline = await store.OpenAsync(cancellationToken).ConfigureAwait(false);
        if (baseline.SchemaVersion != 2) throw new ProjectMigrationRequiredException();
        var key = write.KeyHash();
        if (BriefWrite.FindReceipt(baseline.Project, key) is { } prior)
            return prior.Matches(write) ? Reply(prior) : BriefFailure("brief_key_conflict");
        if (baseline.Project.Revision != write.ExpectedRevision ||
            !string.Equals(baseline.Fingerprint, write.Fingerprint, StringComparison.OrdinalIgnoreCase))
            return BriefFailure("revision_conflict");

        var draft = BackendBrief.Apply(baseline.Project, write.Brief);
        var changed = !ReferenceEquals(draft, baseline.Project);
        var receipt = new BriefReceipt(1, draft.Id, write.ExpectedRevision + (changed ? 1 : 0), write.Digest(), changed);
        if (!changed) return Reply(receipt);

        draft = BriefWrite.Record(draft, key, receipt);
        if (Encoding.Unicode.GetByteCount(ProjectContractJson.Serialize(draft)) > MaxProjectBytes - 32)
            return BriefFailure("project_too_large");
        await store.SaveAsync(baseline, draft, write.ExpectedRevision, write.Actor.Trim(), cancellationToken).ConfigureAwait(false);
        return Reply(receipt);

        static BackendAgentRequestsReply Reply(BriefReceipt receipt) => new() { Json = JsonSerializer.SerializeToUtf8Bytes(receipt, AgentRequestDraft.Json) };
    }, cancellationToken, BriefFailure);

    private static BackendAgentRequestsReply BriefFailure(string code) => new() { ErrorCode = code };
}
