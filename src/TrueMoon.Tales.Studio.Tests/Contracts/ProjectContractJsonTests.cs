using System.Text.Json;
using System.Text.Json.Nodes;
using TrueMoon.Tales.Studio.Contracts.Projects;
using TrueMoon.Tales.Studio.Contracts.Serialization;
using Xunit;

namespace TrueMoon.Tales.Studio.Tests.Contracts;

public sealed class ProjectContractJsonTests
{
    [Fact]
    public void Json_RoundTrip_PreservesMultipleSourcesReusableBindingsAndIndependentClipIds()
    {
        var json = ProjectContractJson.Serialize(Project());
        var actual = ProjectContractJson.Deserialize(json);
        using var document = JsonDocument.Parse(json);

        Assert.Equal(1, document.RootElement.GetProperty("contract_version").GetInt32());
        Assert.False(document.RootElement.TryGetProperty("Version", out _));
        Assert.Equal("project-qa", actual.Id);
        Assert.Equal(7, actual.Revision);
        Assert.Equal(24, actual.Fps);
        var segment = Assert.Single(actual.Segments);
        Assert.Equal("segment-story", segment.Id);
        Assert.Equal(["source-a", "source-b"], segment.Actions[0].Sources.Select(s => s.SourceId));
        Assert.Equal("reaction", segment.Actions[0].Sources[1].Purpose);
        Assert.Equal("source-a", Assert.Single(segment.Actions[1].Sources).SourceId);
        Assert.Equal(["clip-a1", "clip-b", "clip-a2"], actual.Montage.Clips.Select(c => c.Id));
        Assert.All(actual.Montage.Clips, clip => Assert.Equal("action-main", clip.ActionId));
        Assert.All(actual.Montage.Clips, clip => Assert.NotEqual(clip.Id, clip.ActionId));
        Assert.Equal(["source-a", "source-b", "source-a"], actual.Montage.Clips.Select(c => c.SourceId));
        Assert.Equal(1.25, actual.Montage.Clips[0].SourceInFrame);
        Assert.Equal(49.25, actual.Montage.Clips[0].SourceOutFrame);
        Assert.Equal(2, actual.Montage.Clips[0].Speed);
        Assert.Equal(72, segment.Actions[0].PlannedDurationFrames);
        Assert.Equal([12, 48, 84], actual.Montage.Clips.Select(c => c.StartFrame));
        Assert.Equal("18446744073709551615", actual.Sources[0].Properties["seed"].GetString());
        Assert.Equal("@videos/Clips/Дубль A.mp4", actual.Sources[0].Takes[0].Properties["original_path"].GetString());
    }

    [Fact]
    public void Json_TextAction_PreservesUnknownPlanWithoutMaterialOrMontage()
    {
        var project = Project() with
        {
            Sources = [],
            Segments =
            [
                new()
                {
                    Id = "segment-text", Name = "Замысел", Description = "Пока без материала",
                    PlannedWindow = new(),
                    Actions = [new() { Id = "action-text", Title = "Событие", Description = "Экранное действие" }]
                }
            ],
            Montage = new()
        };

        var actual = ProjectContractJson.Deserialize(ProjectContractJson.Serialize(project));

        var segment = Assert.Single(actual.Segments);
        var action = Assert.Single(segment.Actions);
        Assert.Equal("action-text", action.Id);
        Assert.Null(segment.PlannedWindow.StartFrame);
        Assert.Null(segment.PlannedWindow.DurationFrames);
        Assert.Null(action.PlannedDurationFrames);
        Assert.Empty(action.Sources);
        Assert.Empty(actual.Sources);
        Assert.Empty(actual.Montage.Windows);
        Assert.Empty(actual.Montage.Clips);
    }

    [Fact]
    public void Json_TakeIds_AreScopedToTheirOwningSource()
    {
        var actual = ProjectContractJson.Deserialize(ProjectContractJson.Serialize(Project()));

        Assert.Equal(["take", "take"], actual.Sources.Select(s => Assert.Single(s.Takes).Id));
        Assert.Equal(["video-a", "video-b"], actual.Sources.Select(s => Assert.Single(s.Takes).VideoAssetId));
        Assert.Equal("source-b", actual.Montage.Clips.Single(c => c.Id == "clip-b").SourceId);
    }

    [Fact]
    public void Json_Placeholder_PreservesActionWithoutVideoTiming()
    {
        var project = Project();
        var clip = project.Montage.Clips[0] with { TakeId = null, SourceInFrame = 0, SourceOutFrame = null, Speed = 1 };
        project = project with { Montage = project.Montage with { Clips = [clip] } };

        var actual = Assert.Single(ProjectContractJson.Deserialize(ProjectContractJson.Serialize(project)).Montage.Clips);

        Assert.Null(actual.TakeId);
        Assert.Null(actual.SourceOutFrame);
        Assert.Equal(0, actual.SourceInFrame);
        Assert.Equal("action-main", actual.ActionId);
        Assert.Equal(24, actual.DurationFrames);
    }

