using System.Text.Json;
using System.Text.Json.Serialization;

namespace TrueMoon.Tales.Studio.Backend;

// Identifiers refer to saved project entities. Filesystem paths never enter the diagnostic payload.
internal sealed record BackendReadinessIssue(string Reason, long Revision, string? Fingerprint,
    string? ClipId = null, string? AssetId = null, string? Track = null, string? TrackId = null)
{
    private static readonly JsonSerializerOptions Json = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower,
        UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow,
        AllowDuplicateProperties = false
    };

    internal bool Matches(long revision, string? fingerprint) =>
        Revision == revision && Fingerprint is not null && string.Equals(Fingerprint, fingerprint, StringComparison.OrdinalIgnoreCase);

    internal string Encode() => JsonSerializer.Serialize(this, Json);

    internal static BackendReadinessIssue Decode(string value)
    {
        var issue = JsonSerializer.Deserialize<BackendReadinessIssue>(value, Json);
        if (issue is null || issue.Revision < 0 || !Identifier(issue.ClipId) || !Identifier(issue.AssetId) ||
            !Identifier(issue.TrackId) || (issue.Track is null) != (issue.TrackId is null) ||
            issue.Track is not (null or "music" or "sound_events") ||
            issue.Reason is not ("clip_take_missing" or "clip_timing" or "clip_source_range" or
                "asset_missing" or "asset_access" or "asset_invalid" or "asset_tools" or "legacy_root_overlap") ||
            (issue.Reason == "legacy_root_overlap"
                ? issue.Fingerprint is not null || issue.ClipId is not null || issue.AssetId is not null || issue.Track is not null
                : !BackendConnection.IsHash(issue.Fingerprint) || issue.ClipId is null && issue.AssetId is null))
            throw new InvalidDataException("Invalid export readiness issue.");
        return issue;
    }

    private static bool Identifier(string? value) => value is null ||
        value.Length is > 0 and <= 256 && !value.Any(char.IsControl);
}

internal sealed class BackendReadinessException(string exportCode, BackendReadinessIssue issue, Exception? inner = null)
    : IOException(issue.Reason, inner)
{
    internal string ExportCode { get; } = exportCode;
    internal BackendReadinessIssue Issue { get; } = issue;
}

internal sealed class DisabledLegacyOverlapException : Exception { }
