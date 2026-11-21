using System.Text.Json;
using TrueMoon.Tales.Studio.Backend;
using TrueMoon.Tales.Studio.Contracts.Projects;
using TrueMoon.Tales.Studio.Tests.Persistence;
using Xunit;

namespace TrueMoon.Tales.Studio.Tests.Backend;

public sealed class BackendTakeRemovalTests
{
    private const string Reference = "@project/video/selected.mp4";
    private static readonly ProjectContract Project = BackendScenarioEdit.SetReference(new()
    {
        Id = "project",
        Sources = [new() { Id = "source", Takes = [new() { Id = "wanted", VideoAssetId = "asset" }, new() { Id = "other" }] }]
    }, "asset", Reference);

    [Theory]
    [InlineData("clip")]
    [InlineData("sound")]
    [InlineData("shared-file")]
    [InlineData("draft-of-another-take")]
    public void RemoveFromLibrary_RejectsReferencesFromTheRemainingProject(string use)
    {
        var project = use switch
        {
            "clip" => Project with { Montage = new() { Clips = [new() { Id = "clip", WindowId = "scene", SourceId = "source", TakeId = "wanted" }] } },
            "sound" => Project with { Montage = new() { Properties = Property("sound_events", new[] { new { asset_id = "asset" } }) } },
            "shared-file" => BackendScenarioEdit.SetReference(Project, "another-asset", Reference),
            _ => Project with { Sources = [.. Project.Sources, new() { Id = "derived", Takes =
                [new() { Id = "later", Properties = Property("draft_snapshot", new { take_id = "wanted" }) }] }] }
        };
        var error = Assert.Throws<ScenarioEditException>(() => BackendTakeRemoval.RemoveFromLibrary(project, "source", "wanted", out _));
        Assert.Equal("take_in_use", error.Code);
    }

    [Fact]
    public void RemoveFromLibrary_RemovesOnlyTheSelectedTakeAndItsAsset()
    {
        var result = BackendTakeRemoval.RemoveFromLibrary(Project, "source", "wanted", out var path);
        Assert.Equal(Reference, path);
        Assert.Equal("other", Assert.Single(Assert.Single(result.Sources).Takes).Id);
        Assert.Null(BackendScenarioEdit.TryReference(result, "asset"));
        Assert.Equal(Reference, BackendScenarioEdit.TryReference(Project, "asset"));
    }

    [Fact]
    public async Task Recycle_RejectsAReplacementBeforeCallingWindows()
    {
        using var files = new PersistenceFixture();
        var path = Path.Combine(files.Root, "test.mp4");
        File.WriteAllBytes(path, [4, 8, 12]);
        var removal = new BackendTakeRemoval.Removal("source", "wanted", Reference, new string('A', 64));
        var error = await Assert.ThrowsAsync<ScenarioEditException>(() => BackendTakeRemoval.RecycleAsync(removal, path,
            _ => throw new InvalidOperationException("Replaced file must not be recycled.")));
        Assert.Equal("take_replaced", error.Code);
        Assert.Equal(new byte[] { 4, 8, 12 }, File.ReadAllBytes(path));
    }

    private static Dictionary<string, JsonElement> Property<T>(string key, T value) =>
        new() { [key] = JsonSerializer.SerializeToElement(value) };
}
