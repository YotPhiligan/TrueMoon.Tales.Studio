using System.Globalization;
using TrueMoon.Tales.Studio.Backend.Export;
using TrueMoon.Tales.Studio.Contracts.Projects;
using TrueMoon.Tales.Studio.Models;

namespace TrueMoon.Tales.Studio.Backend;

internal sealed record BackendRenderedMontage(string Key, int Start, int Frames, double Fps, int Revision, string Fingerprint,
    bool Bounded = false, bool Animatic = false)
{
    private const string Prefix = "review-";
    internal int FileStart => Bounded ? 0 : Start;
    internal static bool IsReview(string? id) => id is { Length: > 47 } &&
        id.StartsWith("review-export-", StringComparison.Ordinal) && id[46] == '-';
    internal static string Identifier(string job, int? start) => Prefix + job + "-" +
        (start?.ToString(CultureInfo.InvariantCulture) ?? "all");

    internal static (string Job, int? Start) Parse(string key)
    {
        var suffixOffset = Prefix.Length + 40;
        if (key.Length <= suffixOffset || key[suffixOffset - 1] != '-') throw new ArgumentException("Invalid rendered montage ID.");
        var job = key.Substring(Prefix.Length, 39);
        var scope = key[suffixOffset..];
        if (!BackendConnection.IsExportId(job)) throw new ArgumentException("Invalid rendered montage job.");
        if (scope == "all") return (job, null);
        if (!int.TryParse(scope, NumberStyles.None, CultureInfo.InvariantCulture, out var start))
            throw new ArgumentException("Invalid rendered montage scope.");
        return (job, start);
    }

    internal static BackendRenderedMontage Resolve(string key, ProjectContract project, BackendExportStatus job, int? start)
    {
        if (job.ProjectId != project.Id || job.Fps != project.Fps)
            throw new InvalidDataException("Rendered montage belongs to another project or time base.");
        if (job.Animatic)
        {
            if (start is not null) throw new InvalidDataException("The animatic is a whole timeline review.");
            return new(key, 0, job.Frames, job.Fps, checked((int)job.ProjectRevision), job.ProjectFingerprint, Animatic: true);
        }
        if (job.RangeStart is { } rangeStart)
        {
            if (start is not null) throw new InvalidDataException("A bounded review has no segment inside the rendered file.");
            return new(key, rangeStart, job.Frames, job.Fps, checked((int)job.ProjectRevision), job.ProjectFingerprint, Bounded: true);
        }
        var window = start is null ? null : project.Montage.Windows.SingleOrDefault(row => row.StartFrame == start);
        if (start is not null && window is null || window is not null && (long)window.StartFrame + window.DurationFrames > job.Frames)
            throw new InvalidDataException("The rendered segment is unavailable.");
        return new(key, window?.StartFrame ?? 0, window?.DurationFrames ?? job.Frames, job.Fps, checked((int)job.ProjectRevision),
            job.ProjectFingerprint);
    }

    internal SceneSnapshot Scene(double sourceIn = 0) => new("Предпросмотр", Revision, Fps,
        new(Key, "Сохранённый монтаж", Start, Frames),
        new(Key, Key, Key, Key, null, Start, Frames, sourceIn, sourceIn + Frames, 1),
        "Сохранённый монтаж", [new(Key, Key, new(Fps, Frames / Fps, Frames))], [], Fingerprint: Fingerprint);
}