    [Theory]
    [InlineData(.25)]
    [InlineData(4)]
    public void Json_SpeedBounds_AcceptExactEndpoints(double speed)
    {
        var project = Project();
        project = project with { Montage = project.Montage with { Clips =
            [project.Montage.Clips[0] with { Speed = speed, SourceOutFrame = 1.25 + 24 * speed }] } };

        var actual = Assert.Single(ProjectContractJson.Deserialize(ProjectContractJson.Serialize(project)).Montage.Clips);

        Assert.Equal(speed, actual.Speed);
        Assert.Equal(1.25 + 24 * speed, actual.SourceOutFrame);
    }

    [Theory]
    [InlineData(.249)]
    [InlineData(4.001)]
    public void Json_SpeedBounds_RejectAdjacentOutsideValues(double speed)
    {
        var project = Project();
        project = project with { Montage = project.Montage with { Clips = [project.Montage.Clips[0] with { Speed = speed }] } };

        Assert.Throws<JsonException>(() => ProjectContractJson.Serialize(project));
    }

    [Theory]
    [InlineData("")]
    [InlineData("[]")]
    [InlineData("{}")]
    [InlineData("{\"contract_version\":true}")]
    [InlineData("{\"contract_version\":\"1\"}")]
    [InlineData("{\"contract_version\":1.5}")]
    public void Json_MalformedOrMissingVersion_IsRejected(string json)
    {
        Assert.ThrowsAny<JsonException>(() => ProjectContractJson.Deserialize(json));
    }

    [Fact]
    public void Json_UnknownWireMember_IsRejectedInsteadOfSilentlyDropped()
    {
        var node = JsonNode.Parse(ProjectContractJson.Serialize(Project()))!;
        node["future_wire_member"] = true;

        Assert.Throws<JsonException>(() => ProjectContractJson.Deserialize(node.ToJsonString()));
    }

    [Theory]
    [InlineData("project_sources")]
    [InlineData("source_takes")]
    [InlineData("montage_clips")]
    [InlineData("null_source")]
    public void Json_NullCollectionsAndItems_AreRejected(string error)
    {
        var project = Project();
        project = error switch
        {
            "project_sources" => project with { Sources = null! },
            "source_takes" => project with { Sources = [project.Sources[0] with { Takes = null! }, project.Sources[1]] },
            "montage_clips" => project with { Montage = project.Montage with { Clips = null! } },
            "null_source" => project with { Sources = [null!] },
            _ => throw new ArgumentOutOfRangeException(nameof(error))
        };

        Assert.Throws<JsonException>(() => ProjectContractJson.Serialize(project));
    }

    [Fact]
    public void Json_OptionalDetachedMontageLinks_PreserveSavedWindowAndClip()
    {
        var project = Project() with
        {
            Segments = [],
            Montage = new()
            {
                Windows = [new() { Id = "window-last-valid", SegmentId = null, StartFrame = 0, DurationFrames = 96 }],
                Clips = [new() { Id = "clip-last-valid", WindowId = "window-last-valid", ActionId = null,
                    SourceId = "source-a", TakeId = "take", StartFrame = 12, DurationFrames = 24 }]
            }
        };

        var actual = ProjectContractJson.Deserialize(ProjectContractJson.Serialize(project));

        Assert.Null(Assert.Single(actual.Montage.Windows).SegmentId);
        var clip = Assert.Single(actual.Montage.Clips);
        Assert.Null(clip.ActionId);
        Assert.Equal("window-last-valid", clip.WindowId);
        Assert.Equal("source-a", clip.SourceId);
        Assert.Equal(12, clip.StartFrame);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(2)]
    public void Json_UnsupportedContractVersion_IsRejected(int version)
    {
        var node = JsonNode.Parse(ProjectContractJson.Serialize(Project()))!;
        node["contract_version"] = version;

        Assert.Throws<NotSupportedException>(() => ProjectContractJson.Deserialize(node.ToJsonString()));
        Assert.Throws<NotSupportedException>(() => ProjectContractJson.Serialize(Project() with { Version = version }));
    }

