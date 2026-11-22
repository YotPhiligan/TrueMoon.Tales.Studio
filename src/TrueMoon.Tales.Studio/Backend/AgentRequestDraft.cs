using System.Security.Cryptography;
using System.Text.Encodings.Web;
using System.Text.Json;

namespace TrueMoon.Tales.Studio.Backend;

internal sealed record AgentRequestDraft
{
    internal const int MaxIpcChars = 30 * 1024;
    internal const int MaxBodyBytes = 160 * 1024;
    internal const int MaxText = 12_000;
    internal static readonly JsonSerializerOptions Json = new(BackendAdaptation.Json)
    {
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping
    };

    public required long ExpectedRevision { get; init; }
    public required string Fingerprint { get; init; }
    public required string ClientKey { get; init; }
    public required string Goal { get; init; }
    public string Mode { get; init; } = "propose";
    public string Actor { get; init; } = "author";
    public string? Block { get; init; }
    public AgentRequestScope? Scope { get; init; }

    internal bool Valid => ExpectedRevision >= 0 && BackendConnection.IsHash(Fingerprint) &&
        ClientKey is { Length: >= 16 and <= 80 } &&
        ClientKey.All(ch => char.IsAsciiLetterOrDigit(ch) || ch is '-' or '_') &&
        Goal is { Length: <= MaxText } && !string.IsNullOrWhiteSpace(Goal) &&
        Mode is "propose" or "apply" &&
        Actor is { Length: <= 80 } && !string.IsNullOrWhiteSpace(Actor) && !Actor.Any(char.IsControl) &&
        BackendAgentContext.ValidSelector(Block) && (Scope is null || Scope.Valid(Mode, Block));

    internal AgentRequestDraft Normalize() => this with
    {
        Fingerprint = Fingerprint.ToUpperInvariant(), Actor = Actor.Trim(),
        Scope = Scope is null ? null : Scope with { Components = Scope.EffectiveComponents }
    };

    internal string Digest() =>
        Convert.ToHexString(SHA256.HashData(JsonSerializer.SerializeToUtf8Bytes(Normalize(), Json)));
}

internal sealed record AgentRequestScope
{
    private static readonly string[] Options = ["description", "references", "guides", "prompt", "settings"];
    public required string Intent { get; init; }
    public required string SegmentId { get; init; }
    public string? ItemId { get; init; }
    public string[]? Components { get; init; }
    public AgentRequestBlockDraft? Draft { get; init; }

    internal string[] EffectiveComponents => Components ?? (Intent == "breakdown_segment" ? [] : Options);

    internal bool Valid(string mode, string? block)
    {
        if (SegmentId is null || !BackendAgentContext.ValidSelector(SegmentId) || !BackendAgentContext.ValidSelector(ItemId))
            return false;
        var components = EffectiveComponents;
        if (Intent == "breakdown_segment")
            return mode == "propose" && block is null && ItemId is null && Draft is null && components.Length == 0;
        if (components.Length is < 1 or > 5 || !components.All(Options.Contains) ||
            components.Distinct(StringComparer.Ordinal).Count() != components.Length)
            return false;
        return Intent switch
        {
            "improve_block" => ItemId is not null && Draft is null,
            "prepare_block" => block is null && ItemId is null && Draft is { Valid: true },
            _ => false
        };
    }
}

internal sealed record AgentRequestBlockDraft
{
    public required string Title { get; init; }
    public string Description { get; init; } = "";
    public int? DurationFrames { get; init; }
    public string? AfterItemId { get; init; }

    internal bool Valid => Title is { Length: <= 160 } && !string.IsNullOrWhiteSpace(Title) &&
        Description is { Length: <= AgentRequestDraft.MaxText } && DurationFrames is null or (>= 1 and <= 518400) &&
        BackendAgentContext.ValidSelector(AfterItemId);
}
