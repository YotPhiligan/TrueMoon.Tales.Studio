using System.Text.Json;
using System.Text.Json.Nodes;
using TrueMoon.Tales.Studio.Contracts.Projects;
using TrueMoon.Tales.Studio.Contracts.Compatibility;
using Xunit;

namespace TrueMoon.Tales.Studio.Tests.Contracts;

public sealed class LegacyProjectReaderTests
{
    [Fact]
    public void Read_UnicodeBomAndPlaceholder_PreserveLegacySourceAndAction()
    {
        var fixture = Fixture();
        var clip = fixture["timeline"]!["clips"]![1]!;
        clip["take_id"] = null;
        clip["source_in_frame"] = 0;
        clip["source_out_frame"] = null;
        clip["speed"] = 1;

        var result = LegacyProjectReader.Read("\uFEFF" + fixture.ToJsonString());

        var actual = result.Project.Montage.Clips.Single(c => c.Id == "clip-a");
        Assert.Null(actual.TakeId);
        Assert.Equal("clip-a", actual.ActionId);
        Assert.Equal("source-a", actual.SourceId);
        Assert.Equal(24, actual.DurationFrames);
        Assert.Equal("Смысловой сегмент", Assert.Single(result.Project.Segments).Name);
    }

    [Fact]
    public void Read_ActiveScenario_KeepsIntendedActionAndSavedMontageIndependent()
    {
        var fixture = Fixture();
        fixture["timeline"]!["scenario"]!["segments"]![0]!["items"]![0]!["duration_frames"] = 200;

        var result = LegacyProjectReader.Read(fixture.ToJsonString());

        Assert.True(result.HasScenario);
        Assert.Equal(1, result.Project.Version);
        Assert.Equal("project-legacy", result.Project.Id);
        Assert.Equal(11, result.Project.Revision);
        var segment = Assert.Single(result.Project.Segments);
        Assert.Equal("window-saved", segment.Id);
        Assert.Equal("Смысловой сегмент", segment.Name);
        Assert.Equal(120, segment.PlannedWindow.DurationFrames);
        var action = segment.Actions.Single(a => a.Id == "clip-a");
        Assert.Equal(200, action.PlannedDurationFrames);
        Assert.Equal("source-a", Assert.Single(action.Sources).SourceId);
        Assert.Equal(12, action.GapBeforeFrames);
        Assert.Equal(12, action.AnchorFrame);
        Assert.True(action.GapProtected);
        Assert.Equal("Звук события", action.SoundNotes);
        Assert.Equal("Стык события", action.ContinuityNotes);
        var clip = result.Project.Montage.Clips.Single(c => c.Id == "clip-a");
        Assert.Equal("clip-a", clip.ActionId);
        Assert.Equal("window-saved", clip.WindowId);
        Assert.Equal(12, clip.StartFrame);
        Assert.Equal(24, clip.DurationFrames);
        Assert.Equal(1.25, clip.SourceInFrame);
        Assert.Equal(49.25, clip.SourceOutFrame);
        Assert.Equal(2, clip.Speed);
        Assert.True(clip.MuteNativeAudio);
        Assert.Equal(12, clip.AnchorFrame);
        Assert.True(clip.GapProtected);
        Assert.Equal(120, Assert.Single(result.Project.Montage.Windows).DurationFrames);
        Assert.Equal(200, result.OriginalDocument.GetProperty("timeline").GetProperty("scenario")
            .GetProperty("segments")[0].GetProperty("items")[0].GetProperty("duration_frames").GetInt32());
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void Read_AbsentScenario_AdoptsOnlyPlacedClipsInTimeOrder(bool explicitNull)
    {
        var fixture = Fixture(false);
        if (explicitNull) fixture["timeline"]!["scenario"] = null;

        var result = LegacyProjectReader.Read(fixture.ToJsonString());

        Assert.False(result.HasScenario);
        Assert.Empty(result.Issues);
        var segment = Assert.Single(result.Project.Segments);
        Assert.Equal("Монтажный сегмент", segment.Name);
        Assert.Equal(["clip-a", "clip-b"], segment.Actions.Select(a => a.Id));
        Assert.Equal([12, 12], segment.Actions.Select(a => a.GapBeforeFrames));
        Assert.Equal(["source-a", "source-b"], segment.Actions.Select(a => Assert.Single(a.Sources).SourceId));
        Assert.Equal(3, result.Project.Sources.Length);
        Assert.DoesNotContain(segment.Actions, a => a.Sources.Any(s => s.SourceId == "source-unused"));
        Assert.Equal(["clip-b", "clip-a"], result.Project.Montage.Clips.Select(c => c.Id));
        Assert.Equal("clip-a", result.Project.Montage.Clips.Single(c => c.Id == "clip-a").ActionId);
    }

    [Fact]
    public void Read_MissingTimeline_KeepsProductionGroupsOutOfScenario()
    {
        var fixture = Fixture();
        fixture.Remove("timeline");

        var result = LegacyProjectReader.Read(fixture.ToJsonString());

        Assert.False(result.HasScenario);
        Assert.Empty(result.Project.Segments);
        Assert.Empty(result.Project.Montage.Windows);
        Assert.Empty(result.Project.Montage.Clips);
        Assert.Equal(["source-a", "source-b", "source-unused"], result.Project.Sources.Select(s => s.Id));
        Assert.Equal("production-group", result.OriginalDocument.GetProperty("segments")[0].GetProperty("id").GetString());
        Assert.False(result.OriginalDocument.TryGetProperty("timeline", out _));
    }

    [Fact]
    public void Read_UnplacedStoryWindow_PreservesUnknownTimesAndNoInventedMontage()
    {
        var fixture = Fixture();
        var segment = fixture["timeline"]!["scenario"]!["segments"]![0]!;
        segment["start_frame"] = null;
        segment["duration_frames"] = null;
        segment["items"]![0]!["duration_frames"] = null;
        fixture["timeline"]!["segments"] = new JsonArray();
        fixture["timeline"]!["clips"] = new JsonArray();

        var result = LegacyProjectReader.Read(fixture.ToJsonString());

        Assert.True(result.HasScenario);
        var story = Assert.Single(result.Project.Segments);
        Assert.Null(story.PlannedWindow.StartFrame);
        Assert.Null(story.PlannedWindow.DurationFrames);
        Assert.Null(story.Actions[0].PlannedDurationFrames);
        Assert.Equal("source-a", Assert.Single(story.Actions[0].Sources).SourceId);
        Assert.Empty(result.Project.Montage.Windows);
        Assert.Empty(result.Project.Montage.Clips);
    }

    [Theory]
    [InlineData("removed")]
    [InlineData("rebound")]
    [InlineData("moved")]
    public void Read_StaleMontageAction_IsDetachedWithDiagnostic(string change)
    {
        var fixture = Fixture();
        var segments = fixture["timeline"]!["scenario"]!["segments"]!.AsArray();
        var items = segments[0]!["items"]!.AsArray();
        var item = items[0]!.DeepClone();
        if (change == "rebound") items[0]!["block_id"] = "source-b";
        else items.RemoveAt(0);
        if (change == "moved")
            segments.Add(new JsonObject { ["id"] = "other-story", ["name"] = "Другое действие", ["description"] = "",
                ["start_frame"] = null, ["duration_frames"] = null, ["color"] = "blue", ["items"] = new JsonArray(item) });

        var result = LegacyProjectReader.Read(fixture.ToJsonString());

        var clip = result.Project.Montage.Clips.Single(c => c.Id == "clip-a");
        Assert.Null(clip.ActionId);
        Assert.Equal("source-a", clip.SourceId);
        Assert.Equal(12, clip.StartFrame);
        Assert.Equal(24, clip.DurationFrames);
        Assert.Contains(result.Issues, issue => issue.Code == "unlinked_clip_action" && issue.EntityId == "clip-a");
        if (change == "removed") Assert.DoesNotContain(result.Project.Segments.SelectMany(s => s.Actions), a => a.Id == "clip-a");
    }

    [Fact]
    public void Read_StaleMontageWindow_PreservesWindowWithoutInventingStorySegment()
    {
        var fixture = Fixture();
        fixture["timeline"]!["scenario"]!["segments"] = new JsonArray(new JsonObject
        {
            ["id"] = "unplaced-story", ["name"] = "Черновик", ["description"] = "",
            ["color"] = "mint", ["start_frame"] = null, ["duration_frames"] = null, ["items"] = new JsonArray()
        });

        var result = LegacyProjectReader.Read(fixture.ToJsonString());

        Assert.Equal("unplaced-story", Assert.Single(result.Project.Segments).Id);
        var window = Assert.Single(result.Project.Montage.Windows);
        Assert.Equal("window-saved", window.Id);
        Assert.Null(window.SegmentId);
        Assert.Equal(120, window.DurationFrames);
        Assert.All(result.Project.Montage.Clips, c => Assert.Null(c.ActionId));
        Assert.Contains(result.Issues, issue => issue.Code == "unlinked_window_segment" && issue.EntityId == "window-saved");
    }

    [Fact]
    public void Read_OriginalDocument_PreservesUnknownHistoryAndPathsAfterInputIsDisposed()
    {
        var fixture = Fixture();
        LegacyProjectReadResult result;
        using (var input = JsonDocument.Parse(fixture.ToJsonString())) result = LegacyProjectReader.Read(input.RootElement);

        var original = result.OriginalDocument;
        Assert.True(JsonNode.DeepEquals(fixture, JsonNode.Parse(original.GetRawText())));
        Assert.Equal("@projects/Тест/Studio.project.json", original.GetProperty("root").GetString());
        Assert.Equal(9007199254740993, original.GetProperty("future").GetProperty("precise_integer").GetInt64());
        Assert.Equal("r000010.json", original.GetProperty("history")[0].GetString());
        Assert.Equal(10, original.GetProperty("activity")[0].GetProperty("revision").GetInt32());
        Assert.Equal("old-clip", original.GetProperty("timeline").GetProperty("undo")[0]
            .GetProperty("before").GetProperty("clips")[0].GetProperty("id").GetString());
        Assert.Equal("@arts/Characters/Example/Опора.png", original.GetProperty("assets").GetProperty("ref-image").GetProperty("path").GetString());
        var source = result.Project.Sources.Single(s => s.Id == "source-a");
        Assert.Equal("18446744073709551615", source.Properties["settings"].GetProperty("seed").GetString());
        Assert.Equal("Неизменяемая постановка", source.Properties["prompt"].GetString());
        Assert.Equal("@videos/Artifacts/native.safetensors", Assert.Single(source.Takes).Properties["native_av_path"].GetString());
    }

    [Theory]
    [InlineData("project")]
    [InlineData("timeline")]
    [InlineData("scenario")]
    public void Read_UnsupportedSchemaVersions_AreRejected(string scope)
    {
        var fixture = Fixture();
        if (scope == "project") fixture["schema_version"] = 2;
        else if (scope == "timeline") fixture["timeline"]!["version"] = 2;
        else fixture["timeline"]!["scenario"]!["version"] = 2;

        Assert.Throws<NotSupportedException>(() => LegacyProjectReader.Read(fixture.ToJsonString()));
    }

    [Theory]
    [InlineData("[]")]
    [InlineData("{}")]
    [InlineData("{\"schema_version\":true}")]
    [InlineData("{\"schema_version\":\"1\"}")]
    public void Read_MalformedDocumentOrVersionKind_IsRejected(string json)
    {
        Assert.Throws<JsonException>(() => LegacyProjectReader.Read(json));
    }

    [Theory]
    [InlineData("sources")]
    [InlineData("takes")]
    [InlineData("items")]
    public void Read_NullCollections_AreRejected(string collection)
    {
        var fixture = Fixture();
        if (collection == "sources") fixture["blocks"] = null;
        else if (collection == "takes") fixture["blocks"]![0]!["takes"] = null;
        else fixture["timeline"]!["scenario"]!["segments"]![0]!["items"] = null;

        Assert.Throws<JsonException>(() => LegacyProjectReader.Read(fixture.ToJsonString()));
    }

    [Theory]
    [InlineData("clip_source")]
    [InlineData("clip_take")]
    [InlineData("clip_window")]
    [InlineData("action_source")]
    [InlineData("duplicate_source")]
    public void Read_BrokenReferences_AreRejected(string error)
    {
        var fixture = Fixture();
        switch (error)
        {
            case "clip_source": fixture["timeline"]!["clips"]![0]!["block_id"] = "missing"; break;
            case "clip_take": fixture["timeline"]!["clips"]![0]!["take_id"] = "missing"; break;
            case "clip_window": fixture["timeline"]!["clips"]![0]!["segment_id"] = "missing"; break;
            case "action_source": fixture["timeline"]!["scenario"]!["segments"]![0]!["items"]![0]!["block_id"] = "missing"; break;
            case "duplicate_source": fixture["blocks"]!.AsArray().Add(fixture["blocks"]![0]!.DeepClone()); break;
        }

        Assert.Throws<JsonException>(() => LegacyProjectReader.Read(fixture.ToJsonString()));
    }

    private static JsonObject Fixture(bool scenario = true)
    {
        var root = JsonNode.Parse("""
            {
              "schema_version":1,"id":"project-legacy","name":"Тестовый проект","revision":11,"fps":24,
              "root":"@projects/Тест/Studio.project.json","history":["r000010.json"],
              "activity":[{"revision":10,"actor":"author","action":"История"}],
              "future":{"precise_integer":9007199254740993,"payload":[null,true,{"key":"значение"}]},
              "segments":[{"id":"production-group","name":"Библиотека","block_ids":["source-a","source-b","source-unused"]}],
              "assets":{"ref-image":{"path":"@arts/Characters/Example/Опора.png"},"video-a":{"path":"E:/fixtures/Дубль A.mp4"},"video-b":{"path":"@videos/Fixtures/B.mp4"}},
              "blocks":[
                {"id":"source-a","label":"A","title":"Общий план","prompt":"Неизменяемая постановка","settings":{"seed":"18446744073709551615"},"guides":[{"frame":-1,"reference_id":"ref-image"}],
                 "takes":[{"id":"take","video_asset_id":"video-a","native_av_path":"@videos/Artifacts/native.safetensors","technical_status":"success","review_state":"accepted"}]},
                {"id":"source-b","label":"B","title":"Крупный план","takes":[{"id":"take","video_asset_id":"video-b"}]},
                {"id":"source-unused","label":"Unused","title":"Вне сценария","takes":[]}
              ],
              "timeline":{"version":1,"fps":24,
                "segments":[{"id":"window-saved","name":"Монтажный сегмент","color":"mint","start_frame":0,"duration_frames":120}],
                "clips":[
                  {"id":"clip-b","segment_id":"window-saved","block_id":"source-b","take_id":"take","start_frame":48,"duration_frames":24,"source_in_frame":2},
                  {"id":"clip-a","segment_id":"window-saved","block_id":"source-a","take_id":"take","story_title":"Событие A","start_frame":12,"duration_frames":24,"source_in_frame":1.25,"source_out_frame":49.25,"speed":2,"anchor_frame":12,"gap_protected":true,"mute_native_audio":true}
                ],
                "markers":[{"id":"beat","name":"Акцент","frame":24}],"music":{"asset_id":"score","gain":0.8},
                "undo":[{"before":{"clips":[{"id":"old-clip"}]}}],"redo":[],"sound_events":[{"id":"sound","tail":"preserve"}]
              }
            }
            """)!.AsObject();
        if (scenario)
            root["timeline"]!["scenario"] = JsonNode.Parse("""
                {"version":1,"segments":[
                  {"id":"window-saved","name":"Смысловой сегмент","description":"Содержание","color":"mint","segment_type":"action","start_frame":0,"duration_frames":120,
                   "items":[
                    {"id":"clip-a","block_id":"source-a","title":"Действие A","description":"Смысл A","duration_frames":24,"gap_before_frames":12,"take_id":"take","source_in_frame":1.25,"source_out_frame":49.25,"speed":2,"anchor_frame":12,"gap_protected":true,"sound_notes":"Звук события","continuity_notes":"Стык события"},
                    {"id":"clip-b","block_id":"source-b","title":"Действие B","description":"Смысл B","duration_frames":24,"gap_before_frames":12,"take_id":"take","source_in_frame":2}
                   ]}
                ]}
                """);
        return root;
    }
}
