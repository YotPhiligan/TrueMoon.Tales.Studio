using System.Security.Cryptography;
using System.Text.Json;
using SkiaSharp;
using TrueMoon.Tales.Studio.Contracts.Projects;

namespace TrueMoon.Tales.Studio.Backend;

internal sealed record ReferenceVersion(int Number, string Label, string ImageReference, string Sha256)
{
    internal string Display => $"v{Number} · {Label}";
}

internal sealed record ReferenceEntry(string Id, string Name, string Kind, ReferenceVersion[] Versions);
internal sealed record ReferenceAssignment(string SegmentId, string? ActionId, string ReferenceId, int Version);
internal sealed record TakeReferenceInput(string ReferenceId, string Name, string Kind, ReferenceVersion Version);

internal sealed record ReferenceLibrary(ReferenceEntry[] Entries, ReferenceAssignment[] Assignments)
{
    internal IEnumerable<(ReferenceEntry Entry, ReferenceVersion Version, bool Action)> For(string segmentId, string? actionId) =>
        from assignment in Assignments
        where assignment.SegmentId == segmentId && (assignment.ActionId is null || assignment.ActionId == actionId)
        group assignment by assignment.ReferenceId into candidates
        let selected = candidates.FirstOrDefault(row => row.ActionId is not null) ?? candidates.First()
        join entry in Entries on selected.ReferenceId equals entry.Id
        from version in entry.Versions
        where version.Number == selected.Version
        select (entry, version, selected.ActionId is not null);
}

internal static class BackendReferenceLibrary
{
    private const string Key = "studio_reference_library_v1";
    private const string InputKey = "studio_reference_inputs_v1";
    private const int MaxImageBytes = 32 * 1024 * 1024;

    internal static bool IsKind(string? kind) => kind is "character" or "costume" or "location" or "prop" or "style";

    internal static ReferenceLibrary Read(ProjectContract project)
    {
        if (!project.Properties.TryGetValue(Key, out var raw)) return new([], []);
        return raw.Deserialize<ReferenceLibrary>(BackendAdaptation.Json) is { Entries: not null, Assignments: not null } library &&
            library.Entries.All(entry => entry is { Id.Length: > 0, Name.Length: > 0, Versions.Length: > 0 } &&
                IsKind(entry.Kind) && entry.Versions.All(Valid) &&
                entry.Versions.Select(row => row.Number).SequenceEqual(Enumerable.Range(1, entry.Versions.Length))) &&
            library.Entries.Select(entry => entry.Id).Distinct(StringComparer.Ordinal).Count() == library.Entries.Length &&
            library.Assignments.All(row => row is { SegmentId.Length: > 0, ReferenceId.Length: > 0, Version: > 0 }) &&
            library.Assignments.Select(row => (row.SegmentId, row.ActionId, row.ReferenceId)).Distinct().Count() == library.Assignments.Length
            ? library : throw new InvalidDataException("Invalid reference library.");
    }

    internal static TakeReferenceInput[] Inputs(TakeContract? take)
    {
        if (take is null || !take.Properties.TryGetValue(InputKey, out var raw)) return [];
        return raw.Deserialize<TakeReferenceInput[]>(BackendAdaptation.Json) is { } inputs &&
            inputs.All(row => row is { ReferenceId.Length: > 0, Name.Length: > 0 } && IsKind(row.Kind) && Valid(row.Version)) &&
            inputs.Select(row => row.ReferenceId).Distinct(StringComparer.Ordinal).Count() == inputs.Length
            ? inputs : throw new InvalidDataException("Invalid take reference inputs.");
    }

    internal static async Task<ProjectContract> AddAsync(ProjectContract project, AddReferenceVersion edit,
        BackendMediaResolver resolver, CancellationToken token)
    {
        var library = Read(project);
        var label = Required(edit.Label, 80);
        var image = Required(edit.ImageReference, 512);
        var sha = await ReadImageHashAsync(resolver, image, token).ConfigureAwait(false);
        var entry = edit.ReferenceId is { } id ? GetEntry(library, id) : null;
        if (entry is null)
        {
            var name = Required(edit.Name, 100);
            if (!IsKind(edit.Kind)) throw new ScenarioEditException("invalid_reference");
            var existing = library.Entries.FirstOrDefault(row => row.Kind == edit.Kind &&
                string.Equals(row.Name, name, StringComparison.OrdinalIgnoreCase));
            if (existing is not null)
            {
                if (Same(existing.Versions[0], label, image, sha)) return project;
                throw new ScenarioEditException("reference_name_exists");
            }
            entry = new(Guid.NewGuid().ToString("N"), name, edit.Kind!, [new(1, label, image, sha)]);
            return Save(project, library with { Entries = [.. library.Entries, entry] });
        }

        if (edit.Name is not null || edit.Kind is not null) throw new ScenarioEditException("invalid_reference");
        if (entry.Versions.Any(version => Same(version, label, image, sha))) return project;
        if (entry.Versions.Any(version => version.Sha256 == sha ||
            string.Equals(version.ImageReference, image, StringComparison.OrdinalIgnoreCase)))
            throw new ScenarioEditException("reference_version_exists");
        var updated = entry with { Versions = [.. entry.Versions, new(entry.Versions.Length + 1, label, image, sha)] };
        return Save(project, library with { Entries = library.Entries.Select(row => row == entry ? updated : row).ToArray() });
    }

