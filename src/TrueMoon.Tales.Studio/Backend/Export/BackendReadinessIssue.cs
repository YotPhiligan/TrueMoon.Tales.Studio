using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text;

namespace TrueMoon.Tales.Studio.Backend;

// Identifiers refer to saved project entities. Filesystem paths never enter the diagnostic payload.
internal sealed record BackendReadinessIssue(string Reason, long Revision, string? Fingerprint,
    string? ClipId = null, string? AssetId = null, string? Track = null, string? TrackId = null, int? Frame = null,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] string? WindowId = null)
{
    internal static readonly JsonSerializerOptions Json = new()
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
        return issue is { Valid: true } ? issue : throw new InvalidDataException("Invalid export readiness issue.");
    }

    internal bool EpisodeNote => Reason?.StartsWith("episode_", StringComparison.Ordinal) == true;

    internal bool Valid => Revision >= 0 && Frame is not < 0 && Identifier(ClipId) && Identifier(AssetId) && Identifier(TrackId) &&
        Identifier(WindowId) && (WindowId is null || EpisodeNote) &&
        (Track is null) == (TrackId is null) && (Track is null or "music" or "sound_events") &&
        (Reason switch
        {
            "legacy_root_overlap" => NoTarget && (Fingerprint is null || BackendConnection.IsHash(Fingerprint)),
            "montage_invalid" or "montage_empty" or "title_font" => NoTarget && BackendConnection.IsHash(Fingerprint),
            "clip_take_missing" or "clip_timing" or "clip_source_range" => ClipId is not null && BackendConnection.IsHash(Fingerprint),
            "asset_missing" or "asset_access" or "asset_invalid" or "asset_tools" or "speech_source_changed" or "subtitle_review"
                => AssetId is not null && BackendConnection.IsHash(Fingerprint),
            "episode_duration" or "episode_voice_missing" or "episode_join_data_invalid"
                => NoTarget && WindowId is null && Frame is null && BackendConnection.IsHash(Fingerprint),
            "episode_window_without_video" or "episode_join_states_missing" or "episode_join_bridge_missing"
                => NoTarget && WindowId is not null && Frame is not null && BackendConnection.IsHash(Fingerprint),
            _ => false
        });

    private bool NoTarget => ClipId is null && AssetId is null && Track is null;

    private static bool Identifier(string? value) => value is null ||
        value.Length is > 0 and <= 256 && !value.Any(char.IsControl);
}

internal sealed record BackendReadinessReport(long Revision, string Fingerprint, int Total, BackendReadinessIssue[] Issues,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] BackendEpisodeSummary? Episode = null)
{
    internal const int MaxRows = 24;

    internal string Encode()
    {
        var report = this;
        while (true)
        {
            var json = JsonSerializer.Serialize(report, BackendReadinessIssue.Json);
            if (Encoding.Unicode.GetByteCount(json) <= 16 * 1024) return json;
            report = report with { Issues = report.Issues[..^1] };
        }
    }

    internal static BackendReadinessReport Decode(string value, long revision, string fingerprint, bool episode = false)
    {
        var report = JsonSerializer.Deserialize<BackendReadinessReport>(value, BackendReadinessIssue.Json);
        if (report is null || report.Revision != revision || !string.Equals(report.Fingerprint, fingerprint, StringComparison.OrdinalIgnoreCase) ||
            report.Issues is null || report.Total < report.Issues.Length || report.Issues.Length > MaxRows ||
            episode != (report.Episode is not null) || report.Episode is { } summary && (!summary.Valid || summary.Notes > report.Total) ||
            report.Issues.Any(row => row is not { Valid: true } || !row.Matches(revision, fingerprint) || !episode && row.EpisodeNote))
            throw new InvalidDataException("Invalid readiness report.");
        return report;
    }
}

internal sealed class BackendReadinessException(string exportCode, BackendReadinessIssue issue, Exception? inner = null)
    : IOException(issue.Reason, inner)
{
    internal string ExportCode { get; } = exportCode;
    internal BackendReadinessIssue Issue { get; } = issue;
}

internal sealed class DisabledLegacyOverlapException : Exception { }
