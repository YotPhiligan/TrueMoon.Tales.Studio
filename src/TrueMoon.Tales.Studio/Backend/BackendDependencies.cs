using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using TrueMoon.Tales.Studio.Backend.Export;
using TrueMoon.Tales.Studio.Contracts.Projects;
using TrueMoon.Tales.Studio.Persistence;

namespace TrueMoon.Tales.Studio.Backend;

internal sealed record DependencyRow(string Kind, string Label, string Before, string After,
    string? SegmentId = null, string? ActionId = null, string? SourceId = null, string? TakeId = null, string? ClipId = null);
internal sealed record DependencyReport(long Revision, string Fingerprint, long ComparedRevision, string ComparedFingerprint,
    int Total, DependencyRow[] Rows, bool ExportSearchComplete)
{
    internal string Encode()
    {
        var report = this;
        while (true)
        {
            var json = JsonSerializer.Serialize(report, BackendAdaptation.Json);
            if (Encoding.Unicode.GetByteCount(json) <= 32 * 1024) return json;
            report = report with { Rows = report.Rows[..^1] };
        }
    }
}

internal static class BackendDependencies
{
    internal static DependencyReport Compare(ProjectFileDocument previous, ProjectFileDocument current,
        Func<(BackendExportStatus[] Jobs, bool Complete)> readExports, CancellationToken token)
    {
        var before = previous.Project;
        var after = current.Project;
        var rows = new List<DependencyRow>();
        var old = new ProjectInputs(before);
        var now = new ProjectInputs(after);
        var affectedTakes = new HashSet<(string Source, string Take, string Reference)>();
        var scopes = old.Scopes.Keys.Union(now.Scopes.Keys);
        foreach (var scope in scopes)
        {
            token.ThrowIfCancellationRequested();
            var label = now.Scopes.GetValueOrDefault(scope) ?? old.Scopes[scope];
            var left = old.Chapter(scope);
            var right = now.Chapter(scope);
            if (left != right)
                rows.Add(new("chapter", Short(label), DescribeText(left), DescribeText(right), scope.Segment, scope.Action));

            var prior = old.Assignments(scope).ToDictionary(row => row.Entry.Id);
            var next = now.Assignments(scope).ToDictionary(row => row.Entry.Id);
            foreach (var id in prior.Keys.Union(next.Keys))
            {
                var earlier = prior.GetValueOrDefault(id);
                var selected = next.GetValueOrDefault(id);
                if (earlier.Version == selected.Version) continue;
                rows.Add(new("reference", Short($"{label} · {selected.Entry?.Name ?? earlier.Entry.Name}"),
                    Short(earlier.Version?.Display), Short(selected.Version?.Display), scope.Segment, scope.Action));
                if (earlier.Version is null) continue;
                foreach (var sourceId in old.Sources(scope))
                foreach (var take in now.TakesBySource[sourceId])
                {
                    var input = BackendReferenceLibrary.Inputs(take).FirstOrDefault(row => row.ReferenceId == id);
                    if (input?.Version != earlier.Version || !affectedTakes.Add((sourceId, take.Id, id))) continue;
                    rows.Add(new("take_reference", Short(BackendScenarioEdit.TakeDisplayName(take)),
                        Short(input.Version.Display), Short(selected.Version?.Display), scope.Segment, scope.Action, sourceId, take.Id));
                }
            }
        }

        foreach (var source in after.Sources)
        foreach (var take in source.Takes)
        {
            if (BackendTakePairs.Read(take) is not { } pair) continue;
            var left = old.TakeReference(source.Id, pair.Draft.TakeId);
            var right = now.TakeReference(source.Id, pair.Draft.TakeId);
            if (left == right || right == pair.Draft.Reference) continue;
            rows.Add(new("take_draft", Short(BackendScenarioEdit.TakeDisplayName(take)),
                Short($"{pair.Draft.Name} · SHA {pair.Draft.Sha256[..12]}"), Short(Path.GetFileName(right)),
                SourceId: source.Id, TakeId: take.Id));
        }

        CompareSounds();
        var oldClips = before.Montage.Clips.ToDictionary(row => row.Id);
        var newClips = after.Montage.Clips.ToDictionary(row => row.Id);
        foreach (var id in oldClips.Keys.Union(newClips.Keys))
        {
            var left = oldClips.GetValueOrDefault(id);
            var right = newClips.GetValueOrDefault(id);
            if (left == right && old.Asset(left) == now.Asset(right)) continue;
            var clip = right ?? left!;
            var window = (right is null ? old : now).Windows.GetValueOrDefault(clip.WindowId);
            rows.Add(new("clip", Short((right is null ? old : now).ClipName(clip)), DescribeClip(old, left),
                DescribeClip(now, right), window?.SegmentId, clip.ActionId, clip.SourceId, clip.TakeId, right?.Id));
        }

        var montage = !JsonElement.DeepEquals(old.MontageInputs(), now.MontageInputs());
        if (montage) rows.Add(new("montage", "Монтаж и экспорт MP4", $"рев. {before.Revision}", $"рев. {after.Revision}"));
        if (montage || !SameCards(before, after))
            rows.Add(new("animatic", "Аниматик", $"рев. {before.Revision}", $"рев. {after.Revision}"));
        var exportsComplete = true;
        if (montage)
        {
            var (exports, complete) = readExports();
            exportsComplete = complete;
            foreach (var job in exports.OrderBy(row => row.Id, StringComparer.Ordinal))
                rows.Add(new("export", job.Id, $"рев. {job.ProjectRevision} · {job.Profile}", job.ArtifactSha256!));
        }
        return new(after.Revision, current.Fingerprint, before.Revision, previous.Fingerprint, rows.Count, rows.Take(80).ToArray(), exportsComplete);

        void CompareSounds()
        {
            var prior = Sounds(before);
            var next = Sounds(after);
            foreach (var id in prior.Keys.Union(next.Keys))
            {
                var a = prior.GetValueOrDefault(id);
                var b = next.GetValueOrDefault(id);
                if (JsonNode.DeepEquals(a, b) && old.SoundReference(a) == now.SoundReference(b)) continue;
                var sound = b ?? a!;
                var speech = BackendSpeech.Read(sound["speech"]);
                var owners = b is null ? old : now;
                var window = owners.Windows.GetValueOrDefault((string?)sound["segment_id"] ?? "");
                var clip = (string?)sound["clip_id"];
                rows.Add(new("sound", Short((string?)sound["name"] ?? id), DescribeSound(old, a), DescribeSound(now, b),
                    window?.SegmentId, speech?.ActionId, ClipId: clip));
            }
        }
    }

