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
    public async Task Split_PreservesActionAndSourceBoundaryAtNonUnitySpeed()
    {
        using var files = new BackendWriteFixture(scenario: true);
        using var service = files.Service();
        await files.MigrateAsync(service);
        var project = (await files.Files.Store().OpenAsync()).Project;
        var originals = project.Montage.Clips;
        var main = originals.Single(row => row.Id == "clip-main");
        var fast = main with { Speed = 1.25, SourceInFrame = 4, SourceOutFrame = 34, MuteNativeAudio = true };
        project = project with { Montage = project.Montage with { Clips = [fast, .. originals.Where(row => row.Id != main.Id)] } };
        var edited = BackendMontageEdits.Split(project, new(main.Id, main.StartFrame + 9));
        var halves = edited.Montage.Clips.Where(row => row.WindowId == main.WindowId).ToArray();
        Assert.NotNull(main.ActionId);
        Assert.Collection(halves,
            left => { Assert.Equal(main.Id, left.Id); Assert.Equal(9, left.DurationFrames); Assert.Equal(15.25d, left.SourceOutFrame); },
            right => { Assert.NotEqual(main.Id, right.Id); Assert.Equal(15, right.DurationFrames); Assert.Equal(15.25d, right.SourceInFrame); Assert.Equal(34d, right.SourceOutFrame); });
        Assert.All(halves, clip =>
        {
            Assert.Equal((main.ActionId, main.SourceId, main.TakeId), (clip.ActionId, clip.SourceId, clip.TakeId));
            Assert.Equal(1.25, clip.Speed);
            Assert.True(clip.MuteNativeAudio);
        });
        Assert.Same(project.Segments, edited.Segments);
        Assert.Equal(project.Montage.Windows, edited.Montage.Windows);
    }

    [Fact]
    public async Task ClipEdits_RejectChangesThatWouldOrphanBoundSound_AndProtectedRemoval()
    {
        using var files = new BackendWriteFixture(scenario: true);
        using var service = files.Service();
        await files.MigrateAsync(service);
        var project = (await files.Files.Store().OpenAsync()).Project;
        var main = project.Montage.Clips.Single(row => row.Id == "clip-main");
        using var sound = JsonDocument.Parse("""[{"id":"voice","clip_id":"clip-main","offset_frames":4,"duration_frames":6}]""");
        var montage = project.Montage with
        {
            Properties = new Dictionary<string, JsonElement>(project.Montage.Properties) { ["sound_events"] = sound.RootElement.Clone() }
        };
        var withSound = project with { Montage = montage };
        Assert.Equal("montage_clip_sound", Assert.Throws<ScenarioEditException>(() =>
            BackendMontageEdits.Split(withSound, new(main.Id, main.StartFrame + 9))).Code);
        Assert.Equal("montage_clip_sound", Assert.Throws<ScenarioEditException>(() => BackendMontageEdits.Remove(withSound, new(main.Id))).Code);
        var safe = BackendMontageEdits.Split(withSound, new(main.Id, main.StartFrame + 10));
        Assert.Equal(sound.RootElement.GetRawText(), safe.Montage.Properties["sound_events"].GetRawText());
        var protectedProject = project with { Montage = montage with
            { Clips = montage.Clips.Select(row => row.Id == main.Id ? row with { GapProtected = true } : row).ToArray() } };
        Assert.Equal("montage_protected_clip", Assert.Throws<ScenarioEditException>(() => BackendMontageEdits.Remove(protectedProject, new(main.Id))).Code);
    }

    [Fact]
    public void Commands_RejectUnknownOrDuplicateFields()
    {
        Assert.Throws<JsonException>(() => ScenarioCommand.Decode("""{"operation":"save_segment","unknown":1}"""));
        Assert.Throws<JsonException>(() => ScenarioCommand.Decode("""{"operation":"save_segment","name":"one","name":"two"}"""));
    }
}