    [Theory]
    [InlineData("duplicate_source")]
    [InlineData("duplicate_take")]
    [InlineData("duplicate_action")]
    [InlineData("duplicate_clip")]
    [InlineData("duplicate_window")]
    [InlineData("missing_source")]
    [InlineData("missing_take")]
    [InlineData("missing_action")]
    [InlineData("missing_window")]
    [InlineData("missing_segment")]
    [InlineData("null_binding_id")]
    [InlineData("unbound_clip_source")]
    public void Json_InvalidReferencesAndDuplicateIds_AreRejected(string error)
    {
        var project = Project();
        var source = project.Sources[0];
        var segment = project.Segments[0];
        var clip = project.Montage.Clips[0];
        var invalid = error switch
        {
            "duplicate_source" => project with { Sources = [source, source] },
            "duplicate_take" => project with { Sources = [source with { Takes = [source.Takes[0], source.Takes[0]] }, project.Sources[1]] },
            "duplicate_action" => project with { Segments = [segment with { Actions = [segment.Actions[0], segment.Actions[0]] }] },
            "duplicate_clip" => project with { Montage = project.Montage with { Clips = [clip, clip] } },
            "duplicate_window" => project with { Montage = project.Montage with { Windows = [project.Montage.Windows[0], project.Montage.Windows[0]] } },
            "missing_source" => project with { Montage = project.Montage with { Clips = [clip with { SourceId = "missing" }] } },
            "missing_take" => project with { Montage = project.Montage with { Clips = [clip with { TakeId = "missing" }] } },
            "missing_action" => project with { Montage = project.Montage with { Clips = [clip with { ActionId = "missing" }] } },
            "missing_window" => project with { Montage = project.Montage with { Clips = [clip with { WindowId = "missing" }] } },
            "missing_segment" => project with { Montage = project.Montage with { Windows = [project.Montage.Windows[0] with { SegmentId = "missing" }] } },
            "null_binding_id" => project with { Segments = [segment with { Actions = [segment.Actions[0] with { Sources = [new() { SourceId = null! }] }] }] },
            "unbound_clip_source" => project with { Segments = [segment with { Actions = [segment.Actions[0] with { Sources = [new() { SourceId = "source-b" }] }] }] },
            _ => throw new ArgumentOutOfRangeException(nameof(error))
        };

        Assert.Throws<JsonException>(() => ProjectContractJson.Serialize(invalid));
    }

    private static ProjectContract Project() => new()
    {
        Id = "project-qa", Name = "Действие из нескольких склеек", Revision = 7, Fps = 24,
        Sources =
        [
            new()
            {
                Id = "source-a", Label = "A", Title = "Общий план",
                Properties = new Dictionary<string, JsonElement> { ["seed"] = JsonSerializer.SerializeToElement("18446744073709551615") },
                Takes = [new() { Id = "take", VideoAssetId = "video-a", Properties = new Dictionary<string, JsonElement>
                    { ["original_path"] = JsonSerializer.SerializeToElement("@videos/Clips/Дубль A.mp4") } }]
            },
            new() { Id = "source-b", Label = "B", Title = "Крупный план", Takes = [new() { Id = "take", VideoAssetId = "video-b" }] }
        ],
        Segments =
        [
            new()
            {
                Id = "segment-story", Name = "Сегмент", Description = "Содержание",
                PlannedWindow = new() { StartFrame = 0, DurationFrames = 240 },
                Actions =
                [
                    new() { Id = "action-main", Title = "Основное действие", Description = "Один смысл — несколько клипов",
                        PlannedDurationFrames = 72, Sources = [new() { SourceId = "source-a", Purpose = "main" }, new() { SourceId = "source-b", Purpose = "reaction" }] },
                    new() { Id = "action-reuse", Title = "Другое действие", Description = "Повторное использование материала",
                        Sources = [new() { SourceId = "source-a", Purpose = "detail" }] }
                ]
            }
        ],
        Montage = new()
        {
            Windows = [new() { Id = "window-edit", SegmentId = "segment-story", StartFrame = 0, DurationFrames = 240 }],
            Clips =
            [
                new() { Id = "clip-a1", ActionId = "action-main", WindowId = "window-edit", SourceId = "source-a", TakeId = "take",
                    StartFrame = 12, DurationFrames = 24, SourceInFrame = 1.25, SourceOutFrame = 49.25, Speed = 2 },
                new() { Id = "clip-b", ActionId = "action-main", WindowId = "window-edit", SourceId = "source-b", TakeId = "take",
                    StartFrame = 48, DurationFrames = 24, SourceOutFrame = 24 },
                new() { Id = "clip-a2", ActionId = "action-main", WindowId = "window-edit", SourceId = "source-a", TakeId = "take",
                    StartFrame = 84, DurationFrames = 24, SourceInFrame = 52, SourceOutFrame = 76 }
            ]
        }
    };
}