    private static bool SameCards(ProjectContract left, ProjectContract right)
    {
        try { return BackendAnimatic.Cards(left).SequenceEqual(BackendAnimatic.Cards(right)); }
        catch (BackendExportException) { return true; } // Readiness separately checks invalid animatic timing.
    }

    private static Dictionary<string, JsonObject> Sounds(ProjectContract project) =>
        BackendTrackEdits.Rows(project.Montage, "sound_events").OfType<JsonObject>()
            .Where(row => row["id"] is not null).ToDictionary(row => (string)row["id"]!);

    private static string DescribeSound(ProjectInputs inputs, JsonObject? row)
    {
        if (row is null) return "—";
        var speech = BackendSpeech.Read(row["speech"]);
        var label = speech is null ? (string?)row["asset_id"] :
            $"{speech.Speaker} · v{speech.SelectedVersion} · SHA {speech.Selected.Sha256[..12]}";
        return Short($"{label} · {Path.GetFileName(inputs.SoundReference(row))} · смещение {row["offset_frames"]} · {row["duration_frames"]} к.");
    }

    private static string DescribeText(ChapterFragment? fragment) => fragment is null ? "—" :
        Short($"{Path.GetFileName(fragment.Reference)} · строки {fragment.FirstLine}–{fragment.LastLine} · {fragment.Sha256[..12]}");

    private static string DescribeClip(ProjectInputs inputs, ClipContract? clip) => clip is null ? "—" :
        Short($"{inputs.ClipName(clip)} · {clip.StartFrame}–{(long)clip.StartFrame + clip.DurationFrames} к. · {Path.GetFileName(inputs.Asset(clip))}");

