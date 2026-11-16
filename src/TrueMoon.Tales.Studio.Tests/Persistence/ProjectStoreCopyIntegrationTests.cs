using System.Runtime.CompilerServices;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using TrueMoon.Tales.Studio.Contracts.Projects;
using TrueMoon.Tales.Studio.Contracts.Serialization;
using TrueMoon.Tales.Studio.Persistence;
using Xunit;

namespace TrueMoon.Tales.Studio.Tests.Persistence;

public sealed class ProjectStoreCopyIntegrationTests
{
    [ProjectStoreCopyFact]
    public async Task Copy_OpenMigrateSaveAndReopen_PreservesIdsLinksAndExactHistory()
    {
        var root = GuardCopyRoot(Environment.GetEnvironmentVariable("STUDIO_PROJECT_STORE_COPY")!);
        var artifactRoot = Directory.GetParent(root)!.FullName;
        var seedPath = Path.Combine(artifactRoot, "original", "Studio.project.json");
        NoLinks(seedPath);
        var seed = await File.ReadAllBytesAsync(seedPath);
        var workingPath = Path.Combine(root, "Studio.project.json");
        NoLinks(workingPath);
        Assert.Equal(seed, await File.ReadAllBytesAsync(workingPath));
        var historyDirectory = Path.Combine(root, "Production", "History");
        NoLinks(historyDirectory);
        Assert.False(Directory.Exists(historyDirectory) && Directory.EnumerateFiles(historyDirectory).Any());
        var store = new ProjectStore(new() { ProjectDirectory = root, AllowedWriteRoots = [root] });

        var baseline = await store.OpenAsync();
        Assert.Equal(1, baseline.SchemaVersion);
        Assert.Equal(Convert.ToHexString(SHA256.HashData(seed)), baseline.Fingerprint);
        Assert.Equal(seed, await File.ReadAllBytesAsync(workingPath));
        var migrated = await store.MigrateAsync(baseline, baseline.Project.Revision, "agent:store-copy-acceptance");
        var migrationBytes = await File.ReadAllBytesAsync(workingPath);
        Assert.Equal(2, migrated.SchemaVersion);
        AssertSameProject(baseline.Project, migrated.Project, baseline.Project.Name, baseline.Project.Revision + 1);
        Assert.True(JsonNode.DeepEquals(JsonNode.Parse(baseline.OriginalDocument.GetRawText()),
            JsonNode.Parse(migrated.OriginalDocument.GetProperty("legacy_document").GetRawText())), "Migration changed the retained legacy document.");
        var editedName = migrated.Project.Name + " · ProjectStore copy acceptance";
        var saved = await store.SaveAsync(migrated, migrated.Project with { Name = editedName }, migrated.Project.Revision,
            "agent:store-copy-acceptance");
        var reopened = await new ProjectStore(new() { ProjectDirectory = root }).OpenAsync();

        Assert.Equal(2, reopened.SchemaVersion);
        Assert.Equal(saved.Fingerprint, reopened.Fingerprint);
        AssertSameProject(baseline.Project, reopened.Project, editedName, baseline.Project.Revision + 2);
        Assert.True(JsonNode.DeepEquals(JsonNode.Parse(baseline.OriginalDocument.GetRawText()),
            JsonNode.Parse(reopened.OriginalDocument.GetProperty("legacy_document").GetRawText())), "Save changed the retained legacy document.");
        var history = Directory.GetFiles(historyDirectory, "*.json");
        Assert.Equal(2, history.Length);
        var originalHistory = Assert.Single(history, path => Path.GetFileName(path).StartsWith($"r{baseline.Project.Revision:D6}-", StringComparison.Ordinal));
        var migratedHistory = Assert.Single(history, path => Path.GetFileName(path).StartsWith($"r{migrated.Project.Revision:D6}-", StringComparison.Ordinal));
        NoLinks(originalHistory);
        NoLinks(migratedHistory);
        Assert.Equal(seed, await File.ReadAllBytesAsync(originalHistory));
        Assert.Equal(migrationBytes, await File.ReadAllBytesAsync(migratedHistory));
        Assert.Equal(seed, await File.ReadAllBytesAsync(seedPath));
        Assert.False(File.Exists(Path.Combine(root, ".Studio.project.transaction.json")));

        var reportPath = Path.Combine(artifactRoot, "store-acceptance.json");
        NoLinks(reportPath);
        await using var report = new FileStream(reportPath, FileMode.CreateNew, FileAccess.Write, FileShare.Read);
        await JsonSerializer.SerializeAsync(report, new
        {
            schema_before = baseline.SchemaVersion,
            schema_after = reopened.SchemaVersion,
            revision_before = baseline.Project.Revision,
            revision_after_migration = migrated.Project.Revision,
            revision_after_save = reopened.Project.Revision,
            segments = reopened.Project.Segments.Length,
            actions = reopened.Project.Segments.Sum(segment => segment.Actions.Length),
            sources = reopened.Project.Sources.Length,
            takes = reopened.Project.Sources.Sum(source => source.Takes.Length),
            windows = reopened.Project.Montage.Windows.Length,
            clips = reopened.Project.Montage.Clips.Length,
            history_files = history.Length,
            seed_sha256 = Convert.ToHexString(SHA256.HashData(seed)),
            saved_sha256 = reopened.Fingerprint,
            seed_unchanged = true
        });
    }

