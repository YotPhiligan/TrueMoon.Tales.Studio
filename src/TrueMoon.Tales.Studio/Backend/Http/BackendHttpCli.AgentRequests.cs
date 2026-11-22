using System.Globalization;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;

namespace TrueMoon.Tales.Studio.Backend.Http;

internal static partial class BackendHttpCli
{
    private static readonly UTF8Encoding AgentRequestEncoding = new(false, true);
    private const string AgentUpdatePrefix = "backend-agent-request-";
    private const string ClaimAgentRequestCommand = AgentUpdatePrefix + "claim";
    private const string FinishAgentRequestCommand = AgentUpdatePrefix + "finish";
    private const string FailAgentRequestCommand = AgentUpdatePrefix + "fail";
    private const string CancelAgentRequestCommand = AgentUpdatePrefix + "cancel";

    private static bool IsAgentRequestCommand(string? command) => command is AgentRequestsCommand or AgentRequestCommand or
        CreateAgentRequestCommand or ClaimAgentRequestCommand or FinishAgentRequestCommand or FailAgentRequestCommand or CancelAgentRequestCommand;

    private static bool TryAgentRequestCommand(string command, Dictionary<string, string> options, out string route, out HttpContent? content)
    {
        route = BackendHttpServer.AgentRequestsRoute;
        content = null;
        if (command == AgentRequestsCommand) return options.Count == 0;
        if (command == CreateAgentRequestCommand) return TryCreateAgentRequest(options, out content);
        if (!options.Remove("--id", out var id) || !BackendAgentRequests.ValidId(id)) return false;
        route += "/" + id;
        return command == AgentRequestCommand ? options.Count == 0 : TryUpdateAgentRequest(command[AgentUpdatePrefix.Length..], options, out content);
    }

    private static string? AgentRequestReconciliationHint(HttpRequestMessage request, object? payload) => payload switch
    {
        AgentRequestDraft draft => $" Проверьте задание {BackendAgentRequests.RequestId(draft.ClientKey)} через backend-agent-request. Для явного повтора сохраните тот же client_key и исходные параметры.",
        AgentRequestUpdate => $" Перечитайте задание {request.RequestUri!.Segments[^1]} через backend-agent-request и проверьте его состояние и ревизию.",
        _ => null
    };

    private static bool TryUpdateAgentRequest(string action, Dictionary<string, string> options, out HttpContent? content)
    {
        content = null;
        if (!options.Remove("--revision", out var revisionText) ||
            !long.TryParse(revisionText, NumberStyles.None, CultureInfo.InvariantCulture, out var revision)) return false;
        options.Remove("--actor", out var actor);
        options.Remove("--result-file", out var resultPath);
        options.Remove("--target-file", out var targetPath);
        options.Remove("--proposal-file", out var proposalPath);
        if (options.Count != 0 || action == "claim" && resultPath is not null ||
            action is ("finish" or "fail") && resultPath is null ||
            action != "finish" && (targetPath is not null || proposalPath is not null)) return false;
        try
        {
            var target = targetPath is null ? null : JsonSerializer.Deserialize<AgentRequestResultTarget>(ReadAgentInput(targetPath), AgentRequestDraft.Json);
            var proposal = proposalPath is null ? null : JsonSerializer.Deserialize<AgentRequestProposalItem[]>(
                ReadAgentInput(proposalPath, AgentRequestUpdate.MaxBodyBytes), AgentRequestDraft.Json);
            if (targetPath is not null && target is null || proposalPath is not null && proposal is null) return false;
            var update = new AgentRequestUpdate
            {
                ExpectedRevision = revision, Action = action, Actor = actor ?? "agent:cli",
                Result = resultPath is null ? "" : ReadAgentInput(resultPath, AgentRequestUpdate.MaxBodyBytes),
                ResultTarget = target, Proposal = proposal
            };
            if (!update.Valid) return false;
            content = JsonContent.Create(update, options: AgentRequestDraft.Json);
            return true;
        }
        catch (Exception error) when (error is IOException or InvalidDataException or UnauthorizedAccessException or ArgumentException or JsonException or NotSupportedException)
        { return false; }
    }

    private static bool TryCreateAgentRequest(Dictionary<string, string> options, out HttpContent? content)
    {
        content = null;
        if (!TryBaseline(options, out var revision, out var fingerprint) ||
            !options.Remove("--goal-file", out var goalPath) || !options.Remove("--client-key", out var key))
            return false;
        options.Remove("--mode", out var mode);
        options.Remove("--block", out var block);
        options.Remove("--actor", out var actor);
        options.Remove("--scope-file", out var scopePath);
        if (options.Count != 0) return false;
        try
        {
            var scope = scopePath is null ? null : JsonSerializer.Deserialize<AgentRequestScope>(ReadAgentInput(scopePath), AgentRequestDraft.Json);
            if (scopePath is not null && scope is null) return false;
            var request = new AgentRequestDraft
            {
                ExpectedRevision = revision, Fingerprint = fingerprint, ClientKey = key, Goal = ReadAgentInput(goalPath),
                Mode = mode ?? "propose", Actor = actor ?? "author", Block = block, Scope = scope
            };
            if (!request.Valid) return false;
            content = JsonContent.Create(request, options: AgentRequestDraft.Json);
            return true;
        }
        catch (Exception error) when (error is IOException or InvalidDataException or UnauthorizedAccessException or ArgumentException or JsonException or NotSupportedException)
        { return false; }
    }

    private static string ReadAgentInput(string path, int maxBytes = AgentRequestDraft.MaxBodyBytes)
    {
        using var stream = File.OpenRead(path);
        if (stream.Length > maxBytes) throw new InvalidDataException("Agent request input file is too large.");
        var bytes = new byte[(int)stream.Length];
        stream.ReadExactly(bytes);
        var start = bytes.AsSpan().StartsWith("\uFEFF"u8) ? 3 : 0;
        return AgentRequestEncoding.GetString(bytes.AsSpan(start));
    }
}