    private static string Short(string? value)
    {
        if (string.IsNullOrEmpty(value)) return "—";
        var line = string.Join(' ', value.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries));
        return line.Length <= 120 ? line : line[..(char.IsHighSurrogate(line[119]) ? 119 : 120)] + "…";
    }

    private sealed class ProjectInputs(ProjectContract project)
    {
        private readonly Dictionary<(string, string?), ChapterFragment> _chapters =
            BackendAdaptation.Read(project).Texts.ToDictionary(row => (row.SegmentId, row.ActionId), row => row.Fragment);
        private readonly Dictionary<string, ScenarioSegmentContract> _segments = project.Segments.ToDictionary(row => row.Id);

        internal Dictionary<(string Segment, string? Action), string> Scopes { get; } = project.Segments.SelectMany(segment =>
            segment.Actions.Select(action => ((segment.Id, (string?)action.Id), $"{segment.Name} / {action.Title}"))
                .DefaultIfEmpty(((segment.Id, null), segment.Name))).ToDictionary();
        internal Dictionary<string, SegmentWindowContract> Windows { get; } = project.Montage.Windows.ToDictionary(row => row.Id);
        internal ILookup<string, TakeContract> TakesBySource { get; } =
            project.Sources.SelectMany(source => source.Takes.Select(take => (source.Id, Take: take))).ToLookup(row => row.Id, row => row.Take);
        internal ReferenceLibrary References { get; } = BackendReferenceLibrary.Read(project);

        internal ChapterFragment? Chapter((string Segment, string? Action) scope) =>
            Scopes.ContainsKey(scope) ? _chapters.GetValueOrDefault(scope) ?? _chapters.GetValueOrDefault((scope.Segment, null)) : null;

        internal IEnumerable<(ReferenceEntry Entry, ReferenceVersion Version, bool Action)> Assignments((string Segment, string? Action) scope) =>
            Scopes.ContainsKey(scope) ? References.For(scope.Segment, scope.Action) : [];

        internal IEnumerable<string> Sources((string Segment, string? Action) scope) =>
            _segments.GetValueOrDefault(scope.Segment)?.Actions.FirstOrDefault(row => row.Id == scope.Action)?.Sources.Select(row => row.SourceId) ?? [];

        private TakeContract? Take(ClipContract clip) => TakesBySource[clip.SourceId].FirstOrDefault(row => row.Id == clip.TakeId);
        internal string ClipName(ClipContract clip) => Take(clip) is { } take ? BackendScenarioEdit.TakeDisplayName(take) : clip.Id;
        internal string? Asset(ClipContract? clip) => clip is not null && Take(clip)?.VideoAssetId is { } asset
            ? BackendScenarioEdit.TryReference(project, asset) : null;
        internal string? TakeReference(string source, string take) => TakesBySource[source].FirstOrDefault(row => row.Id == take)?.VideoAssetId is { } asset
            ? BackendScenarioEdit.TryReference(project, asset) : null;
        internal string? SoundReference(JsonObject? row) => (string?)row?["asset_id"] is { } asset
            ? BackendScenarioEdit.TryReference(project, asset) : null;

        internal JsonElement MontageInputs()
        {
            var properties = project.Montage.Properties;
            var audio = Sounds(project).Values.Select(row => (string?)row["asset_id"]).Append(
                (string?)BackendTrackEdits.Object(project.Montage, "music")?["asset_id"]).OfType<string>().Distinct(StringComparer.Ordinal);
            return JsonSerializer.SerializeToElement(new
            {
                project.Fps, outputSize = Get(project.Properties, "output_size"),
                windows = project.Montage.Windows.OrderBy(row => row.Id, StringComparer.Ordinal),
                clips = project.Montage.Clips.OrderBy(row => row.Id, StringComparer.Ordinal).Select(row => new { row, reference = Asset(row) }),
                audioFiles = audio.Order(StringComparer.Ordinal).Select(id => new { id, path = BackendScenarioEdit.TryReference(project, id) }),
                music = Get(properties, "music"), sounds = Get(properties, "sound_events"), titles = Get(properties, "titles"),
                transitions = Get(properties, "transitions"), clipAudio = Get(properties, "clip_audio")
            });
        }

        private static JsonElement? Get(IReadOnlyDictionary<string, JsonElement> values, string key) =>
            values.TryGetValue(key, out var value) ? value : null;
    }
}