    private static void AssertSameProject(ProjectContract expected, ProjectContract actual, string name, long revision)
    {
        Assert.Equal(revision, actual.Revision);
        Assert.True(string.Equals(name, actual.Name, StringComparison.Ordinal), "The requested Name edit was not preserved.");
        var normalized = actual with { Name = expected.Name, Revision = expected.Revision };
        Assert.True(JsonNode.DeepEquals(JsonNode.Parse(ProjectContractJson.Serialize(expected)),
            JsonNode.Parse(ProjectContractJson.Serialize(normalized))), "The project changed beyond the requested Name/Revision fields; IDs, links or metadata differ.");
    }

    private static string GuardCopyRoot(string value, [CallerFilePath] string sourceFile = "")
    {
        if (!Path.IsPathFullyQualified(value)) throw new InvalidOperationException("An absolute isolated-copy path is required.");
        var root = Path.TrimEndingDirectorySeparator(Path.GetFullPath(value));
        var project = new DirectoryInfo(root);
        var copy = project.Parent;
        var local = copy?.Parent;
        var sourceRoot = Path.GetFullPath(Path.Combine(Path.GetDirectoryName(sourceFile)!, "..", ".."));
        var comparison = OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;
        if (project.Name != "project" || copy is null || !Regex.IsMatch(copy.Name, "\\Astore-copy-[0-9a-f]{32}\\z") ||
            local is null || local.Name != ".local" || !string.Equals(local.FullName, Path.Combine(sourceRoot, ".local"), comparison))
            throw new InvalidOperationException("Only this Studio/src/.local/store-copy-<32hex>/project is permitted.");
        NoLinks(root);
        return root;
    }

    private static void NoLinks(string path)
    {
        for (string? current = Path.GetFullPath(path); current is not null; current = Path.GetDirectoryName(current))
        {
            try
            {
                if ((File.GetAttributes(current) & FileAttributes.ReparsePoint) != 0)
                    throw new UnauthorizedAccessException("Isolated copy paths cannot contain reparse points.");
            }
            catch (FileNotFoundException) { }
            catch (DirectoryNotFoundException) { }
        }
    }
}

public sealed class ProjectStoreCopyFactAttribute : FactAttribute
{
    public ProjectStoreCopyFactAttribute()
    {
        if (Environment.GetEnvironmentVariable("STUDIO_PROJECT_STORE_COPY") is null)
            Skip = "Requires explicit isolated ProjectStore copy under Studio/src/.local";
    }
}
