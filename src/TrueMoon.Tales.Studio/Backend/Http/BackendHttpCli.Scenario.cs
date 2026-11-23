using System.Net.Http.Json;
using System.Text.Json;

namespace TrueMoon.Tales.Studio.Backend.Http;

internal static partial class BackendHttpCli
{
    private const string ScenarioActionCommand = "backend-scenario-action-save";
    private const string ScenarioSegmentCommand = "backend-scenario-segment-save";
    private const string ScenarioVideoCommand = "backend-scenario-video-register";
    private const string MoveScenarioActionCommand = "backend-scenario-action-move";
    private const string LinkScenarioSourceCommand = "backend-scenario-action-link-source";

    private static bool IsScenarioActionCommand(string? command) =>
        command is ScenarioActionCommand or MoveScenarioActionCommand or LinkScenarioSourceCommand;

    private static bool TryScenarioActionRequest(string command, Dictionary<string, string> options, out HttpContent? content)
    {
        content = null;
        if (!TryBaseline(options, out var revision, out var fingerprint) || !options.Remove("--client-key", out var key)) return false;
        options.Remove("--actor", out var actor);
        var request = new ScenarioActionWrite { ExpectedRevision = revision, Fingerprint = fingerprint, ClientKey = key, Actor = actor ?? "author" };
        if (command is ScenarioActionCommand or ScenarioSegmentCommand or ScenarioVideoCommand)
        {
            if (!options.Remove("--file", out var path)) return false;
            try
            {
                var form = ReadAgentInput(path, ScenarioActionWrite.MaxBodyBytes);
                request = command switch
                {
                    ScenarioSegmentCommand => request with { Segment = JsonSerializer.Deserialize<ScenarioSegmentDraft>(form, AgentRequestDraft.Json) },
                    ScenarioVideoCommand => request with { Video = JsonSerializer.Deserialize<ImportVideo>(form, AgentRequestDraft.Json) },
                    _ => request with { Action = JsonSerializer.Deserialize<ScenarioActionDraft>(form, AgentRequestDraft.Json) }
                };
            }
            catch (Exception error) when (error is IOException or InvalidDataException or UnauthorizedAccessException or ArgumentException or JsonException or NotSupportedException)
            { return false; }
        }
        else
        {
            if (!options.Remove("--segment", out var segment) || !options.Remove("--action", out var action)) return false;
            if (command == MoveScenarioActionCommand)
            {
                options.Remove("--before", out var before);
                request = request with { Move = new(segment, action, before) };
            }
            else
            {
                if (!options.Remove("--source", out var source)) return false;
                options.Remove("--purpose", out var purpose);
                request = request with { Link = new(segment, action, source, purpose) };
            }
        }
        if (options.Count != 0 || !request.Valid) return false;
        content = JsonContent.Create(request, options: AgentRequestDraft.Json);
        return true;
    }
}
