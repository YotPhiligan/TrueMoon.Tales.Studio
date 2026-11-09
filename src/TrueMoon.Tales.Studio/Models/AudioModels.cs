namespace TrueMoon.Tales.Studio.Models;

internal sealed record AudioMusic(string AssetId, int StartFrame, int DurationFrames, double SourceInFrame, double Gain, bool Muted);
internal sealed record AudioEvent(string AssetId, string SegmentId, string? ClipId, int OffsetFrames, int DurationFrames,
    double SourceInFrame, int Stream, double Gain, bool Muted);
internal sealed record AudioSlice(string AssetId, int Stream, double SourceSeconds, double SourceDuration,
    int OutputSample, int Samples, double Rate, double Gain, bool Optional = false);
