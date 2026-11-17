using TrueMoon.Tales.Studio.Backend;
using TrueMoon.Tales.Studio.Editing;
using TrueMoon.Tales.Studio.Infrastructure;
using TrueMoon.Tales.Studio.Models;
using TrueMoon.Tales.Studio.Media;
using TrueMoon.Tales.Studio.Presentation.Scene;
using TrueMoon.Tales.Studio.Tests.Support;
using Xunit;

namespace TrueMoon.Tales.Studio.Tests.Presentation.Scene;

public sealed class BackendTrimModelTests
{
    [Fact]
    public void InvalidProjectedTrim_ShowsErrorWithoutCommit_AndCanBeCancelled()
    {
        var api = new Service(); using var model = Ready(api);
        Assert.True(model.BeginTrim(TrimEdge.End)); model.MoveTrim(60);
        Assert.True(model.HasError); Assert.True(model.Modified);
        model.CompleteTrim(); Assert.False(model.Trimming); Assert.False(model.CanSave);
        Assert.Empty(api.Calls); Assert.Equal(12, model.Scene!.Revision);
        model.Poll(); Assert.True(model.Idle);
        model.Cancel(); Assert.False(model.Modified); Assert.True(model.CanReload);
        Assert.Equal(8, model.TimelineDuration); Assert.Equal(13, model.PreviewClips[1].StartFrame);
    }

    [Fact]
    public void BackendTrim_UsesFractionalSourceCapacity_WithoutDroppingFinalMaterial()
    {
        var api = new Service { Video = new(30, 11 / 30d, 11) }; using var model = Ready(api);
        Assert.True(model.BeginTrim(TrimEdge.End)); model.MoveTrim(1);
        Assert.Equal(8.8, model.End, 8); Assert.Equal(9, model.TimelineDuration);
        model.CompleteTrim(); model.Poll(); Assert.False(model.HasError, model.Status);
        Assert.Equal(8.8, Assert.Single(api.Calls).End, 8); Assert.Equal(9, model.Scene!.Clip.DurationFrames);
    }

    [Fact]
    public void SuccessfulTrim_RetiresOldBaselineSeek_AndIgnoresItsLateConflict()
    {
        var api = new Service(); var frames = new DelayedFrames(); using var model = new SceneViewModel(api, frames);
        model.Open(); model.Poll(); Assert.True(model.Loaded);
        model.BeginTrim(TrimEdge.End); model.MoveTrim(-2); Assert.NotNull(frames.Pending);
        model.CompleteTrim(); model.Poll(); Assert.Equal(13, model.Scene!.Revision);
        Assert.True(frames.Token.IsCancellationRequested);
        frames.Pending!.SetException(new BackendCommandException("revision_conflict")); model.Poll();
        Assert.False(model.Stale); Assert.False(model.HasError); Assert.False(model.Modified);
        Assert.Equal(13, model.Scene.Revision); Assert.Single(api.Calls);
    }

    [Fact]
    public void TrimDraft_ProjectsFollowingActualClips_AndCommitsOnlyOnRelease()
    {
        var api = new Service(); using var model = Ready(api);
        Assert.True(model.BeginTrim(TrimEdge.End));
        model.MoveTrim(-2);
        Assert.Equal(new[] { 2, 11, 30 }, model.PreviewClips.Select(row => row.StartFrame));
        Assert.Equal(6, model.TimelineDuration); Assert.Empty(api.Calls);
        Assert.Equal(8, model.Scene!.Clip.DurationFrames);
        model.CompleteTrim(); model.Poll();
        Assert.Equal((0d, 6d, (int?)null), Assert.Single(api.Calls));
        Assert.False(model.Modified); Assert.False(model.Stale); Assert.Equal(13, model.Scene!.Revision);
        Assert.Equal(11, model.Scene.SegmentClips![1].StartFrame);
    }

