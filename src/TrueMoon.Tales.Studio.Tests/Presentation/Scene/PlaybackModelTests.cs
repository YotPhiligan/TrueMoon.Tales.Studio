using TrueMoon.Alloy.Runtime;
using TrueMoon.Argentis;
using TrueMoon.Tales.Studio.Editing;
using TrueMoon.Tales.Studio.Media;
using TrueMoon.Tales.Studio.Models;
using TrueMoon.Tales.Studio.Presentation.Scene;
using TrueMoon.Tales.Studio.Presentation.Shell;
using TrueMoon.Tales.Studio.Tests.Support;
using Xunit;

namespace TrueMoon.Tales.Studio.Tests.Presentation.Scene;

public sealed class PlaybackModelTests
{
    private static InteractionFixture Ready(FakePreviewMedia media)
    {
        var fixture = new InteractionFixture(media);
        fixture.Model.Poll();
        Assert.True(fixture.Model.CanPlay);
        return fixture;
    }

    private static void Start(InteractionFixture fixture, FakePreviewMedia media)
    {
        fixture.Model.TogglePlayback();
        media.Videos[^1].Enqueue(0);
        fixture.Model.Poll();
        Assert.True(fixture.Model.Playing);
        Assert.Equal(1, media.Sounds[^1].PlayCalls);
    }

    [Fact]
    public void Playback_WaitsForFirstVideoFrameBeforeStartingAudioClock()
    {
        var media = new FakePreviewMedia();
        using var fixture = Ready(media);

        fixture.Model.TogglePlayback();
        fixture.Model.Poll();

        Assert.True(fixture.Model.Playing);
        Assert.True(fixture.Model.NeedsFrame);
        Assert.Equal(0, media.Sounds[0].PlayCalls);
        Assert.Equal(0, fixture.Model.FrameIndex);
        media.Videos[0].Enqueue(0);
        fixture.Model.Poll();
        Assert.Equal(1, media.Sounds[0].PlayCalls);
        Assert.Equal(0, fixture.Model.TimelineCursor);
        Assert.Empty(fixture.Server.Posts);
    }

    [Fact]
    public void Playback_SampleClockSelectsDueFrameAndRetainsFutureFrame()
    {
        var media = new FakePreviewMedia();
        using var fixture = Ready(media);
        Start(fixture, media);
        media.Videos[0].Enqueue(1);
        media.Videos[0].Enqueue(2);
        media.Videos[0].Enqueue(3);

        media.Sounds[0].PlayedSamples = 3999; // Just before output frame 2 at 24fps.
        fixture.Model.Poll();
        Assert.Equal(1, fixture.Model.FrameIndex);
        Assert.Equal(1, fixture.Model.TimelineCursor);
        media.Sounds[0].PlayedSamples = 4000;
        fixture.Model.Poll();
        Assert.Equal(2, fixture.Model.FrameIndex);
        Assert.Equal(2, fixture.Model.Pixels![0]);
        Assert.Equal(2, fixture.Model.TimelineCursor);
        fixture.Model.Poll();
        Assert.Equal(2, fixture.Model.FrameIndex); // Future frame 3 must not flash early.
        media.Sounds[0].PlayedSamples = 6000;
        fixture.Model.Poll();
        Assert.Equal(3, fixture.Model.FrameIndex);
        Assert.Equal(1, media.Sounds[0].PlayCalls);
        Assert.Equal(5, fixture.Model.Scene!.Revision);
        Assert.Empty(fixture.Server.Posts);
    }

