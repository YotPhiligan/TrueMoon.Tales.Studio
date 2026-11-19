using System.Text.Json;
using TrueMoon.Tales.Studio.Backend;
using TrueMoon.Tales.Studio.Contracts.Projects;
using Xunit;

namespace TrueMoon.Tales.Studio.Tests.Backend;

public sealed class BackendSceneJoinsTests
{
    [Fact]
    public void Pairs_FollowSavedTime_AndDoNotSkipDetachedOrOverlappingWindows()
    {
        var first = Window("one", 0, 1);
        var second = Window("two", 4, 2);
        var detached = Window("old", 6, 1) with { SegmentId = null };
        var third = Window("three", 7, 2);
        var overlapping = Window("overlap", 8, 1);
        var pair = Assert.Single(BackendSceneJoins.Pairs(Project(overlapping, second, third, first, detached)));
        Assert.Equal((first, second, 0, 4), (pair.Left, pair.Right, pair.ExitFrame, pair.EntryFrame));
    }

    [Fact]
    public void Apply_StoresBothStates_AndClearsWithoutChangingMontage()
    {
        var baseline = Project(Window("left", 0, 8), Window("right", 8, 8));
        var command = new SaveSceneJoin("left", "right", "  Часы\r\nидут.  ", "Часы на поясе.", "  Рэй закрепил цепочку. ");
        var decoded = Assert.IsType<SaveSceneJoin>(ScenarioCommand.Decode(ScenarioCommand.Encode(command)));
        var saved = BackendSceneJoins.Apply(baseline, decoded);
        var note = Assert.Single(BackendSceneJoins.Read(saved)).Value;
        Assert.Equal(("Часы\nидут.", "Часы на поясе.", "Рэй закрепил цепочку."), (note.ExitState, note.EntryState, note.Bridge));
        Assert.Equal("Переход описан", note.Summary);
        Assert.Equal("keep me", saved.Properties["opaque"].GetString());
        Assert.Same(baseline.Montage, saved.Montage);
        Assert.Same(saved, BackendSceneJoins.Apply(saved, command));
        var cleared = BackendSceneJoins.Apply(saved, command with { ExitState = "", EntryState = "", Bridge = "" });
        Assert.Empty(BackendSceneJoins.Read(cleared));
        Assert.Single(cleared.Properties);
        Assert.Same(baseline.Montage, cleared.Montage);
    }

    [Theory]
    [InlineData("left", "third", "Выход", "Вход", "", "scene_join_unavailable")]
    [InlineData("right", "left", "Выход", "Вход", "", "scene_join_unavailable")]
    [InlineData("left", "right", "", "Вход", "Связка", "invalid_scene_join")]
    [InlineData("left", "right", "Выход", null, "", "invalid_scene_join")]
    [InlineData("left", "right", "Выход\0", "Вход", "", "invalid_scene_join")]
    public void Apply_RejectsInvalidPairsAndNotes(string left, string right, string exit, string? entry, string bridge, string code)
    {
        var project = Project(Window("left", 0, 8), Window("right", 8, 8), Window("third", 16, 8));
        Assert.Equal(code, Assert.Throws<ScenarioEditException>(
            () => BackendSceneJoins.Apply(project, new(left, right, exit, entry!, bridge))).Code);
        Assert.Empty(BackendSceneJoins.Read(project));
    }

    [Fact]
    public void Apply_PreservesStateOfOtherJoins_AndReportsAnUnfilledSide()
    {
        var project = Project(Window("left", 0, 8), Window("right", 8, 8), Window("third", 16, 8));
        var same = BackendSceneJoins.Apply(project, new("left", "right", "Часы идут.", "Часы идут.", ""));
        var withPartial = BackendSceneJoins.Apply(same, new("right", "third", "", "Привал", ""));
        var notes = BackendSceneJoins.Read(withPartial);
        Assert.Equal("Записи совпадают", notes[("left", "right")].Summary);
        Assert.Equal("Не заполнен выход", notes[("right", "third")].Summary);
        Assert.Equal("Привал", notes[("right", "third")].EntryState);
    }

    private static SegmentWindowContract Window(string id, int start, int duration) =>
        new() { Id = id, SegmentId = "scene-" + id, StartFrame = start, DurationFrames = duration };

    private static ProjectContract Project(params SegmentWindowContract[] windows) => new()
    {
        Id = "project",
        Montage = new() { Windows = windows },
        Segments = windows.Where(row => row.SegmentId is not null).Select(row => new ScenarioSegmentContract { Id = row.SegmentId! }).ToArray(),
        Properties = new Dictionary<string, JsonElement> { ["opaque"] = JsonSerializer.SerializeToElement("keep me") }
    };
}
