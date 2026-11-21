using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Nodes;
using TrueMoon.Tales.Studio.Contracts.Projects;
using TrueMoon.Tales.Studio.Persistence;

namespace TrueMoon.Tales.Studio.Backend;

internal static class BackendTakeRemoval
{
    private const string Removed = "studio_removed_takes_v1";
    private static readonly StringComparison PathComparison = OperatingSystem.IsWindows()
        ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;
    internal sealed record Removal(string SourceId, string TakeId, string Reference, string Sha256, string? AssetId = null);

    private static Removal[] History(ProjectContract project) =>
        project.Properties.TryGetValue(Removed, out var value) ? value.Deserialize<Removal[]>() ?? [] : [];

    internal static Removal? Find(ProjectContract project, string sourceId, string takeId) =>
        History(project).LastOrDefault(row => row.SourceId == sourceId && row.TakeId == takeId);

    internal static bool CanRemovePath(string projectDirectory, string path)
    {
        if (!IsVideo(path)) return false;
        var relative = Path.GetRelativePath(projectDirectory, path).Split(Path.DirectorySeparatorChar);
        return relative.Length > 1 && relative[0].Equals("video", PathComparison) && !relative.Contains("..") ||
            relative is ["Production", "FullTransfer.v1", var id, "Run", "output", ..] && BackendFullTransfer.ValidJobId(id);
    }

    internal static ProjectContract RemoveFromLibrary(ProjectContract project, string sourceId, string takeId, out string reference,
        BackendMediaResolver? resolver = null)
    {
        var source = project.Sources.SingleOrDefault(row => row.Id == sourceId);
        var take = source?.Takes.SingleOrDefault(row => row.Id == takeId);
        if (take?.VideoAssetId is not { } assetId || BackendScenarioEdit.TryReference(project, assetId) is not { } path)
            throw new ScenarioEditException("take_remove_unavailable");
        reference = path;
        var assets = JsonNode.Parse(project.Properties["assets"].GetRawText())!.AsObject();
        assets.Remove(assetId);
        var properties = new Dictionary<string, JsonElement>(project.Properties)
        {
            ["assets"] = JsonSerializer.SerializeToElement(assets)
        };
        var draft = project with
        {
            Properties = properties,
            Sources = project.Sources.Select(row => row.Id == sourceId
                ? row with { Takes = row.Takes.Where(item => item.Id != takeId).ToArray() } : row).ToArray()
        };
        CheckLinks(draft, takeId, assetId, path, resolver);
        return draft;
    }

    internal static void CheckLinks(ProjectContract project, string takeId, string? assetId, string path, BackendMediaResolver? resolver)
    {
        var remaining = new Dictionary<string, JsonElement>(project.Properties);
        remaining.Remove(Removed);
        var json = JsonSerializer.SerializeToElement(project with { Properties = remaining });
        var physical = resolver?.ResolveReference(path);
        if (ContainsLink(json))
            throw new ScenarioEditException("take_in_use");

        bool ContainsLink(JsonElement node) => node.ValueKind switch
        {
            JsonValueKind.Object => node.EnumerateObject().Any(item => ContainsLink(item.Value) ||
                item.Name == takeId || item.Name == assetId),
            JsonValueKind.Array => node.EnumerateArray().Any(ContainsLink),
            JsonValueKind.String => node.GetString() is { } text &&
                (text == takeId || text == assetId || text.Equals(path, PathComparison) || ResolvesToVideo(text)),
            _ => false
        };

        bool ResolvesToVideo(string text)
        {
            if (resolver is null || !IsVideo(text)) return false;
            try { return string.Equals(physical, resolver.ResolveReference(text), PathComparison); }
            catch (Exception error) when (error is IOException or UnauthorizedAccessException or ArgumentException) { return false; }
        }
    }

    internal static ProjectContract Record(ProjectContract project, Removal removal) => project with
    {
        Properties = new Dictionary<string, JsonElement>(project.Properties)
        {
            [Removed] = JsonSerializer.SerializeToElement(History(project).Append(removal).ToArray())
        }
    };

    internal static async Task<(Removal Record, string Path)> CheckFileAsync(ProjectContract project, string sourceId, string takeId,
        string reference, ProjectStoreOptions paths, BackendMediaResolver resolver, CancellationToken token)
    {
        var path = AuthorizedPath(reference, paths, resolver);
        await using var file = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, 64 * 1024, FileOptions.Asynchronous);
        var sha = Convert.ToHexString(await SHA256.HashDataAsync(file, token).ConfigureAwait(false));
        var take = project.Sources.Single(row => row.Id == sourceId).Takes.Single(row => row.Id == takeId);
        if (BackendTakePairs.Read(take) is { } pair && !sha.Equals(pair.Final.Sha256, StringComparison.OrdinalIgnoreCase))
            throw new ScenarioEditException("take_replaced");
        return (new(sourceId, takeId, reference, sha, take.VideoAssetId), path);
    }

    internal static string AuthorizedPath(string reference, ProjectStoreOptions paths, BackendMediaResolver resolver)
    {
        var path = resolver.ResolveReference(reference);
        if (!CanRemovePath(paths.ProjectDirectory, path)) throw new ScenarioEditException("take_remove_unavailable");
        BackendProjectAccess.CheckProjectSubdirectoryWrite(paths, Path.GetRelativePath(paths.ProjectDirectory, path).Split(Path.DirectorySeparatorChar)[0], path);
        return path;
    }

    private static bool IsVideo(string path) => Path.GetExtension(path).ToLowerInvariant() is ".mp4" or ".mov" or ".webm";

    internal static async Task RecycleAsync(Removal removal, string path, Action<string> recycle)
    {
        BackendMediaResolver.CheckNoLinks(path);
        await using var file = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read | FileShare.Delete, 64 * 1024, FileOptions.Asynchronous);
        if (Convert.ToHexString(await SHA256.HashDataAsync(file).ConfigureAwait(false)) != removal.Sha256)
            throw new ScenarioEditException("take_replaced");
        file.Close();
        BackendMediaResolver.CheckNoLinks(path);
        recycle(path);
    }
}