    [Fact]
    public void Playback_ProjectCursorRemainsSeparateFromNativeFrameNumber()
    {
        var media = new FakePreviewMedia();
        using var fixture = Ready(media);
        fixture.Server.Current = fixture.Server.Current with { SourceInFrame = 2, SourceOutFrame = 12, Speed = 2, DurationFrames = 5 };
        fixture.Model.Open(); fixture.Model.Poll(); fixture.Model.Poll();
        fixture.Model.TogglePlayback();
        media.Videos[0].Enqueue(0, native: 2);
        fixture.Model.Poll();
        media.Videos[0].Enqueue(2, native: 6);
        media.Sounds[0].PlayedSamples = 4000;
        fixture.Model.Poll();

        Assert.Equal(6, fixture.Model.FrameIndex);
        Assert.Equal(2, fixture.Model.TimelineCursor);
        Assert.Equal(6, fixture.Model.Pixels![0]);
        Assert.Empty(fixture.Server.Posts);
    }

    [Fact]
    public void Playback_PauseDisposesMediaAndResumeStartsAtPausedFrame()
    {
        var media = new FakePreviewMedia();
        using var fixture = Ready(media);
        Start(fixture, media);
        media.Videos[0].Enqueue(2);
        media.Sounds[0].PlayedSamples = 4000;
        fixture.Model.Poll();

        fixture.Model.TogglePlayback();
        Assert.False(fixture.Model.Playing);
        Assert.True(media.Sounds[0].Disposed);
        Assert.True(media.Videos[0].Disposed);
        Assert.Equal(2, fixture.Model.TimelineCursor);
        media.Videos[0].Enqueue(7);
        fixture.Model.Poll();
        Assert.Equal(2, fixture.Model.FrameIndex);
        fixture.Model.TogglePlayback();
        Assert.Equal(new[] { 0, 2 }, media.VideoStarts);
        Assert.Equal(new[] { 0, 2 }, media.SoundStarts);
        Assert.Equal(0, media.Sounds[1].PlayCalls);
        media.Videos[1].Enqueue(2);
        fixture.Model.Poll();
        Assert.Equal(1, media.Sounds[1].PlayCalls);
        Assert.Empty(fixture.Server.Posts);
    }

    [Fact]
    public void Playback_EndRetainsLastFrameAndReplayRestartsAtBeginning()
    {
        var media = new FakePreviewMedia();
        using var fixture = Ready(media);
        Start(fixture, media);
        for (var frame = 1; frame < 8; frame++) media.Videos[0].Enqueue(frame);
        media.Videos[0].Finished = true;
        media.Sounds[0].PlayedSamples = 16000;
        fixture.Model.Poll();

        Assert.False(fixture.Model.Playing);
        Assert.Equal(7, fixture.Model.FrameIndex);
        Assert.Equal(7, fixture.Model.TimelineCursor);
        Assert.Equal(7, fixture.Model.Pixels![0]);
        Assert.True(media.Sounds[0].Disposed);
        Assert.True(media.Videos[0].Disposed);
        Assert.Contains("Конец", fixture.Model.MediaStatus);
        Assert.Equal(fixture.Model.MediaStatus, fixture.Model.Status);
        fixture.Model.TogglePlayback();
        Assert.Equal(new[] { 0, 0 }, media.VideoStarts);
        Assert.Equal(new[] { 0, 0 }, media.SoundStarts);
        Assert.Empty(fixture.Server.Posts);
    }

    [Fact]
    public void Playback_SeekStopsMediaAndShowsRequestedFrameWithoutWriting()
    {
        var media = new FakePreviewMedia();
        using var fixture = Ready(media);
        Start(fixture, media);

        fixture.Model.SeekTimeline(4);
        fixture.Model.Poll();

        Assert.False(fixture.Model.Playing);
        Assert.True(media.Sounds[0].Disposed);
        Assert.True(media.Videos[0].Disposed);
        Assert.Equal(4, fixture.Model.FrameIndex);
        Assert.Equal(4, fixture.Model.TimelineCursor);
        Assert.Empty(fixture.Server.Posts);
    }

