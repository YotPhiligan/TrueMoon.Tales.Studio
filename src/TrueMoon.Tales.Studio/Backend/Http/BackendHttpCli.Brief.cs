using System.Net.Http.Json;
using System.Text.Json;

namespace TrueMoon.Tales.Studio.Backend.Http;

internal static partial class BackendHttpCli
{
    private const string BriefCommand = "backend-brief";
    private const string UpdateBriefCommand = "backend-brief-update";

    private static bool TryBriefRequest(string command, Dictionary<string, string> options, out HttpContent? content)
    {
        content = null;
        if (command == BriefCommand) return options.Count == 0;
        if (!TryBaseline(options, out var revision, out var fingerprint) ||
            !options.Remove("--file", out var path) || !options.Remove("--client-key", out var key)) return false;
        options.Remove("--actor", out var actor);
        if (options.Count != 0) return false;
        try
        {
            var patch = JsonSerializer.Deserialize<Dictionary<string, string>>(ReadAgentInput(path, BackendBrief.MaxBodyBytes), AgentRequestDraft.Json);
            if (patch is null) return false;
            var write = new BriefWrite { ExpectedRevision = revision, Fingerprint = fingerprint, ClientKey = key, Actor = actor ?? "author", Brief = patch };
            if (!write.Valid) return false;
            content = JsonContent.Create(write, options: AgentRequestDraft.Json);
            return true;
        }
        catch (Exception error) when (error is IOException or InvalidDataException or UnauthorizedAccessException or ArgumentException or JsonException or NotSupportedException)
        { return false; }
    }
}
