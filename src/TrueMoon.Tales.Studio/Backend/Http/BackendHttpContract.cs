using TrueMoon.Tales.Studio.Contracts.Projects;

namespace TrueMoon.Tales.Studio.Backend.Http;

internal sealed record BackendHttpSnapshot(int ApiVersion, int SchemaVersion, string Fingerprint, bool ScenarioActive, ProjectContract Project)
{
    internal static BackendHttpSnapshot From(OpenedBackendProject opened) =>
        new(1, opened.SchemaVersion, opened.Fingerprint, opened.ScenarioActive, opened.Project);
}

internal sealed record BackendHttpFailure(string Error, string Message, bool ReconciliationRequired = false);

internal sealed record BackendHttpMediaSnapshot(int ApiVersion, int SchemaVersion, BackendMediaReply Media);

internal class BackendHttpMediaQuery
{
    public required string AssetId { get; init; }
    public required long ExpectedRevision { get; init; }
    public required string Fingerprint { get; init; }
    internal bool Valid => !string.IsNullOrWhiteSpace(AssetId) && AssetId.Length <= 160 && ExpectedRevision >= 0 &&
        Fingerprint is { Length: 64 } && Fingerprint.All(Uri.IsHexDigit);
}

internal sealed class BackendHttpFrameQuery : BackendHttpMediaQuery
{
    public required int Frame { get; init; }
}

internal sealed class BackendHttpMove
{
    public required string ClipId { get; init; }
    public required int StartFrame { get; init; }
    public required long ExpectedRevision { get; init; }
    public required string Fingerprint { get; init; }

    internal bool Valid => !string.IsNullOrWhiteSpace(ClipId) && ClipId.Length <= 160 && ExpectedRevision >= 0 &&
        Fingerprint is { Length: 64 } && Fingerprint.All(Uri.IsHexDigit);
}

internal sealed class BackendHttpTrim
{
    public required string ClipId { get; init; }
    public required double SourceInFrame { get; init; }
    public required double SourceOutFrame { get; init; }
    public required int StartFrame { get; init; }
    public required long ExpectedRevision { get; init; }
    public required string Fingerprint { get; init; }

    internal bool Valid => !string.IsNullOrWhiteSpace(ClipId) && ClipId.Length <= 160 && !ClipId.Contains('\0') &&
        double.IsFinite(SourceInFrame) && double.IsFinite(SourceOutFrame) && SourceInFrame >= 0 && SourceOutFrame > SourceInFrame &&
        StartFrame >= 0 && ExpectedRevision >= 0 && Fingerprint is { Length: 64 } && Fingerprint.All(Uri.IsHexDigit);
}