    [Theory]
    [InlineData("step")]
    [InlineData("take")]
    [InlineData("start")]
    [InlineData("end")]
    public void Playback_ManualNavigationStopsExistingStream(string operation)
    {
        var media = new FakePreviewMedia();
        using var fixture = Ready(media);
        Start(fixture, media);

        switch (operation)
        {
            case "step": fixture.Model.Step(1); break;
            case "take": fixture.Model.SelectTake(1); break;
            case "start": fixture.Model.ReturnToClip(); break;
            case "end": fixture.Model.GoToEnd(); break;
        }
        fixture.Model.Poll();

        Assert.False(fixture.Model.Playing);
        Assert.True(media.Sounds[0].Disposed);
        Assert.True(media.Videos[0].Disposed);
        Assert.Equal(operation == "end" ? 7 : operation == "step" ? 1 : 0, fixture.Model.FrameIndex);
        Assert.Equal(operation == "take" ? 1 : 0, fixture.Model.TakeIndex);
        Assert.Empty(fixture.Server.Posts);
    }

    [Fact]
    public void Playback_TrimStopsMediaAndCancelRetainsPreparedWaveform()
    {
        var media = new FakePreviewMedia();
        using var fixture = Ready(media);
        var peaks = fixture.Model.AudioPeaks;
        Start(fixture, media);

        Assert.True(fixture.Model.BeginTrim(TrimEdge.End));
        fixture.Model.MoveTrim(-2);
        Assert.False(fixture.Model.Playing);
        Assert.True(media.Sounds[0].Disposed);
        Assert.True(media.Videos[0].Disposed);
        Assert.Null(fixture.Model.AudioPeaks);
        Assert.False(fixture.Model.CanPlay);
        fixture.Model.CancelTrim();
        fixture.Model.Poll();
        Assert.Same(peaks, fixture.Model.AudioPeaks);
        Assert.Equal(8, fixture.Model.End);
        Assert.Empty(fixture.Server.Posts);
    }

    [Fact]
    public void Playback_ClipSwitchStopsMediaAndPreparesNewSnapshot()
    {
        var media = new FakePreviewMedia();
        using var fixture = Ready(media);
        Start(fixture, media);
        fixture.Server.Current = fixture.Server.Current with { Id = "clip-B", SourceInFrame = 2, DurationFrames = 6 };

        fixture.Model.SelectClip("clip-B"); fixture.Model.Poll(); fixture.Model.Poll();

        Assert.False(fixture.Model.Playing);
        Assert.True(media.Sounds[0].Disposed);
        Assert.True(media.Videos[0].Disposed);
        Assert.Equal("clip-B", fixture.Model.Scene!.Clip.Id);
        Assert.Equal(2, fixture.Model.FrameIndex);
        Assert.Equal(2, media.Preparations.Count);
        Assert.True(media.Preparations[0].Token.IsCancellationRequested);
        Assert.Equal("clip-B", media.Preparations[1].Scene.Clip.Id);
        Assert.Empty(fixture.Server.Posts);
    }