    internal static ProjectContract Assign(ProjectContract project, AssignSceneReference edit)
    {
        var segment = project.Segments.FirstOrDefault(row => row.Id == edit.SegmentId);
        if (segment is null || edit.ActionId is { } actionId && !segment.Actions.Any(row => row.Id == actionId))
            throw new ScenarioEditException("invalid_reference_target");
        var library = Read(project);
        var entry = GetEntry(library, edit.ReferenceId);
        var previous = library.Assignments.FirstOrDefault(row =>
            (row.SegmentId, row.ActionId, row.ReferenceId) == (edit.SegmentId, edit.ActionId, edit.ReferenceId));
        if (previous?.Version == edit.Version) return project;
        var assignments = library.Assignments.Where(row => row != previous).ToList();
        if (edit.Version is { } number)
        {
            GetVersion(entry, number);
            assignments.Add(new(edit.SegmentId, edit.ActionId, entry.Id, number));
        }
        return Save(project, library with { Assignments = [.. assignments] });
    }

    internal static async Task<ProjectContract> RecordInputAsync(ProjectContract project, RecordTakeReference edit,
        BackendMediaResolver resolver, CancellationToken token)
    {
        var source = project.Sources.FirstOrDefault(row => row.Id == edit.SourceId);
        var take = source?.Takes.FirstOrDefault(row => row.Id == edit.TakeId);
        if (source is null || take is null) throw new ScenarioEditException("invalid_reference_target");
        var entry = GetEntry(Read(project), edit.ReferenceId);
        var version = GetVersion(entry, edit.Version);
        var inputs = Inputs(take);
        var previous = inputs.FirstOrDefault(row => row.ReferenceId == entry.Id);
        if (previous is not null)
        {
            if (previous.Version == version) return project;
            throw new ScenarioEditException("reference_input_recorded");
        }
        if (await ReadImageHashAsync(resolver, version.ImageReference, token).ConfigureAwait(false) != version.Sha256)
            throw new ScenarioEditException("reference_changed");

        var properties = new Dictionary<string, JsonElement>(take.Properties)
        {
            [InputKey] = JsonSerializer.SerializeToElement<TakeReferenceInput[]>(
                [.. inputs, new(entry.Id, entry.Name, entry.Kind, version)], BackendAdaptation.Json)
        };
        var updated = source with { Takes = source.Takes.Select(row => row == take ? row with { Properties = properties } : row).ToArray() };
        return project with { Sources = project.Sources.Select(row => row == source ? updated : row).ToArray() };
    }

    private static async Task<string> ReadImageHashAsync(BackendMediaResolver resolver, string reference, CancellationToken token)
    {
        var path = BackendAdaptation.ResolveStill(resolver, reference);
        await using var source = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, 16 * 1024, FileOptions.Asynchronous);
        if (source.Length is < 1 or > MaxImageBytes) throw new ScenarioEditException("invalid_animatic_still");
        var sha = Convert.ToHexString(await SHA256.HashDataAsync(source, token).ConfigureAwait(false));
        source.Position = 0;
        using var codec = SKCodec.Create(source);
        if (codec is null || !BackendAdaptation.ValidStill(codec) || !string.Equals(BackendAdaptation.ResolveStill(resolver, reference), path,
            StringComparison.OrdinalIgnoreCase)) throw new ScenarioEditException("invalid_animatic_still");
        return sha;
    }

    private static bool Same(ReferenceVersion version, string label, string image, string sha) =>
        (version.Label, version.ImageReference, version.Sha256) == (label, image, sha);

    private static bool Valid(ReferenceVersion? version) => version is { Number: > 0, Label.Length: > 0, ImageReference.Length: > 0 } &&
        BackendConnection.IsHash(version.Sha256);

    private static string Required(string? value, int max) => value?.Trim() is { Length: > 0 } text &&
        text.Length <= max && !text.Any(char.IsControl) ? text : throw new ScenarioEditException("invalid_reference");

    private static ReferenceEntry GetEntry(ReferenceLibrary library, string id) =>
        library.Entries.FirstOrDefault(row => row.Id == id) ?? throw new ScenarioEditException("invalid_reference_target");

    private static ReferenceVersion GetVersion(ReferenceEntry entry, int number) =>
        entry.Versions.FirstOrDefault(row => row.Number == number) ?? throw new ScenarioEditException("invalid_reference_target");

    private static ProjectContract Save(ProjectContract project, ReferenceLibrary library)
    {
        var properties = new Dictionary<string, JsonElement>(project.Properties)
        { [Key] = JsonSerializer.SerializeToElement(library, BackendAdaptation.Json) };
        return project with { Properties = properties };
    }
}
