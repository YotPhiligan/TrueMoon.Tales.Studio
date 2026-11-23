using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using TrueMoon.Tales.Studio.Contracts.Projects;

namespace TrueMoon.Tales.Studio.Backend;

internal static class BackendBrief
{
    internal const string Property = "brief";
    internal const int MaxBodyBytes = 576 * 1024;
    private const int MaxText = 12_000;
    private static readonly string[] Fields = ["synopsis", "visual_style", "continuity_rules", "agent_notes"];

    internal static Dictionary<string, string> Read(ProjectContract project)
    {
        var stored = project.Properties.GetValueOrDefault(Property);
        if (stored.ValueKind is not (JsonValueKind.Undefined or JsonValueKind.Object))
            throw new ScenarioEditException("invalid_brief");
        var result = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var field in Fields)
        {
            var text = "";
            if (stored.ValueKind == JsonValueKind.Object && stored.TryGetProperty(field, out var value))
            {
                if (value.ValueKind != JsonValueKind.String || !ValidText(value.GetString()))
                    throw new ScenarioEditException("invalid_brief");
                text = value.GetString()!;
            }
            result.Add(field, text);
        }
        return result;
    }

    internal static bool ValidFields(Dictionary<string, string>? fields) => fields is { Count: > 0 } &&
        fields.All(pair => Fields.Contains(pair.Key, StringComparer.Ordinal) && ValidText(pair.Value));

    private static bool ValidText(string? text)
    {
        if (text is null || text.Length > 2 * MaxText) return false;
        return text.Length <= MaxText || text.EnumerateRunes().Count() <= MaxText;
    }

    internal static ProjectContract Apply(ProjectContract project, Dictionary<string, string> patch)
    {
        var current = Read(project);
        if (patch.All(pair => current[pair.Key] == pair.Value)) return project;

        var properties = new Dictionary<string, JsonElement>(project.Properties);
        // Preserve any historical fields that are outside the editable brief contract.
        var stored = properties.TryGetValue(Property, out var existing)
            ? existing.EnumerateObject().ToDictionary(member => member.Name, member => member.Value, StringComparer.Ordinal)
            : new Dictionary<string, JsonElement>(StringComparer.Ordinal);
        foreach (var (field, value) in patch) stored[field] = JsonSerializer.SerializeToElement(value, AgentRequestDraft.Json);
        properties[Property] = JsonSerializer.SerializeToElement(stored, AgentRequestDraft.Json);
        return project with { Properties = properties };
    }
}

internal sealed record BriefWrite
{
    internal const string ReceiptsProperty = "studio_brief_writes_v1";
    public required long ExpectedRevision { get; init; }
    public required string Fingerprint { get; init; }
    public required string ClientKey { get; init; }
    public required Dictionary<string, string> Brief { get; init; }
    public string Actor { get; init; } = "author";

    internal bool Valid => ExpectedRevision is >= 0 and < int.MaxValue && BackendConnection.IsHash(Fingerprint) &&
        AgentRequestDraft.ValidActor(Actor) && AgentRequestDraft.ValidClientKey(ClientKey) && BackendBrief.ValidFields(Brief);

    internal string KeyHash() => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(ClientKey)));

    internal string Digest() => Convert.ToHexString(SHA256.HashData(JsonSerializer.SerializeToUtf8Bytes(this with
    {
        Fingerprint = Fingerprint.ToUpperInvariant(), Actor = Actor.Trim(),
        Brief = Brief.OrderBy(pair => pair.Key, StringComparer.Ordinal).ToDictionary()
    }, AgentRequestDraft.Json)));

    internal static BriefReceipt? FindReceipt(ProjectContract project, string key)
    {
        if (!project.Properties.TryGetValue(ReceiptsProperty, out var entries)) return null;
        if (entries.ValueKind != JsonValueKind.Object) throw new JsonException("Invalid brief receipts.");
        if (!entries.TryGetProperty(key, out var entry)) return null;
        var receipt = entry.Deserialize<BriefReceipt>(AgentRequestDraft.Json);
        if (receipt is not { ApiVersion: 1, Changed: true } || receipt.ProjectId != project.Id ||
            receipt.ProjectRevision <= 0 || receipt.ProjectRevision > project.Revision || !BackendConnection.IsHash(receipt.SubmissionSha256))
            throw new JsonException("Invalid saved brief receipt.");
        return receipt;
    }

    internal static ProjectContract Record(ProjectContract project, string key, BriefReceipt receipt)
    {
        var properties = new Dictionary<string, JsonElement>(project.Properties);
        var entries = properties.TryGetValue(ReceiptsProperty, out var existing)
            ? existing.Deserialize<Dictionary<string, JsonElement>>(AgentRequestDraft.Json)!
            : new Dictionary<string, JsonElement>(StringComparer.Ordinal);
        entries.Add(key, JsonSerializer.SerializeToElement(receipt, AgentRequestDraft.Json));
        properties[ReceiptsProperty] = JsonSerializer.SerializeToElement(entries, AgentRequestDraft.Json);
        return project with { Properties = properties };
    }
}

internal sealed record BriefSnapshot(int ApiVersion, string ProjectId, long ProjectRevision, string Fingerprint, Dictionary<string, string> Brief)
{
    internal static void Validate(JsonElement json)
    {
        if (json.Deserialize<BriefSnapshot>(AgentRequestDraft.Json) is not { ApiVersion: 1, ProjectRevision: >= 0 } snapshot ||
            string.IsNullOrWhiteSpace(snapshot.ProjectId) || !BackendConnection.IsHash(snapshot.Fingerprint) ||
            snapshot.Brief?.Count != 4 || !BackendBrief.ValidFields(snapshot.Brief))
            throw new JsonException("Invalid project brief snapshot.");
    }
}

internal sealed record BriefReceipt(int ApiVersion, string ProjectId, long ProjectRevision, string SubmissionSha256,
    [property: JsonRequired] bool Changed)
{
    internal bool Matches(BriefWrite request) => ApiVersion == 1 && !string.IsNullOrWhiteSpace(ProjectId) &&
        ProjectRevision == request.ExpectedRevision + (Changed ? 1 : 0) && SubmissionSha256 == request.Digest();

    internal static void Validate(JsonElement json, BriefWrite request)
    {
        if (json.Deserialize<BriefReceipt>(AgentRequestDraft.Json) is not { } receipt || !receipt.Matches(request))
            throw new JsonException("Invalid project brief receipt.");
    }
}
