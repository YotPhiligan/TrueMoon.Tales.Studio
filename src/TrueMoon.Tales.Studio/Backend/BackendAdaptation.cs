using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using SkiaSharp;
using TrueMoon.Tales.Studio.Contracts.Projects;

namespace TrueMoon.Tales.Studio.Backend;

internal sealed record ChapterFragment(string Reference, string Sha256, int FirstLine, int LastLine, string Excerpt);
internal sealed record AdaptationText(string SegmentId, string? ActionId, ChapterFragment Fragment);
internal sealed record AnimaticStill(string SegmentId, string ActionId, string Reference);
internal sealed record AdaptationNotes(AdaptationText[] Texts, AnimaticStill[] Stills);

internal static class BackendAdaptation
{
    private const string Key = "studio_adaptation_v1";
    private const int MaxChapterBytes = 2 * 1024 * 1024;
    private const int MaxExcerpt = 4096;
    internal static readonly JsonSerializerOptions Json = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower,
        UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow,
        AllowDuplicateProperties = false
    };

    internal static AdaptationNotes Read(ProjectContract project) =>
        project.Properties.TryGetValue(Key, out var raw)
            ? raw.Deserialize<AdaptationNotes>(Json) is { Texts: not null, Stills: not null } notes ? notes
                : throw new InvalidDataException("Invalid adaptation notes.")
            : new([], []);

    internal static async Task<ChapterFragment> ReadTextAsync(BackendMediaResolver resolver, string reference, int first, int last,
        CancellationToken token, string? expectedSha = null)
    {
        if (reference is not { Length: > 7 and <= 512 } || !reference.StartsWith("@texts/", StringComparison.Ordinal) ||
            first < 1 || last < first || (long)last - first >= 80 ||
            !string.Equals(Path.GetExtension(reference), ".md", StringComparison.OrdinalIgnoreCase))
            throw new ScenarioEditException("invalid_text_reference");
        var path = resolver.Resolve(reference);
        if (!string.Equals(Path.GetExtension(path), ".md", StringComparison.OrdinalIgnoreCase))
            throw new ScenarioEditException("invalid_text_reference");
        await using var source = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, 16 * 1024, FileOptions.Asynchronous);
        if (source.Length is < 1 or > MaxChapterBytes) throw new ScenarioEditException("invalid_text_reference");
        var bytes = new byte[(int)source.Length];
        await source.ReadExactlyAsync(bytes, token).ConfigureAwait(false);
        if (source.ReadByte() != -1 || !string.Equals(path, resolver.Resolve(reference), StringComparison.Ordinal))
            throw new ScenarioEditException("text_changed");
        var sha = Convert.ToHexString(SHA256.HashData(bytes));
        if (expectedSha is not null && !string.Equals(expectedSha, sha, StringComparison.OrdinalIgnoreCase))
            throw new ScenarioEditException("text_changed");
        string[] lines;
        try
        {
            var text = new UTF8Encoding(false, true).GetString(bytes).TrimStart('\uFEFF');
            lines = text.ReplaceLineEndings("\n").Split('\n');
        }
        catch (DecoderFallbackException) { throw new ScenarioEditException("invalid_text_reference"); }
        if (last > lines.Length) throw new ScenarioEditException("invalid_text_range");
        var excerpt = string.Join('\n', lines[(first - 1)..last]);
        if (string.IsNullOrWhiteSpace(excerpt) || excerpt.Length > MaxExcerpt) throw new ScenarioEditException("invalid_text_range");
        return new(reference, sha, first, last, excerpt);
    }

    internal static async Task<ProjectContract> BindTextAsync(ProjectContract project, BindAdaptationText edit,
        BackendMediaResolver resolver, CancellationToken token)
    {
        RequireTarget(project, edit.SegmentId, edit.ActionId);
        var notes = Read(project);
        var previous = notes.Texts.SingleOrDefault(row => row.SegmentId == edit.SegmentId && row.ActionId == edit.ActionId);
        AdaptationText? binding = null;
        if (edit.Reference is { } reference)
        {
            if (!BackendConnection.IsHash(edit.TextSha256)) throw new ScenarioEditException("invalid_text_reference");
            var fragment = await ReadTextAsync(resolver, reference, edit.FirstLine, edit.LastLine, token, edit.TextSha256).ConfigureAwait(false);
            binding = new(edit.SegmentId, edit.ActionId, fragment);
        }
        if (previous == binding) return project;
        var next = notes.Texts.Where(row => row != previous).ToList();
        if (binding is not null) next.Add(binding);
        return Save(project, notes with { Texts = [.. next] });
    }

    internal static ProjectContract SetStill(ProjectContract project, SaveAnimaticStill edit, BackendMediaResolver resolver)
    {
        RequireTarget(project, edit.SegmentId, edit.ActionId);
        var notes = Read(project);
        var previous = notes.Stills.SingleOrDefault(row => row.SegmentId == edit.SegmentId && row.ActionId == edit.ActionId);
        AnimaticStill? still = null;
        if (edit.Reference is { } reference)
        {
            ResolveStill(resolver, reference);
            still = new(edit.SegmentId, edit.ActionId, reference);
        }
        if (previous == still) return project;
        var next = notes.Stills.Where(row => row != previous).ToList();
        if (still is not null) next.Add(still);
        return Save(project, notes with { Stills = [.. next] });
    }

    internal static string ResolveStill(BackendMediaResolver resolver, string reference)
    {
        if (reference is not { Length: > 2 and <= 512 } || reference[0] != '@') throw new ScenarioEditException("invalid_animatic_still");
        var path = resolver.Resolve(reference);
        if (Path.GetExtension(path).ToLowerInvariant() is not (".png" or ".jpg" or ".jpeg") ||
            new FileInfo(path).Length is < 1 or > 32 * 1024 * 1024)
            throw new ScenarioEditException("invalid_animatic_still");
        return path;
    }

    internal static bool ValidStill(SKCodec codec) => codec.EncodedFormat is SKEncodedImageFormat.Png or SKEncodedImageFormat.Jpeg &&
        codec.FrameCount <= 1 && codec.Info.Width is >= 1 and <= 8192 && codec.Info.Height is >= 1 and <= 8192 &&
        (long)codec.Info.Width * codec.Info.Height <= 16_000_000;

    private static void RequireTarget(ProjectContract project, string segmentId, string? actionId)
    {
        var segment = project.Segments.SingleOrDefault(row => row.Id == segmentId);
        if (segment is null || actionId is not null && !segment.Actions.Any(row => row.Id == actionId))
            throw new ScenarioEditException("invalid_scenario_edit");
    }

    private static ProjectContract Save(ProjectContract project, AdaptationNotes notes)
    {
        var properties = new Dictionary<string, JsonElement>(project.Properties);
        if (notes is { Texts.Length: 0, Stills.Length: 0 }) properties.Remove(Key);
        else properties[Key] = JsonSerializer.SerializeToElement(notes, Json);
        return project with { Properties = properties };
    }
}
