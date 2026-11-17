using TrueMoon.Tales.Studio.Backend;
using TrueMoon.Tales.Studio.Tests.Support;
using Xunit;

namespace TrueMoon.Tales.Studio.Tests.Presentation.Scene;

public sealed class BackendPlaybackStateTests
{
    [Fact]
    public void Seek_PreservesProjectFrameWhenSourceSampleCannotRepresentItExactly()
    {
        var media = new FakePreviewMedia();
        using var fixture = new InteractionFixture(media, server =>
            server.Current = server.Current with { SourceInFrame = 1, SourceOutFrame = 11, Speed = 1.25, DurationFrames = 8 });
        fixture.Model.Poll();

        fixture.Model.SeekTimeline(3);
        fixture.Model.Poll();
        // The source sample is floor(1 + 3*1.25) = 4. Recovering local time from it gives 2.
        Assert.Equal(4, fixture.Model.FrameIndex);
        Assert.Equal(3, fixture.Model.TimelineCursor);
        fixture.Model.TogglePlayback();
        Assert.Equal(3, Assert.Single(media.VideoStarts));
        Assert.Equal(3, Assert.Single(media.SoundStarts));
        Assert.Empty(fixture.Server.Posts);
    }

    [Fact]
    public void Playback_RevisionConflictStopsAndRequiresExplicitReload()
    {
        var media = new FakePreviewMedia();
        using var fixture = new InteractionFixture(media);
        fixture.Model.Poll();
        fixture.Model.TogglePlayback();
        media.Videos[0].Error = new BackendCommandException("revision_conflict");

        fixture.Model.Poll();

        Assert.False(fixture.Model.Playing);
        Assert.True(fixture.Model.Stale);
        Assert.True(fixture.Model.HasError);
        Assert.False(fixture.Model.CanPlay);
        Assert.False(fixture.Model.CanBeginMove);
        Assert.True(media.Videos[0].Disposed);
        Assert.True(media.Sounds[0].Disposed);
        fixture.Model.TogglePlayback();
        Assert.Single(media.Videos);
        fixture.Server.Revision++;
        fixture.Model.Open(); fixture.Model.Poll(); fixture.Model.Poll();
        Assert.False(fixture.Model.Stale);
        Assert.True(fixture.Model.CanPlay);
        Assert.Equal(6, fixture.Model.Scene!.Revision);
        Assert.Empty(fixture.Server.Posts);
    }

    [Fact]
    public void AudioPreparation_RevisionConflictPreservesSnapshotAndDisablesEditingUntilReload()
    {
        var media = new FakePreviewMedia { AutoPrepare = false };
        using var fixture = new InteractionFixture(media);
        media.Preparations[0].Completion.SetException(new BackendCommandException("revision_conflict"));

        fixture.Model.Poll();

        Assert.True(fixture.Model.Loaded);
        Assert.True(fixture.Model.Stale);
        Assert.False(fixture.Model.CanPlay);
        Assert.False(fixture.Model.CanBeginMove);
        Assert.Equal(5, fixture.Model.Scene!.Revision);
        Assert.Null(fixture.Model.AudioPeaks);
        Assert.Empty(fixture.Server.Posts);
    }
}
