using TrueMoon.Tales.Studio.Backend;
using TrueMoon.Tales.Studio.Contracts.Projects;
using TrueMoon.Tales.Studio.Contracts.Serialization;
using TrueMoon.Tales.Studio.Persistence;
using Xunit;

namespace TrueMoon.Tales.Studio.Tests.Backend;

public sealed class BackendMoveTests
{
    [Theory]
    [InlineData(0, true)]
    [InlineData(12, true)]
    [InlineData(216, true)]
    [InlineData(-1, false)]
    [InlineData(217, false)]
    public void PlainMove_UsesExactWindowBounds_AndPreservesOtherData(int start, bool valid)
    {
        using var fixture = new BackendWriteFixture();
        var project = ProjectFileCodec.Read(File.ReadAllBytes(fixture.Files.ProjectPath)).Project;
        if (!valid) { Assert.Throws<InvalidMoveException>(() => BackendMove.Apply(project, false, "clip-main", start)); return; }
        var moved = BackendMove.Apply(project, false, "clip-main", start);
        var expected = project with { Montage = project.Montage with { Clips = project.Montage.Clips
            .Select(clip => clip.Id == "clip-main" ? clip with { StartFrame = start } : clip).ToArray() } };
        Assert.Equal(ProjectContractJson.Serialize(expected), ProjectContractJson.Serialize(moved));
        if (start == 12) Assert.Same(project, moved);
    }

    [Fact]
    public void Scenario_ReordersActionsAndReflowsClips_PreservingGapMaterialAndOtherWindow()
    {
        using var fixture = new BackendWriteFixture(true);
        var project = Pair(ProjectFileCodec.Read(File.ReadAllBytes(fixture.Files.ProjectPath)).Project);
        Assert.Same(project, BackendMove.Apply(project, true, "clip-main", 13));
        var moved = BackendMove.Apply(project, true, "clip-main", 60);
        Assert.Equal(new[] { "clip-B", "clip-main" }, moved.Segments[0].Actions.Select(action => action.Id));
        Assert.Equal(new[] { 48, 312, 12 }, moved.Montage.Clips.Select(clip => clip.StartFrame));
        Assert.Equal(project.Montage.Clips[0] with { StartFrame = 48 }, moved.Montage.Clips[0]);
        Assert.Equal(project.Montage.Clips[1], moved.Montage.Clips[1]);
        Assert.Equal(project.Montage.Clips[2] with { StartFrame = 12 }, moved.Montage.Clips[2]);
        Assert.Equal(project.Segments[0].Actions[0], moved.Segments[0].Actions[1]);
        Assert.Equal(project.Segments[0].Actions[1], moved.Segments[0].Actions[0]);
        Assert.Equal(project.Montage.Windows, moved.Montage.Windows);
        ProjectContractJson.Validate(moved);
    }

    [Theory]
    [InlineData("anchored")]
    [InlineData("overflow")]
    [InlineData("inconsistent")]
    [InlineData("overlap")]
    public void InvalidLayouts_DoNotProduceAProjectDraft(string reason)
    {
        using var fixture = new BackendWriteFixture(true);
        var project = Pair(ProjectFileCodec.Read(File.ReadAllBytes(fixture.Files.ProjectPath)).Project);
        if (reason == "anchored") project = project with { Montage = project.Montage with { Clips = project.Montage.Clips
            .Select(clip => clip.Id == "clip-main" ? clip with { AnchorFrame = 12 } : clip).ToArray() } };
        if (reason == "inconsistent") project = project with { Segments = project.Segments.Select(segment => segment.Id == "segment-main"
            ? segment with { Actions = segment.Actions.Select(action => action.Id == "clip-main" ? action with { PlannedDurationFrames = 25 } : action).ToArray() } : segment).ToArray() };
        if (reason == "overflow") project = project with
        {
            Montage = project.Montage with { Windows = project.Montage.Windows.Select(window => window.Id == "segment-main" ? window with { DurationFrames = 80 } : window).ToArray(),
                Clips = project.Montage.Clips.Select(clip => clip.Id == "clip-B" ? clip with { AnchorFrame = 48 } : clip).ToArray() },
            Segments = project.Segments.Select(segment => segment.Id == "segment-main" ? segment with { Actions = segment.Actions.Select(action => action.Id == "clip-B" ? action with { AnchorFrame = 48 } : action).ToArray() } : segment).ToArray()
        };
        Assert.Throws<InvalidMoveException>(() => BackendMove.Apply(project, reason != "overlap", "clip-main", reason == "overlap" ? 40 : 60));
    }

    private static ProjectContract Pair(ProjectContract project)
    {
        var action = project.Segments[0].Actions[0] with { Id = "clip-B", Title = "Второе действие" };
        var clip = project.Montage.Clips[0] with { Id = "clip-B", ActionId = "clip-B", StartFrame = 48 };
        return project with { Segments = project.Segments.Select(segment => segment.Id == "segment-main" ? segment with { Actions = [.. segment.Actions, action] } : segment).ToArray(),
            Montage = project.Montage with { Clips = [.. project.Montage.Clips, clip] } };
    }
}
