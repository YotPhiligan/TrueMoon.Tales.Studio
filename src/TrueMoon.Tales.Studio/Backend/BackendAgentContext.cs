using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using TrueMoon.Tales.Studio.Contracts.Projects;
using TrueMoon.Tales.Studio.Contracts.Serialization;
using TrueMoon.Tales.Studio.Persistence;

namespace TrueMoon.Tales.Studio.Backend;

internal sealed record AgentContextSelection(string Kind, string Id, string[] SourceIds,
    AgentContextAction[] Actions, AgentContextPlacement[] Placements, AgentContextContinuation[] Continuations);
internal sealed record AgentContextAction(string SegmentId, string ActionId, string? PreviousActionId, string? NextActionId);
internal sealed record AgentContextPlacement(string ClipId, string? PreviousClipId, string? NextClipId);
internal sealed record AgentContextContinuation(string SourceId, string TakeId, string[] OriginSourceIds);
internal sealed record AgentContextAsset(string Id, string? Reference, string Status, string? ResolvedPath);
internal sealed record AgentContextDocument(string Reference, string Role, string Status, string? Text, string? Sha256, bool Truncated);

internal static class BackendAgentContext
{
    private const int ContextVersion = 3;
    private const int MaxDocumentBytes = 8192;
    private const int MaxDocumentChars = 4000;
    private const int MaxSourceCards = 4;

    internal static bool ValidSelector(string? selector) => selector is null ||
        (selector.Length <= 160 && !string.IsNullOrWhiteSpace(selector) && !selector.Any(char.IsControl));

    internal static async Task<BackendAgentContextReply> ReadAsync(ProjectFileDocument document, BackendOptions options,
        string? selector, CancellationToken token)
    {
        var project = document.Project;
        var selection = selector is null ? null : Select(project, selector);
        var resolver = new BackendMediaResolver(options);
        var assets = ReadAssets(project, resolver, token);
        var documents = new List<AgentContextDocument>();
        documents.Add(await ReadDocumentAsync(resolver, options.ProjectDirectory, "@project/AGENTS.md", "project_instructions", token));
        documents.Add(await ReadDocumentAsync(resolver, options.ProjectDirectory, "@project/Plan.md", "project_plan", token));
        var sourceIds = selection?.SourceIds.ToHashSet(StringComparer.Ordinal) ?? [];
        var cards = project.Sources.Where(source => sourceIds.Contains(source.Id))
            .Select(source => Property(source.Properties, "card_path"))
            .OfType<string>().Distinct(StringComparer.Ordinal).Take(MaxSourceCards + 1).ToArray();
        foreach (var card in cards.Take(MaxSourceCards))
            documents.Add(await ReadDocumentAsync(resolver, options.ProjectDirectory, card, "source_card", token));

        var json = JsonSerializer.Serialize(new
        {
            ContextVersion, document.SchemaVersion, document.Fingerprint, Selector = selector, Project = project,
            ScenarioActive = StudioBackendService.ScenarioActive(document), Block = selection, Assets = assets, Documents = documents,
            SourceCardsTruncated = cards.Length > MaxSourceCards, AutomaticDispatch = false
        }, BackendAdaptation.Json);
        return json.Length > StudioBackendService.MaxProjectBytes / 2
            ? new() { ErrorCode = "agent_context_too_large" }
            : new() { ContextJson = json, SnapshotSha256 = Hash(json) };
    }