    [Fact]
    public void Playback_FromAlternativeTakeUsesAssignedClipAndStartsAtItsBeginning()
    {
        var media = new FakePreviewMedia();
        using var fixture = Ready(media);
        fixture.Model.SelectTake(1); fixture.Model.Poll();
        Assert.False(fixture.Model.Assigned);

        fixture.Model.TogglePlayback();
        Assert.Equal(0, media.VideoStarts[0]);
        Assert.Equal("take-A", media.VideoScenes[0].Clip.TakeId);
        media.Videos[0].Enqueue(0);
        fixture.Model.Poll();
        Assert.True(fixture.Model.Assigned);
        Assert.Equal(0, fixture.Model.TakeIndex);
        Assert.Empty(fixture.Server.Posts);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void Playback_DecoderOrOutputErrorStopsBothResourcesWithoutWriting(bool soundError)
    {
        var media = new FakePreviewMedia();
        using var fixture = Ready(media);
        Start(fixture, media);
        if (soundError) media.Sounds[0].Error = new IOException("output failed");
        else media.Videos[0].Error = new IOException("decoder failed");

        fixture.Model.Poll();

        Assert.False(fixture.Model.Playing);
        Assert.True(fixture.Model.HasError);
        Assert.True(media.Sounds[0].Disposed);
        Assert.True(media.Videos[0].Disposed);
        Assert.Contains(soundError ? "output failed" : "decoder failed", fixture.Model.Status);
        Assert.Equal(5, fixture.Model.Scene!.Revision);
        Assert.False(fixture.Model.Modified);
        Assert.Empty(fixture.Server.Posts);
    }

    [Fact]
    public void Playback_EmptyCompletedVideoReportsFailureBeforeStartingAudio()
    {
        var media = new FakePreviewMedia();
        using var fixture = Ready(media);
        fixture.Model.TogglePlayback();
        media.Videos[0].Finished = true;

        fixture.Model.Poll();

        Assert.True(fixture.Model.HasError);
        Assert.False(fixture.Model.Playing);
        Assert.Equal(0, media.Sounds[0].PlayCalls);
        Assert.True(media.Sounds[0].Disposed);
        Assert.True(media.Videos[0].Disposed);
        Assert.Empty(fixture.Server.Posts);
    }

    [Fact]
    public void Playback_UnavailableDeviceWarningRemainsVisibleDuringVideoPlayback()
    {
        var media = new FakePreviewMedia { SoundWarning = "Device unavailable; silent clock" };
        using var fixture = Ready(media);
        Start(fixture, media);

        Assert.Equal("Device unavailable; silent clock", fixture.Model.MediaStatus);
        Assert.True(fixture.Model.Playing);
        Assert.False(fixture.Model.HasError);
        Assert.Empty(fixture.Server.Posts);
    }

    [Fact]
    public void Playback_SuccessfulRetryClearsPreviousPlaybackError()
    {
        var media = new FakePreviewMedia();
        using var fixture = Ready(media);
        Start(fixture, media);
        media.Videos[0].Error = new IOException("transient decoder failure");
        fixture.Model.Poll();
        Assert.True(fixture.Model.HasError);

        fixture.Model.TogglePlayback();
        media.Videos[1].Enqueue(0);
        fixture.Model.Poll();

        Assert.True(fixture.Model.Playing);
        Assert.False(fixture.Model.HasError);
        Assert.Equal(1, media.Sounds[1].PlayCalls);
        Assert.Empty(fixture.Server.Posts);
    }

    [Fact]
    public void Preparation_ReplacedSnapshotCancelsOldWorkAndIgnoresItsLateResult()
    {
        var media = new FakePreviewMedia { AutoPrepare = false };
        using var fixture = new InteractionFixture(media);
        Assert.False(fixture.Model.CanPlay);
        var first = media.Preparations[0];
        fixture.Server.Revision = 6;
        fixture.Model.Open(); fixture.Model.Poll();
        var second = media.Preparations[1];

        Assert.True(first.Token.IsCancellationRequested);
        first.Completion.SetResult(FakePreviewMedia.Audio(first.Scene, .9f));
        fixture.Model.Poll();
        Assert.Null(fixture.Model.AudioPeaks);
        Assert.False(fixture.Model.CanPlay);
        second.Completion.SetResult(FakePreviewMedia.Audio(second.Scene, .2f));
        fixture.Model.Poll();
        Assert.Equal(new[] { .2f }, fixture.Model.AudioPeaks);
        Assert.True(fixture.Model.CanPlay);
        Assert.Equal(6, fixture.Model.Scene!.Revision);
        Assert.Empty(fixture.Server.Posts);
    }

    [Theory]
    [InlineData("io")]
    [InlineData("cancelled")]
    [InlineData("invalid-data")]
    public void Preparation_FailureOrCancellationKeepsProjectLoadedAndCanBeReloaded(string reason)
    {
        var media = new FakePreviewMedia { AutoPrepare = false };
        using var fixture = new InteractionFixture(media);
        var first = media.Preparations[0];
        if (reason == "cancelled") first.Completion.SetCanceled();
        else first.Completion.SetException(reason == "invalid-data"
            ? new InvalidDataException("invalid audio") : new IOException("audio preparation failed"));
        fixture.Model.Poll();

        Assert.True(fixture.Model.Loaded);
        Assert.False(fixture.Model.CanPlay);
        Assert.False(fixture.Model.HasError);
        Assert.Null(fixture.Model.AudioPeaks);
        Assert.Contains("Звук не подготовлен", fixture.Model.MediaStatus);
        fixture.Model.Open(); fixture.Model.Poll();
        var retry = media.Preparations[1];
        retry.Completion.SetResult(FakePreviewMedia.Audio(retry.Scene));
        fixture.Model.Poll();
        Assert.True(fixture.Model.CanPlay);
        Assert.Equal(5, fixture.Model.Scene!.Revision);
        Assert.Empty(fixture.Server.Posts);
    }

    [Fact]
    public void Dispose_CancelsPreparationAndStopsLiveMediaWithoutApplyingLateResults()
    {
        var media = new FakePreviewMedia();
        var fixture = Ready(media);
        var disposed = false;
        try
        {
            Start(fixture, media);
            var scope = media.Preparations[0].Token;
            fixture.Dispose(); disposed = true;
            Assert.True(scope.IsCancellationRequested);
            Assert.True(media.Sounds[0].Disposed);
            Assert.True(media.Videos[0].Disposed);
            Assert.False(fixture.Model.Playing);
            Assert.False(fixture.Model.NeedsFrame);
            Assert.Null(fixture.Model.Pixels);
            Assert.Empty(fixture.Server.Posts);
        }
        finally { if (!disposed) fixture.Dispose(); }

        var pendingMedia = new FakePreviewMedia { AutoPrepare = false };
        var pendingFixture = new InteractionFixture(pendingMedia);
        var pending = pendingMedia.Preparations[0];
        pendingFixture.Dispose();
        Assert.True(pending.Token.IsCancellationRequested);
        pending.Completion.SetResult(FakePreviewMedia.Audio(pending.Scene, .8f));
        Assert.Null(pendingFixture.Model.AudioPeaks);
        Assert.Null(pendingFixture.Model.Pixels);
    }

    [Fact]
    public void Playback_SpaceShortcutStartsAndFocusLossStopsMedia()
    {
        var media = new FakePreviewMedia();
        using var fixture = Ready(media);
        var view = new SceneView(fixture.Model, new SceneViewStyle(), new StudioTitleBarFactory(new StudioTitleBarStyle()));
        using var session = new UiSession(view, new HeadlessSurface(), new(1280, 820));
        session.Update(); session.Update();
        var play = Assert.Single(Walk(view.Preview).OfType<Button>(), button => button.AccessibleName?.Contains("Воспроизвести монтажный клип") == true);
        session.Focus(play);

        Assert.True(session.HandleInput(new(InputKind.KeyDown, Key: UiKey.Space)).Handled);
        Assert.True(fixture.Model.Playing);
        session.HandleInput(new(InputKind.FocusLost));
        Assert.False(fixture.Model.Playing);
        Assert.Equal("Пауза", fixture.Model.MediaStatus);
        Assert.Equal("Пауза", fixture.Model.Status);
        Assert.True(media.Sounds[^1].Disposed);
        Assert.True(media.Videos[^1].Disposed);
        Assert.Empty(fixture.Server.Posts);
    }

    [Fact]
    public void Playback_StalledAudioClockStopsAfterBoundedGracePeriod()
    {
        var media = new FakePreviewMedia();
        using var fixture = Ready(media);
        Start(fixture, media);
        fixture.Model.Poll(TimeSpan.FromSeconds(3));
        Assert.True(fixture.Model.Playing);
        fixture.Model.Poll(TimeSpan.FromTicks(1));
        Assert.False(fixture.Model.Playing);
        Assert.True(fixture.Model.HasError);
        Assert.Contains("не продвигаются", fixture.Model.Status);
        Assert.True(media.Videos[0].Disposed);
        Assert.True(media.Sounds[0].Disposed);
        Assert.Empty(fixture.Server.Posts);
    }

    [Fact]
    public void Playback_AudioClockProgressResetsStallBudget()
    {
        var media = new FakePreviewMedia();
        using var fixture = Ready(media);
        Start(fixture, media);
        fixture.Model.Poll(TimeSpan.FromSeconds(2));
        media.Sounds[0].PlayedSamples = 1000;
        fixture.Model.Poll(TimeSpan.FromSeconds(2));
        fixture.Model.Poll(TimeSpan.FromSeconds(2));
        Assert.True(fixture.Model.Playing);
        Assert.False(fixture.Model.HasError);
        Assert.False(media.Videos[0].Disposed);
        Assert.Empty(fixture.Server.Posts);
    }

    private static IEnumerable<Element> Walk(Element element) => new[] { element }.Concat(element.Children.SelectMany(Walk));
}

internal sealed class FakePreviewMedia : IPreviewMedia
{
    internal sealed record Preparation(SceneSnapshot Scene, CancellationToken Token, TaskCompletionSource<PreviewAudio> Completion);
    public bool AutoPrepare = true;
    public string? SoundWarning;
    public readonly List<Preparation> Preparations = [];
    public readonly List<FakePreviewVideo> Videos = [];
    public readonly List<FakePreviewSound> Sounds = [];
    public readonly List<int> VideoStarts = [], SoundStarts = [];
    public readonly List<SceneSnapshot> VideoScenes = [];

