using System.Text.Json;
using TrueMoon.Tales.Studio.Backend;
using TrueMoon.Tales.Studio.Contracts.Projects;
using Xunit;

namespace TrueMoon.Tales.Studio.Tests.Backend;

public sealed class BackendReferenceLibraryTests
{
    [Fact]
    public void Assign_ActionOverrideIsLocalAndFallsBackToSceneWhenRemoved()
    {
        var first = new ReferenceVersion(1, "В кофре", "@refs/watch-1.png", new('A', 64));
        var second = new ReferenceVersion(2, "На поясе", "@refs/watch-2.png", new('B', 64));
        var catalog = new ReferenceLibrary([new("watch", "Часы", "prop", [first, second])], []);
        var project = new ProjectContract
        {
            Id = "project", Segments =
            [
                new() { Id = "camp", Actions = [new() { Id = "wind" }, new() { Id = "pack" }] },
                new() { Id = "rest", Actions = [new() { Id = "sit" }] }
            ],
            Properties = new Dictionary<string, JsonElement>
            {
                ["studio_reference_library_v1"] = JsonSerializer.SerializeToElement(catalog, BackendAdaptation.Json),
                ["opaque"] = JsonSerializer.SerializeToElement("original")
            }
        };
        var camp = new AssignSceneReference("camp", null, "watch", 1);
        var windy = new AssignSceneReference("camp", "wind", "watch", 2);
        var assigned = BackendReferenceLibrary.Assign(project, camp);
        var overridden = BackendReferenceLibrary.Assign(assigned, windy);
        var library = BackendReferenceLibrary.Read(overridden);

        Assert.Equal((second, true), Chosen(library, "wind"));
        Assert.Equal((first, false), Chosen(library, "pack"));
        Assert.Equal((first, false), Chosen(library, null));
        Assert.Empty(library.For("rest", "sit"));
        Assert.Same(overridden, BackendReferenceLibrary.Assign(overridden, windy));
        Assert.Equal("invalid_reference_target", Assert.Throws<ScenarioEditException>(() =>
            BackendReferenceLibrary.Assign(overridden, windy with { ActionId = "sit" })).Code);

        var removed = BackendReferenceLibrary.Assign(overridden, windy with { Version = null });
        Assert.Equal((first, false), Chosen(BackendReferenceLibrary.Read(removed), "wind"));
        Assert.Equal("original", removed.Properties["opaque"].GetString());
        Assert.Equal([first, second], BackendReferenceLibrary.Read(removed).Entries.Single().Versions);

        static (ReferenceVersion, bool) Chosen(ReferenceLibrary library, string? action)
        {
            var choice = Assert.Single(library.For("camp", action));
            return (choice.Version, choice.Action);
        }
    }
}
