using System.Net.Http.Json;
using System.Text.Json;

namespace TrueMoon.Tales.Studio.Backend.Http;

internal static partial class BackendHttpCli
{
    private const string ScenarioActionCommand = "backend-scenario-action-save";

    private static bool TryScenarioActionRequest(Dictionary<string, string> options, out HttpContent? content)
    {
        content = null;
        if (!TryBaseline(options, out var revision, out var fingerprint) ||
            !options.Remove("--file", out var path) || !options.Remove("--client-key", out var key)) return false;
        options.Remove("--actor", out var actor);
        if (options.Count != 0) return false;
        try
        {
            var action = JsonSerializer.Deserialize<ScenarioActionDraft>(ReadAgentInput(path, ScenarioActionWrite.MaxBodyBytes), AgentRequestDraft.Json);
            if (action is null) return false;
            var request = new ScenarioActionWrite
            {
                ExpectedRevision = revision, Fingerprint = fingerprint, ClientKey = key, Actor = actor ?? "author", Action = action
            };
            if (!request.Valid) return false;
            content = JsonContent.Create(request, options: AgentRequestDraft.Json);
            return true;
        }
        catch (Exception error) when (error is IOException or InvalidDataException or UnauthorizedAccessException or ArgumentException or JsonException or NotSupportedException)
        { return false; }
    }
}
