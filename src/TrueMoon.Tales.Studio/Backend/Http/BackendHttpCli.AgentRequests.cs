using System.Net.Http.Json;
using System.Text;
using System.Text.Json;

namespace TrueMoon.Tales.Studio.Backend.Http;

internal static partial class BackendHttpCli
{
    private static readonly UTF8Encoding AgentRequestEncoding = new(false, true);

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

    private static string ReadAgentInput(string path)
    {
        using var stream = File.OpenRead(path);
        if (stream.Length > AgentRequestDraft.MaxBodyBytes) throw new InvalidDataException("Agent request input file is too large.");
        var bytes = new byte[(int)stream.Length];
        stream.ReadExactly(bytes);
        var start = bytes.AsSpan().StartsWith("\uFEFF"u8) ? 3 : 0;
        return AgentRequestEncoding.GetString(bytes.AsSpan(start));
    }
}