    [Theory]
    [InlineData("cancel")]
    [InlineData("noop")]
    public void TrimDraft_CancellationAndNoopRestoreLayoutWithoutWriting(string operation)
    {
        var api = new Service(); using var model = Ready(api);
        Assert.True(model.BeginTrim(TrimEdge.End)); model.MoveTrim(-2);
        if (operation == "cancel") model.CancelTrim();
        else { model.MoveTrim(0); model.CompleteTrim(); }
        Assert.Equal(new[] { 2, 13, 30 }, model.PreviewClips.Select(row => row.StartFrame));
        Assert.False(model.Modified); Assert.Empty(api.Calls); Assert.Equal(12, model.Scene!.Revision);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void TrimDraft_ConflictOrWrongEchoRetainsDraftUntilCancelAndExplicitReload(bool wrongEcho)
    {
        var api = new Service { Reject = !wrongEcho, WrongEcho = wrongEcho }; using var model = Ready(api);
        model.BeginTrim(TrimEdge.End); model.MoveTrim(-2); model.CompleteTrim(); model.Poll();
        Assert.True(model.Stale); Assert.True(model.Modified); Assert.True(model.HasError);
        Assert.Equal(6, model.TimelineDuration); Assert.Equal(11, model.PreviewClips[1].StartFrame);
        Assert.False(model.CanSave); Assert.False(model.CanReload); Assert.Equal(12, model.Scene!.Revision);
        model.Save(); Assert.Single(api.Calls);
        model.Cancel(); Assert.False(model.Modified); Assert.True(model.CanReload);
        model.Open(); model.Poll(); Assert.False(model.Stale); Assert.Equal(8, model.TimelineDuration);
    }

    private static SceneViewModel Ready(Service api)
    {
        var model = new SceneViewModel(api, new FakeFrames()); model.Open(); model.Poll();
        Assert.True(model.Loaded, model.Status); return model;
    }

    private sealed class Service : ISceneService
    {
        public bool UsesTrimLayout => true;
        public bool SupportsTrim => true;
        public bool SupportsPreview => true;
        public bool PreviewOptional => false;
        public bool Reject, WrongEcho;
        public MediaInfo Video = new(24, 5, 120);
        public List<(double Start, double End, int? TimelineStart)> Calls { get; } = [];
        private SceneSnapshot _scene = Snapshot();
        private static SceneSnapshot Snapshot()
        {
            Clip[] clips = [new("a", "window", "source", "take", "A", 2, 8, 0, 8, 1),
                new("b", "window", "source", "take", "B", 13, 5, 0, 5, 1),
                new("c", "window", "source", "take", "C", 30, 5, 0, 5, 1, AnchorFrame: 30)];
            ScenarioPlacement[] items = [new("a", "source", "take", 8, 2, 0, 8, 1),
                new("b", "source", "take", 5, 3, 0, 5, 1), new("c", "source", "take", 5, 2, 0, 5, 1, AnchorFrame: 30)];
            return new("Trim", 12, 24, new("window", "Window", 0, 60), clips[0], "A",
                [new("take", "video", new(24, 5, 120))], [], clips, [], items, true, true, new('a', 64));
        }
        public Task<SceneSnapshot> OpenAsync(string? clipId, CancellationToken token) => Task.FromResult(_scene);
        public Task<MediaInfo> MediaInfoAsync(Take take, CancellationToken token) => Task.FromResult(Video);
        public Uri Media(string assetId) => new("http://127.0.0.1/media");
        public Task<bool> HasAudioAsync(string assetId, int stream, CancellationToken token) => Task.FromResult(false);
        public Task<SceneSnapshot> MoveAsync(SceneSnapshot scene, int requestedStart, CancellationToken token) => throw new NotSupportedException();
        public Task<SceneSnapshot> TrimAsync(SceneSnapshot scene, double start, double end, CancellationToken token, int? timelineStart = null)
        {
            Calls.Add((start, end, timelineStart));
            if (Reject) return Task.FromException<SceneSnapshot>(new BackendCommandException("revision_conflict"));
            var projected = TimelineTrimLayout.Project(scene, 120, start, end, timelineStart ?? scene.Clip.StartFrame);
            var saved = scene with { Revision = scene.Revision + 1, Clip = projected.Clips[0], SegmentClips = projected.Clips, ScenarioItems = projected.ScenarioItems };
            if (WrongEcho) saved = saved with { SegmentClips = scene.SegmentClips };
            else _scene = saved;
            return Task.FromResult(saved);
        }
    }

    private sealed class DelayedFrames : IFrameDecoder
    {
        private int _calls;
        internal TaskCompletionSource<byte[]>? Pending;
        internal CancellationToken Token;
        public Task<byte[]> ReadAsync(Uri media, int frame, double fps, CancellationToken token)
        {
            if (_calls++ == 0) return Task.FromResult(FakeFrames.Pixels(frame));
            Token = token; Pending = new(TaskCreationOptions.RunContinuationsAsynchronously); return Pending.Task;
        }
    }
}