    internal static AgentContextSelection Select(ProjectContract project, string selector)
    {
        var actions = project.Segments.SelectMany(segment => segment.Actions.Select((action, index) =>
            (Action: action, Link: new AgentContextAction(segment.Id, action.Id, Previous(segment.Actions, index)?.Id,
                Next(segment.Actions, index)?.Id)))).ToArray();
        var actionId = actions.FirstOrDefault(row => row.Action.Id == selector).Action?.Id;
        var sourceId = actionId is null ? project.Sources.FirstOrDefault(row => row.Id == selector)?.Id : null;
        if (actionId is null && sourceId is null)
        {
            var matchingActions = actions.Where(row => row.Action.Title == selector).Take(2).ToArray();
            var matchingSources = project.Sources.Where(row => row.Label == selector).Take(2).ToArray();
            var count = matchingActions.Length + matchingSources.Length;
            if (count != 1)
                throw new BackendCommandException(count == 0 ? "agent_context_block_missing" : "agent_context_block_ambiguous");
            actionId = matchingActions.FirstOrDefault().Action?.Id;
            sourceId = matchingSources.FirstOrDefault()?.Id;
        }

        var selectedActions = actions.Where(row => actionId is not null ? row.Action.Id == actionId :
            row.Action.Sources.Any(binding => binding.SourceId == sourceId)).ToArray();
        var clips = project.Montage.Clips.OrderBy(clip => clip.StartFrame).ThenBy(clip => clip.Id, StringComparer.Ordinal).ToArray();
        var placements = clips.Select((clip, index) => (Clip: clip, Index: index))
            .Where(row => actionId is not null ? row.Clip.ActionId == actionId : row.Clip.SourceId == sourceId)
            .Select(row => new AgentContextPlacement(row.Clip.Id, Previous(clips, row.Index)?.Id, Next(clips, row.Index)?.Id)).ToArray();
        var sourceIds = sourceId is not null ? [sourceId] : selectedActions.SelectMany(row => row.Action.Sources)
            .Select(binding => binding.SourceId).Distinct(StringComparer.Ordinal).ToArray();
        var origins = project.Sources.SelectMany(source => source.Takes.Select(take => (take.Id, SourceId: source.Id)))
            .ToLookup(row => row.Id, row => row.SourceId, StringComparer.Ordinal);
        var continuations = project.Sources.Where(source => sourceIds.Contains(source.Id, StringComparer.Ordinal))
            .Select(source => (source.Id, TakeId: Property(source.Properties, "context_take_id")))
            .Where(row => row.TakeId is not null)
            .Select(row => new AgentContextContinuation(row.Id, row.TakeId!, origins[row.TakeId!].ToArray())).ToArray();
        return new(actionId is not null ? "action" : "source", actionId ?? sourceId!, sourceIds,
            selectedActions.Select(row => row.Link).ToArray(), placements, continuations);
    }

    private static T? Previous<T>(T[] items, int index) where T : class => index > 0 ? items[index - 1] : null;
    private static T? Next<T>(T[] items, int index) where T : class => index + 1 < items.Length ? items[index + 1] : null;

    private static string? Property(IReadOnlyDictionary<string, JsonElement> properties, string name) =>
        properties.TryGetValue(name, out var value) && value.ValueKind == JsonValueKind.String ? value.GetString() : null;

    private static AgentContextAsset[] ReadAssets(ProjectContract project, BackendMediaResolver resolver, CancellationToken token)
    {
        if (!project.Properties.TryGetValue("assets", out var assets) || assets.ValueKind != JsonValueKind.Object) return [];
        var rows = new List<AgentContextAsset>();
        var paths = new Dictionary<string, (string Status, string? Path)>(StringComparer.Ordinal);
        foreach (var asset in assets.EnumerateObject().OrderBy(asset => asset.Name, StringComparer.Ordinal))
        {
            token.ThrowIfCancellationRequested();
            var reference = BackendScenarioEdit.TryReference(asset.Value);
            if (reference is null) { rows.Add(new(asset.Name, null, "invalid_reference", null)); continue; }
            if (!paths.TryGetValue(reference, out var resolved))
            {
                try { resolved = ("available", resolver.Resolve(reference)); }
                catch (Exception error) when (IsReferenceFailure(error)) { resolved = (Status(error), null); }
                paths.Add(reference, resolved);
            }
            rows.Add(new(asset.Name, reference, resolved.Status, resolved.Path));
        }
        return rows.ToArray();
    }

