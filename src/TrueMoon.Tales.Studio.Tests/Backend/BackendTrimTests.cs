using TrueMoon.Tales.Studio.Backend;
using TrueMoon.Tales.Studio.Contracts.Projects;
using TrueMoon.Tales.Studio.Contracts.Serialization;
using TrueMoon.Tales.Studio.Persistence;
using Xunit;

namespace TrueMoon.Tales.Studio.Tests.Backend;

public sealed class BackendTrimTests
{
    private static ProjectContract Project(bool scenario = false)
    {
        using var fixture = new BackendWriteFixture(scenario);
        return ProjectFileCodec.Read(File.ReadAllBytes(fixture.Files.ProjectPath)).Project;
    }

    [Fact]
    public void PlainTrim_ChangesOnlySelectedSourceAndActualDuration_PreservingAllOtherData()
    {
        var original = Project();
        var trimmed = BackendTrim.Apply(original, false, "clip-main", 4, 20, 12, 72);
        var expected = original with { Montage = original.Montage with { Clips = original.Montage.Clips.Select(row => row.Id == "clip-main"
            ? row with { SourceInFrame = 4, SourceOutFrame = 20, DurationFrames = 16 } : row).ToArray() } };
        Assert.Equal(ProjectContractJson.Serialize(expected), ProjectContractJson.Serialize(trimmed));
        Assert.Same(original.Sources, trimmed.Sources); Assert.Same(original.Segments, trimmed.Segments);
        Assert.Same(original.Montage.Properties, trimmed.Montage.Properties);
        ProjectContractJson.Validate(trimmed);
    }

    [Fact]
    public void ScenarioTrim_ReflowsActualDurationsAndPreservesPlanGapsAnchorsAndOtherWindow()
    {
        var original = Three();
        var trimmed = BackendTrim.Apply(original, true, "clip-main", 0, 12, 12, 72);
        Assert.Equal(12, trimmed.Montage.Clips.Single(row => row.Id == "clip-main").DurationFrames);
        Assert.Equal(36, trimmed.Montage.Clips.Single(row => row.Id == "clip-B").StartFrame);
        Assert.Equal(84, trimmed.Montage.Clips.Single(row => row.Id == "clip-C").StartFrame);
        Assert.Equal(original.Montage.Clips.Single(row => row.Id == "clip-extra"), trimmed.Montage.Clips.Single(row => row.Id == "clip-extra"));
        Assert.Equal(ProjectContractJson.Serialize(original with { Montage = trimmed.Montage }), ProjectContractJson.Serialize(trimmed));
        Assert.True(BackendSceneMapping.Map(trimmed, true, "clip-main").ScenarioConsistent);
        Assert.Equal(24, trimmed.Segments[0].Actions[0].PlannedDurationFrames);
        Assert.True(BackendSceneMapping.Map(BackendMove.Apply(trimmed, true, "clip-main", 13), true, "clip-main").ScenarioConsistent);
    }

    [Fact]
    public void LeftTrim_UsesExplicitStartDeltaForSelectedGap_AndKeepsFollowingMaterialAtItsTime()
    {
        var original = Three();
        var trimmed = BackendTrim.Apply(original, true, "clip-main", 6, 24, 18, 72);
        var clip = trimmed.Montage.Clips.Single(row => row.Id == "clip-main");
        Assert.Equal((18, 18, 6d, 24d), (clip.StartFrame, clip.DurationFrames, clip.SourceInFrame, clip.SourceOutFrame!.Value));
        Assert.Equal(18, trimmed.Segments[0].Actions[0].GapBeforeFrames);
        Assert.Equal(24, trimmed.Segments[0].Actions[0].PlannedDurationFrames);
        Assert.Equal(original.Montage.Clips.Where(row => row.Id != "clip-main"), trimmed.Montage.Clips.Where(row => row.Id != "clip-main"));
        Assert.True(BackendSceneMapping.Map(trimmed, true, "clip-main").ScenarioConsistent);
    }

    [Fact]
    public void LogicalNoop_KeepsImplicitSourceOutAndOriginalObject_WithoutRepresentationChange()
    {
        var project = Project();
        var implicitClip = project.Montage.Clips.Single(row => row.Id == "clip-extra");
        Assert.Null(implicitClip.SourceOutFrame);
        Assert.Same(project, BackendTrim.Apply(project, false, implicitClip.Id, 0, 24, 312, 72));
        Assert.Same(project, BackendTrim.Apply(project, false, "clip-main", 0, 24, 12, 72));
    }

    [Theory]
    [InlineData(0, 1, 0, 1, 1)]
    [InlineData(0, 72, 12, 72, 72)]
    [InlineData(.25, 2.75, 12, 72, 3)]
    [InlineData(0, .000000001, 12, 72, 1)]
    public void ValidSourceBoundaries_KeepPositiveMontageDuration(double start, double end, int position, double capacity, int expectedDuration)
    {
        var result = BackendTrim.Apply(Project(), false, "clip-main", start, end, position, capacity);
        var clip = result.Montage.Clips[0];
        Assert.Equal(start, clip.SourceInFrame); Assert.Equal(end, clip.SourceOutFrame);
        Assert.Equal(expectedDuration, clip.DurationFrames);
        Assert.Equal(position, clip.StartFrame);
    }

