using System.Text.Json;
using TrueMoon.Tales.Studio.Backend;
using TrueMoon.Tales.Studio.Contracts.Projects;
using Xunit;

namespace TrueMoon.Tales.Studio.Tests.Backend;

public sealed class BackendFrameNotesTests
{
    private static readonly string Hash = new('A', 64);

    [Fact]
    public void Locate_UsesNativeFrameAfterMoveAndFractionalTrim()
    {
        var (project, clip, anchor) = Fixture();
        Assert.Equal(new FrameNoteLocation(13, 3, "scene"), BackendFrameNotes.Locate(project, anchor));
        Assert.Equal(new FrameNoteLocation(43, 3, "scene"), BackendFrameNotes.Locate(Update(project, clip with { StartFrame = 40 }), anchor));
        var shifted = clip with { SourceInFrame = 5.75, DurationFrames = 8, StartFrame = 11 };
        Assert.Equal(new FrameNoteLocation(13, 2, "scene"), BackendFrameNotes.Locate(Update(project, shifted), anchor));
        Assert.Null(BackendFrameNotes.Locate(Update(project, clip with { DurationFrames = 3 }), anchor));
        Assert.Null(BackendFrameNotes.Locate(Update(project, clip with { Speed = 3 }), anchor));
        Assert.Null(BackendFrameNotes.Locate(Update(project, clip with { TakeId = "other-take" }), anchor));
    }

    [Fact]
    public async Task Apply_RevalidatesObservationAndEditsTextWithoutChangingAnchor()
    {
        var (project, clip, anchor) = Fixture();
        var command = new SaveFrameNote(null, anchor, "  Положение руки\r\nна кадре.  ");
        var error = await Assert.ThrowsAsync<ScenarioEditException>(() => BackendFrameNotes.ApplyAsync(project,
            command, (_, _) => Task.FromResult(new BackendMediaReply
            { AssetId = "asset", FrameIndex = 10, Fps = 30, PixelSha256 = new string('B', 64) })));
        Assert.Equal("frame_note_unavailable", error.Code);
        Assert.Empty(BackendFrameNotes.Read(project));

        var decoded = Assert.IsType<SaveFrameNote>(ScenarioCommand.Decode(ScenarioCommand.Encode(command)));
        var saved = await BackendFrameNotes.ApplyAsync(project, decoded, (observed, location) =>
        {
            Assert.Equal(anchor, observed);
            Assert.Equal(new FrameNoteLocation(13, 3, "scene"), location);
            return Task.FromResult(new BackendMediaReply { AssetId = "asset", FrameIndex = 10, Fps = 30, PixelSha256 = Hash });
        });
        var note = Assert.Single(BackendFrameNotes.Read(saved));
        Assert.Equal(("Положение руки\nна кадре.", anchor), (note.Text, note.Anchor));
        Assert.Same(project.Montage, saved.Montage);
        Assert.Equal("сохранить", saved.Properties["opaque"].GetString());
        var stale = Update(saved, clip with { DurationFrames = 3 });
        var edit = new SaveFrameNote(note.Id, null, "Нужно проверить заново.");
        var edited = await BackendFrameNotes.ApplyAsync(stale, edit, NoDecode);
        Assert.Equal(note with { Text = edit.Text }, Assert.Single(BackendFrameNotes.Read(edited)));
        Assert.Same(edited, await BackendFrameNotes.ApplyAsync(edited, edit, NoDecode));
    }

    [Fact]
    public async Task Apply_RejectsUnobservedPositionAndAnchorReplacement()
    {
        var (project, _, anchor) = Fixture();
        var position = await Assert.ThrowsAsync<ScenarioEditException>(() =>
            BackendFrameNotes.ApplyAsync(project, new(null, anchor with { OriginalFrame = 14 }, "Текст"), NoDecode));
        Assert.Equal("frame_note_unavailable", position.Code);
        var missing = await Assert.ThrowsAsync<ScenarioEditException>(() =>
            BackendFrameNotes.ApplyAsync(project, new("unknown", anchor, "Текст"), NoDecode));
        Assert.Equal("invalid_frame_note", missing.Code);
    }

    [Fact]
    public void StillVersion_UsesOriginalFileHashRegardlessOfDecodedPixels()
    {
        var anchor = new FrameNoteAnchor("window", null, "action", null, null, 10, 0, 0, 0, Hash);
        Assert.True(anchor.Matches(new() { SourceSha256 = Hash, PixelSha256 = new string('B', 64) }));
        Assert.False(anchor.Matches(new() { SourceSha256 = new string('B', 64), PixelSha256 = Hash }));
    }

    private static Task<BackendMediaReply> NoDecode(FrameNoteAnchor _, FrameNoteLocation __) =>
        throw new InvalidOperationException("This operation must not decode a frame.");

    private static ProjectContract Update(ProjectContract project, ClipContract clip) =>
        project with { Montage = project.Montage with { Clips = [clip] } };

    private static (ProjectContract Project, ClipContract Clip, FrameNoteAnchor Anchor) Fixture()
    {
        var clip = new ClipContract
        {
            Id = "clip", WindowId = "window", SourceId = "source", TakeId = "take",
            StartFrame = 10, DurationFrames = 9, SourceInFrame = 4.5, Speed = 1.25
        };
        var project = new ProjectContract
        {
            Id = "project",
            Fps = 24,
            Sources = [new() { Id = "source", Takes = [new() { Id = "take", VideoAssetId = "asset" }, new() { Id = "other-take", VideoAssetId = "asset" }] }],
            Segments = [new() { Id = "scene" }],
            Montage = new()
            {
                Windows = [new() { Id = "window", SegmentId = "scene", StartFrame = 0, DurationFrames = 100 }],
                Clips = [clip]
            },
            Properties = new Dictionary<string, JsonElement> { ["opaque"] = JsonSerializer.SerializeToElement("сохранить") }
        };
        return (project, clip, new("window", "clip", null, "take", "asset", 13, 3, 10, 30, Hash));
    }
}
