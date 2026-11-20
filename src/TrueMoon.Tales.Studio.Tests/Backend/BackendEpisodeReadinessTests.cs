using TrueMoon.Tales.Studio.Backend;
using TrueMoon.Tales.Studio.Contracts.Projects;
using Xunit;

namespace TrueMoon.Tales.Studio.Tests.Backend;

public sealed class BackendEpisodeReadinessTests
{
    [Theory]
    [InlineData(0, true)]
    [InlineData(4319, true)]
    [InlineData(4320, false)]
    [InlineData(7200, false)]
    [InlineData(7201, true)]
    public void EpisodeReadiness_ClassifiesDurationOnTheProjectFrameGrid(int frames, bool note)
    {
        var project = new ProjectContract { Id = "episode" } with
        {
            Montage = new() { Windows = frames == 0 ? [] : [new() { Id = "window", DurationFrames = frames }] }
        };
        var issues = new List<BackendReadinessIssue>();
        var summary = BackendEpisodeReadiness.Collect(project, new('A', 64), issues);
        Assert.Equal(frames, summary.Frames);
        Assert.Equal(note, issues.Any(row => row.Reason == "episode_duration"));
    }

    [Theory]
    [InlineData("unchanged", "unchanged", "", null)]
    [InlineData("out", "in", "bridge", null)]
    [InlineData("out", "in", "", "episode_join_bridge_missing")]
    [InlineData("out", "", "", "episode_join_states_missing")]
    public void EpisodeReadiness_UsesTheSavedScenePairAndNamesTheMissingJoinField(string exit, string entry,
        string bridge, string? issue)
    {
        var project = new ProjectContract
        {
            Id = "episode", Revision = 19,
            Montage = new()
            {
                Windows = [new() { Id = "left", SegmentId = "scene-a", StartFrame = 0, DurationFrames = 2880 },
                    new() { Id = "right", SegmentId = "scene-b", StartFrame = 2880, DurationFrames = 1920 }]
            }
        };
        project = BackendSceneJoins.Apply(project, new("left", "right", exit, entry, bridge));
        var issues = new List<BackendReadinessIssue>();
        var summary = BackendEpisodeReadiness.Collect(project, new('A', 64), issues);
        var rows = issues.Where(row => row.Reason.StartsWith("episode_join_", StringComparison.Ordinal)).ToArray();
        Assert.Equal(1, summary.Joins);
        if (issue is null)
        {
            Assert.Empty(rows);
            Assert.Equal(1, summary.DocumentedJoins);
        }
        else
        {
            var row = Assert.Single(rows);
            Assert.Equal((issue, "right", 2880), (row.Reason, row.WindowId, row.Frame));
            Assert.True(row.Valid);
            Assert.Equal(0, summary.DocumentedJoins);
        }
    }
}
