using System.Text;
using System.Text.Json;
using TrueMoon.Tales.Studio.Contracts.Projects;
using TrueMoon.Tales.Studio.Contracts.Serialization;
using TrueMoon.Tales.Studio.Persistence;

namespace TrueMoon.Tales.Studio.Backend;

internal sealed partial class StudioBackendService
{
    public Task<BackendAgentRequestsReply> PreviewAgentBreakdownAsync(string sessionKey, string requestId,
        CancellationToken cancellationToken = default) => ExecuteAsync<BackendAgentRequestsReply>(sessionKey, async () =>
    {
        var document = await store.OpenAsync(cancellationToken).ConfigureAwait(false);
        var (plan, _) = await ReadAgentBreakdownAsync(document, requestId, cancellationToken).ConfigureAwait(false);
        var json = JsonSerializer.SerializeToUtf8Bytes(plan, AgentRequestDraft.Json);
        return json.Length <= BackendAgentRequests.MaxResponseBytes ? new() { Json = json } : new() { ErrorCode = "agent_requests_too_large" };
    }, cancellationToken, code => new() { ErrorCode = code });

    public Task<BackendProjectReply> ApplyAgentBreakdownAsync(string sessionKey, string requestId, long expectedRevision, string fingerprint,
        string actor, CancellationToken cancellationToken = default) => ExecuteAsync(sessionKey, async () =>
    {
        RequireWriter();
        if (!AgentRequestDraft.ValidActor(actor)) return Failure("invalid_agent_breakdown");
        var baseline = await ObserveAsync(expectedRevision, fingerprint, cancellationToken).ConfigureAwait(false);
        var (plan, draft) = await ReadAgentBreakdownAsync(baseline, requestId, cancellationToken).ConfigureAwait(false);
        if (plan.Status == "applied") return Failure("agent_breakdown_already_applied");
        if (draft is null) return Failure("agent_breakdown_blocked");
        if (expectedRevision >= int.MaxValue)
            return Failure("invalid_agent_breakdown");
        if (Encoding.Unicode.GetByteCount(ProjectContractJson.Serialize(draft)) > MaxProjectBytes - 32)
            return Failure("project_too_large");
        return await SaveMontageAsync(baseline, draft, expectedRevision, actor.Trim(), cancellationToken).ConfigureAwait(false);
    }, cancellationToken);

    private async Task<(AgentBreakdownPlan Plan, ProjectContract? Draft)> ReadAgentBreakdownAsync(
        ProjectFileDocument document, string id, CancellationToken token)
    {
        if (!BackendAgentRequests.ValidId(id)) throw new ScenarioEditException("invalid_agent_request_id");
        if (document.SchemaVersion != 2) throw new ProjectMigrationRequiredException();
        (string SegmentId, AgentRequestProposalItem[] Items) proposal;
        try
        {
            proposal = await BackendAgentRequests.ReadBreakdownAsync(BackendProjectAccess.Read(options), document.Project.Id, id, token)
                .ConfigureAwait(false);
        }
        catch (BackendCommandException error) { throw new ScenarioEditException(error.Code); }
        catch (JsonException) { throw new ScenarioEditException("invalid_agent_request_record"); }
        return BackendAgentBreakdown.Calculate(document, id, proposal.SegmentId, proposal.Items);
    }
}