    public static PreviewAudio Audio(SceneSnapshot scene, float peak = .25f)
    {
        var count = checked((int)(AudioTimeline.Sample(scene.Clip.StartFrame + scene.Clip.DurationFrames, scene.Fps) - AudioTimeline.Sample(scene.Clip.StartFrame, scene.Fps)));
        return new(new float[count * 2], [peak], scene.Fps, scene.Clip.StartFrame, scene.Clip.DurationFrames);
    }
    public Task<PreviewAudio> PrepareAsync(SceneSnapshot scene, CancellationToken token)
    {
        var completion = new TaskCompletionSource<PreviewAudio>(TaskCreationOptions.RunContinuationsAsynchronously);
        Preparations.Add(new(scene, token, completion));
        if (AutoPrepare) completion.SetResult(Audio(scene));
        return completion.Task;
    }
    public IPreviewVideo Video(SceneSnapshot scene, MediaInfo media, int localFrame, CancellationToken token)
    {
        var video = new FakePreviewVideo();
        Videos.Add(video); VideoStarts.Add(localFrame); VideoScenes.Add(scene);
        return video;
    }
    public IPreviewSound Sound(PreviewAudio audio, int localFrame)
    {
        var sound = new FakePreviewSound { Warning = SoundWarning };
        Sounds.Add(sound); SoundStarts.Add(localFrame);
        return sound;
    }
}

internal sealed class FakePreviewVideo : IPreviewVideo
{
    private readonly Queue<PlaybackFrame> _frames = [];
    public bool Finished, Disposed;
    public bool Complete => Finished && _frames.Count == 0;
    public Task Completion => Task.CompletedTask;
    public Exception? Error { get; set; }
    public void Enqueue(int frame, int? native = null) => _frames.Enqueue(new(frame, native ?? frame, FakeFrames.Pixels(native ?? frame)));
    public bool TryRead(out PlaybackFrame? frame) => _frames.TryDequeue(out frame);
    public void Dispose() => Disposed = true;
}

internal sealed class FakePreviewSound : IPreviewSound
{
    public long PlayedSamples { get; set; }
    public string? Warning { get; set; }
    public Exception? Error { get; set; }
    public int PlayCalls;
    public bool Disposed;
    public void Play() => PlayCalls++;
    public void Dispose() => Disposed = true;
}
