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
    public async Task ClipEdits_SplitBoundSoundAtItsSourceBoundary_AndRejectProtectedRemoval()
    {
        using var files = new BackendWriteFixture(scenario: true);
        using var service = files.Service();
        await files.MigrateAsync(service);
        var project = (await files.Files.Store().OpenAsync()).Project;
        var main = project.Montage.Clips.Single(row => row.Id == "clip-main");
        using var sound = JsonDocument.Parse("""
            [{"id":"voice","clip_id":"clip-main","asset_id":"video-a","offset_frames":4,"duration_frames":6,
              "source_in_frame":12,"gain":0.4,"muted":false,"notes":{"keep":true}},
             {"id":"late","clip_id":"clip-main","offset_frames":12,"duration_frames":3},
             {"id":"room","clip_id":null,"offset_frames":1,"duration_frames":3}]
            """);
        var montage = project.Montage with
        {
            Properties = new Dictionary<string, JsonElement>(project.Montage.Properties) { ["sound_events"] = sound.RootElement.Clone() }
        };
        var withSound = project with { Montage = montage };
        var split = BackendMontageEdits.Split(withSound, new(main.Id, main.StartFrame + 9));
        var right = split.Montage.Clips.Single(clip => clip.StartFrame == main.StartFrame + 9);
        var rows = split.Montage.Properties["sound_events"].EnumerateArray().ToArray();
        Assert.Equal(4, rows.Length);
        var head = rows.Single(row => row.GetProperty("id").GetString() == "voice");
        var late = rows.Single(row => row.GetProperty("id").GetString() == "late");
        var tail = rows.Single(row => row.GetProperty("id").GetString()!.StartsWith("sound-", StringComparison.Ordinal));
        Assert.Equal((4, 5, 12d), Timing(head));
        Assert.Equal((0, 1, 17d), Timing(tail));
        Assert.Equal(right.Id, tail.GetProperty("clip_id").GetString());
        Assert.Equal(right.Id, late.GetProperty("clip_id").GetString());
        Assert.Equal(3, late.GetProperty("offset_frames").GetInt32());
        Assert.Equal("video-a", tail.GetProperty("asset_id").GetString());
        Assert.Equal(.4, tail.GetProperty("gain").GetDouble());
        Assert.True(tail.GetProperty("notes").GetProperty("keep").GetBoolean());
        var removed = BackendMontageEdits.Remove(split, new(right.Id));
        Assert.Equal(new[] { "voice", "room" }, removed.Montage.Properties["sound_events"].EnumerateArray()
            .Select(row => row.GetProperty("id").GetString()));
        var protectedProject = project with { Montage = montage with
            { Clips = montage.Clips.Select(row => row.Id == main.Id ? row with { GapProtected = true } : row).ToArray() } };
        Assert.Equal("montage_protected_clip", Assert.Throws<ScenarioEditException>(() => BackendMontageEdits.Remove(protectedProject, new(main.Id))).Code);

        static (int, int, double) Timing(JsonElement row) => (row.GetProperty("offset_frames").GetInt32(),
            row.GetProperty("duration_frames").GetInt32(), row.GetProperty("source_in_frame").GetDouble());
    }

    [Fact]
    public void Commands_RejectUnknownOrDuplicateFields()
    {
        Assert.Throws<JsonException>(() => ScenarioCommand.Decode("""{"operation":"save_segment","unknown":1}"""));
        Assert.Throws<JsonException>(() => ScenarioCommand.Decode("""{"operation":"save_segment","name":"one","name":"two"}"""));
    }
}
