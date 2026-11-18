using System.ComponentModel;
using System.Text;
using TrueMoon.Tales.Studio.Contracts.Serialization;
using TrueMoon.Tales.Studio.Persistence;

namespace TrueMoon.Tales.Studio.Backend;

internal sealed partial class StudioBackendService
{
    public Task<BackendProjectReply> EditScenarioAsync(string sessionKey, string commandJson, long expectedRevision, string fingerprint,
        CancellationToken cancellationToken = default) => ExecuteAsync(sessionKey, async () =>
    {
        RequireWriter();
        var baseline = await ObserveAsync(expectedRevision, fingerprint, cancellationToken).ConfigureAwait(false);
        if (baseline.SchemaVersion != 2) throw new ProjectMigrationRequiredException();
        try
        {
            var command = ScenarioCommand.Decode(commandJson);
            var draft = await BackendScenarioEdit.ApplyAsync(baseline.Project, command, ProbeAsync, cancellationToken).ConfigureAwait(false);
            if (ReferenceEquals(draft, baseline.Project)) return Reply(baseline);
            if (draft.Revision >= int.MaxValue) throw new InvalidDataException("Revision exceeds the editor range.");
            if (Encoding.Unicode.GetByteCount(ProjectContractJson.Serialize(draft)) > MaxProjectBytes - 32)
                return Failure("project_too_large");
            return Reply(await store.SaveAsync(baseline, draft, expectedRevision, "studio:scenario", cancellationToken).ConfigureAwait(false));
        }
        catch (ScenarioEditException error) { return Failure(error.Code); }
        catch (Win32Exception) { return Failure("decoder_unavailable"); }
    }, cancellationToken);

    private async Task<BackendVideoInfo> ProbeAsync(string reference, CancellationToken token)
    {
        if (media is null) throw new ScenarioEditException("decoder_unavailable");
        try { return await media.ProbeReferenceAsync(reference, token).ConfigureAwait(false); }
        catch (InvalidDataException) { throw new ScenarioEditException("invalid_media"); }
    }
}
