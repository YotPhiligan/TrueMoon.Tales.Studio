using System.Text.Json;
using TrueMoon.Tales.Studio.Backend;
using Xunit;

namespace TrueMoon.Tales.Studio.Tests.Backend;

public sealed class BackendScenarioTests
{
    [Fact]
    public async Task AddingTextAction_PreservesLegacyWindowPositionsAndKeepsOtherWindowsInTheirProjection()
    {
        using var files = new BackendWriteFixture(scenario: true);
        using var service = files.Service();
        await files.MigrateAsync(service);
        var document = await files.Files.Store().OpenAsync();
        var project = document.Project;
        var segment = project.Segments.Single(row => row.Id == "segment-main");
        var command = new SaveAction(segment.Id, null, "Незаполненное действие", "Материал подберём позже", null);
        var edited = await BackendScenarioEdit.ApplyAsync(project, command,
            (_, _) => throw new InvalidOperationException("Text action must not touch media."), CancellationToken.None);
        Assert.Equal(project.Montage.Clips, edited.Montage.Clips);
        Assert.Equal(project.Montage.Windows, edited.Montage.Windows);
        var main = edited.Segments.Single(row => row.Id == segment.Id);
        Assert.Empty(main.Actions.Last().Sources);
        Assert.Equal(segment.Actions.Select(row => row.Id), main.Actions[..^1].Select(row => row.Id));
        Assert.False(BackendSceneMapping.Map(edited, true, "clip-main").ScenarioActive);
        Assert.True(BackendSceneMapping.Map(edited, true, "clip-extra").ScenarioActive);
        var moved = BackendMove.Apply(edited, true, "clip-main", 18);
        Assert.Equal(18, moved.Montage.Clips.Single(clip => clip.Id == "clip-main").StartFrame);
        Assert.Equal(edited.Segments, moved.Segments);
    }

    [Fact]
    public void Commands_RejectUnknownOrDuplicateFields()
    {
        Assert.Throws<JsonException>(() => ScenarioCommand.Decode("""{"operation":"save_segment","unknown":1}"""));
        Assert.Throws<JsonException>(() => ScenarioCommand.Decode("""{"operation":"save_segment","name":"one","name":"two"}"""));
    }
}