    private static async Task<AgentContextDocument> ReadDocumentAsync(BackendMediaResolver resolver, string projectDirectory,
        string reference, string role, CancellationToken token)
    {
        try
        {
            var path = resolver.ResolveReference(reference);
            var relative = Path.GetRelativePath(projectDirectory, path);
            if (Path.IsPathRooted(relative) || relative == ".." || relative.StartsWith(".." + Path.DirectorySeparatorChar, StringComparison.Ordinal) ||
                !Path.GetExtension(path).Equals(".md", StringComparison.OrdinalIgnoreCase) ||
                Path.GetFileName(path).Equals("Handbook.Secrets.md", StringComparison.OrdinalIgnoreCase))
                return new(reference, role, "access_denied", null, null, false);
            BackendMediaResolver.CheckNoLinks(path);
            await using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, 4096, FileOptions.Asynchronous);
            var buffer = new byte[MaxDocumentBytes + 1];
            var count = await stream.ReadAtLeastAsync(buffer, buffer.Length, false, token);
            var text = Encoding.UTF8.GetString(buffer, 0, Math.Min(MaxDocumentBytes, count)).TrimStart('\uFEFF');
            var truncated = count > MaxDocumentBytes || text.Length > MaxDocumentChars;
            var sha = count > MaxDocumentBytes ? null : Convert.ToHexString(SHA256.HashData(buffer.AsSpan(0, count)));
            return new(reference, role, "available", text[..Math.Min(text.Length, MaxDocumentChars)], sha, truncated);
        }
        catch (Exception error) when (IsReferenceFailure(error))
        { return new(reference, role, Status(error), null, null, false); }
    }

    private static bool IsReferenceFailure(Exception error) => error is IOException or UnauthorizedAccessException or
        ArgumentException or InvalidDataException or NotSupportedException;

    private static string Status(Exception error) => error switch
    {
        FileNotFoundException or DirectoryNotFoundException => "missing",
        UnauthorizedAccessException => "access_denied",
        ArgumentException or InvalidDataException or NotSupportedException => "invalid_reference",
        _ => "io_error"
    };

    private static string Hash(string json) => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(json)));

    internal static void Validate(JsonElement context, string? selector, string? sha)
    {
        if (context.ValueKind != JsonValueKind.Object || !BackendConnection.IsHash(sha) ||
            !string.Equals(Hash(context.GetRawText()), sha, StringComparison.OrdinalIgnoreCase) ||
            !context.TryGetProperty("context_version", out var version) || version.ValueKind != JsonValueKind.Number ||
            !version.TryGetInt32(out var number) || number != ContextVersion ||
            !context.TryGetProperty("schema_version", out var schema) || schema.ValueKind != JsonValueKind.Number ||
            !schema.TryGetInt32(out var storageVersion) || storageVersion != 2 ||
            !BackendConnection.IsHash(String(context, "fingerprint")) ||
            !context.TryGetProperty("selector", out var selected) ||
            (selector is null ? selected.ValueKind != JsonValueKind.Null : String(context, "selector") != selector) ||
            !context.TryGetProperty("automatic_dispatch", out var dispatch) || dispatch.ValueKind != JsonValueKind.False ||
            !context.TryGetProperty("block", out var selection) || !context.TryGetProperty("project", out var project))
            throw new JsonException("Invalid agent context envelope.");
        if (selector is null ? selection.ValueKind != JsonValueKind.Null :
            String(selection, "kind") is not ("action" or "source") || string.IsNullOrEmpty(String(selection, "id")))
            throw new JsonException("Invalid agent context selection.");
        _ = ProjectContractJson.Deserialize(project.GetRawText());
    }

    private static string? String(JsonElement element, string property) =>
        element.ValueKind == JsonValueKind.Object && element.TryGetProperty(property, out var value) &&
        value.ValueKind == JsonValueKind.String ? value.GetString() : null;
}