    [Theory]
    [InlineData(-.01, 24, 12, 72)]
    [InlineData(0, 0, 12, 72)]
    [InlineData(24, 12, 12, 72)]
    [InlineData(0, 72.0001, 12, 72)]
    [InlineData(0, 24, -1, 72)]
    [InlineData(0, 24, 217, 72)]
    [InlineData(double.NaN, 24, 12, 72)]
    [InlineData(0, double.PositiveInfinity, 12, 72)]
    [InlineData(0, 24, 12, double.NaN)]
    [InlineData(2147483647d, 2147483648d, 12, double.MaxValue)]
    public void InvalidTimingAndSourceCapacity_RejectWithoutMutatingInput(double start, double end, int position, double capacity)
    {
        var project = Project(); var before = ProjectContractJson.Serialize(project);
        Assert.Throws<InvalidTrimException>(() => BackendTrim.Apply(project, false, "clip-main", start, end, position, capacity));
        Assert.Equal(before, ProjectContractJson.Serialize(project));
    }

    [Theory]
    [InlineData("unknown")]
    [InlineData("overlap")]
    [InlineData("anchor")]
    [InlineData("anchor-overflow")]
    [InlineData("inconsistent")]
    [InlineData("many-uses")]
    [InlineData("shared-window")]
    public void UnsupportedOrConflictingLayouts_DoNotProduceDraft(string kind)
    {
        var project = Three();
        var active = kind != "overlap";
        if (kind == "anchor") project = SetClip(project, "clip-main", row => row with { AnchorFrame = 12 }) with
        { Segments = project.Segments.Select(segment => segment.Id == "segment-main" ? segment with { Actions = segment.Actions.Select(action => action.Id == "clip-main" ? action with { AnchorFrame = 12 } : action).ToArray() } : segment).ToArray() };
        if (kind == "inconsistent") project = SetClip(project, "clip-B", row => row with { StartFrame = 49 });
        if (kind == "many-uses") project = project with { Montage = project.Montage with { Clips = [.. project.Montage.Clips,
            project.Montage.Clips[0] with { Id = "clip-copy", StartFrame = 150 }] } };
        if (kind == "shared-window") project = project with { Montage = project.Montage with { Windows = [.. project.Montage.Windows,
            project.Montage.Windows[0] with { Id = "window-other", StartFrame = 600 }] } };
        var original = ProjectContractJson.Serialize(project);
        Assert.Throws<InvalidTrimException>(() => BackendTrim.Apply(project, active, kind == "unknown" ? "absent" : "clip-main",
            0, kind is "overlap" or "anchor-overflow" ? 48 : 12, kind == "anchor" ? 13 : 12, 72));
        Assert.Equal(original, ProjectContractJson.Serialize(project));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void ProtectedGap_CannotBeConsumedButCanBeExplicitlyIncreased(bool scenario)
    {
        var project = Three();
        project = SetClip(project, "clip-main", row => row with { GapProtected = true }) with
        { Segments = project.Segments.Select(segment => segment.Id == "segment-main" ? segment with { Actions = segment.Actions.Select(action => action.Id == "clip-main" ? action with { GapProtected = true } : action).ToArray() } : segment).ToArray() };
        Assert.Throws<InvalidTrimException>(() => BackendTrim.Apply(project, scenario, "clip-main", 0, 12, 11, 72));
        var trimmed = BackendTrim.Apply(project, scenario, "clip-main", 6, 24, 18, 72);
        Assert.True(trimmed.Montage.Clips[0].GapProtected); Assert.Equal(18, trimmed.Montage.Clips[0].StartFrame);
        Assert.True(trimmed.Segments[0].Actions[0].GapProtected);
    }

    [Fact]
    public void PlainTrim_PreservesFollowingProtectedPause_AndAllowsUnprotectedExtension()
    {
        var project = Three();
        var protectedGap = SetClip(project, "clip-B", row => row with { GapProtected = true });
        Assert.Throws<InvalidTrimException>(() => BackendTrim.Apply(protectedGap, false, "clip-main", 0, 30, 12, 72));
        var extended = BackendTrim.Apply(project, false, "clip-main", 0, 30, 12, 72);
        Assert.Equal(30, extended.Montage.Clips[0].DurationFrames);
        Assert.Equal(48, extended.Montage.Clips.Single(row => row.Id == "clip-B").StartFrame);
    }

    [Fact]
    public void Mapping_PlannedDurationIsIndependentFromActualMontage_AfterCompatibilityProjection()
    {
        var project = Project(true);
        project = project with { Segments = project.Segments.Select(segment => segment.Id == "segment-main" ? segment with
            { Actions = segment.Actions.Select(action => action with { PlannedDurationFrames = 120 }).ToArray() } : segment).ToArray() };
        var scene = BackendSceneMapping.Map(project, true, "clip-main");
        Assert.True(scene.ScenarioConsistent); Assert.Equal(24, Assert.Single(scene.ScenarioItems!).DurationFrames);
        var result = BackendTrim.Apply(project, true, "clip-main", 0, 12, 12, 72);
        Assert.Equal(120, result.Segments[0].Actions[0].PlannedDurationFrames);
        Assert.Equal(12, result.Montage.Clips[0].DurationFrames);
        Assert.True(BackendSceneMapping.Map(result, true, "clip-main").ScenarioConsistent);
    }

    [Fact]
    public void PlaceholderWithoutVideoTakeCannotBeTrimmed()
    {
        var original = Project();
        var project = original with { Sources = original.Sources.Select(source => source.Id == "source-a"
            ? source with { Takes = source.Takes.Select(take => take with { VideoAssetId = null }).ToArray() } : source).ToArray() };
        Assert.Throws<InvalidTrimException>(() => BackendTrim.Apply(project, false, "clip-main", 0, 12, 12, 72));
        Assert.Equal(24, project.Montage.Clips[0].DurationFrames);
    }

    [Fact]
    public void FractionalSourceAndSpeed_CeilingDurationIsIndependentOfPlannedTime()
    {
        var project = SetClip(Project(), "clip-main", row => row with { SourceOutFrame = 30, Speed = 1.25 });
        var result = BackendTrim.Apply(project, false, "clip-main", 2.25, 20.5, 12, 72);
        Assert.Equal(15, result.Montage.Clips[0].DurationFrames); // 18.25 / 1.25 = 14.6 frames.
        Assert.Equal((2.25d, 20.5d, 1.25d), (result.Montage.Clips[0].SourceInFrame, result.Montage.Clips[0].SourceOutFrame!.Value, result.Montage.Clips[0].Speed));
        Assert.Equal(24, result.Segments[0].Actions[0].PlannedDurationFrames);
    }

    [Fact]
    public void CompleteSharedActionInTwoWindows_IsReadableButRefusesTrimWithoutChangingEitherWindow()
    {
        var original = Project(true);
        var shared = original with { Montage = original.Montage with
        {
            Windows = [.. original.Montage.Windows, original.Montage.Windows[0] with { Id = "shared-window", StartFrame = 600 }],
            Clips = [.. original.Montage.Clips, original.Montage.Clips[0] with { Id = "shared-clip", WindowId = "shared-window", StartFrame = 612 }]
        } };
        ProjectContractJson.Validate(shared);
        Assert.True(BackendSceneMapping.Map(shared, true, "clip-main").ScenarioConsistent);
        var bytes = ProjectContractJson.Serialize(shared);
        Assert.Throws<InvalidTrimException>(() => BackendTrim.Apply(shared, true, "clip-main", 0, 12, 12, 72));
        Assert.Equal(bytes, ProjectContractJson.Serialize(shared));
    }

    [Theory]
    [InlineData(.25, 6, 1, 4, 12)]
    [InlineData(4, 96, 4, 24, 5)]
    public void SupportedSpeedExtremes_UseSourceRangeForActualDuration(double speed, double originalEnd, double start, double end, int duration)
    {
        var project = SetClip(Project(), "clip-main", row => row with { Speed = speed, SourceOutFrame = originalEnd });
        var trimmed = BackendTrim.Apply(project, false, "clip-main", start, end, 12, 144);
        Assert.Equal(duration, trimmed.Montage.Clips[0].DurationFrames);
        Assert.Equal(speed, trimmed.Montage.Clips[0].Speed);
    }

    [Theory]
    [InlineData(.249)]
    [InlineData(4.001)]
    [InlineData(double.NaN)]
    public void UnsupportedSavedSpeedCannotBeTrimmed(double speed)
    {
        var project = SetClip(Project(), "clip-main", row => row with { Speed = speed });
        Assert.Throws<InvalidTrimException>(() => BackendTrim.Apply(project, false, "clip-main", 0, 12, 12, 72));
        Assert.Equal(24, project.Montage.Clips[0].DurationFrames);
    }

    private static ProjectContract SetClip(ProjectContract project, string id, Func<ClipContract, ClipContract> change) => project with
    { Montage = project.Montage with { Clips = project.Montage.Clips.Select(row => row.Id == id ? change(row) : row).ToArray() } };

    private static ProjectContract Three()
    {
        var project = Project(true); var action = project.Segments[0].Actions[0]; var clip = project.Montage.Clips[0];
        return project with
        {
            Segments = project.Segments.Select(segment => segment.Id == "segment-main" ? segment with { Actions =
                [action, action with { Id = "action-B" }, action with { Id = "action-C", AnchorFrame = 84 }] } : segment).ToArray(),
            Montage = project.Montage with { Clips = [.. project.Montage.Clips,
                clip with { Id = "clip-B", ActionId = "action-B", StartFrame = 48 },
                clip with { Id = "clip-C", ActionId = "action-C", StartFrame = 84, AnchorFrame = 84 }] }
        };
    }
}
