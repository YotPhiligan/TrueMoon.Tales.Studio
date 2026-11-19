using TrueMoon.Tales.Studio.Backend;

namespace TrueMoon.Tales.Studio.Presentation.Scene;

internal sealed partial class SceneViewModel
{
    public bool SupportsMontageEdits => api.SupportsMontageEdits;
    public bool CanEditMontage => SupportsMontageEdits && ProjectReady && Idle && CanReload && !_stale && StoryboardPlan is null;
    public bool CanUndoMontage => CanEditMontage && (Scene?.CanUndoMontage ?? EmptyProject?.CanUndoMontage) == true;
    public bool CanRedoMontage => CanEditMontage && (Scene?.CanRedoMontage ?? EmptyProject?.CanRedoMontage) == true;
    public bool CanSplitMontage => CanEditMontage && Scene is not null && SplitFrame > TimelineStart &&
        (long)SplitFrame < (long)TimelineStart + TimelineDuration;
    public bool CanRemoveMontage => CanEditMontage && Scene is { Clip: { AnchorFrame: null, GapProtected: false } };
    public int SplitFrame => (int)Math.Clamp(Math.Floor(TimelineCursor), int.MinValue, int.MaxValue);

    public void UndoMontageEdit() { if (CanUndoMontage) EditMontage(new UndoMontage()); }
    public void RedoMontageEdit() { if (CanRedoMontage) EditMontage(new RedoMontage()); }
    public void SplitMontageAtCursor() { if (CanSplitMontage) EditMontage(new SplitMontageClip(Scene!.Clip.Id, SplitFrame)); }
    public void RemoveMontageSelection() { if (CanRemoveMontage) EditMontage(new RemoveMontageClip(Scene!.Clip.Id)); }
    public void SetBlackJoin(int fadeOut, int pause, int fadeIn)
    {
        if (CanEditMontage && Scene is { NextJoin: { } next } scene)
            EditMontage(new SetBlackTransition(scene.Clip.Id, next.RightId, fadeOut, pause, fadeIn));
    }

    public void SetMontageStart(int start)
    {
        if (!CanEditMontage || !BeginMove()) return;
        MoveClip((int)Math.Clamp((long)start - TimelineStart, int.MinValue, int.MaxValue));
        CompleteMove();
    }

    private void EditMontage(ScenarioCommand command)
    {
        var selected = Scene?.Clip.Id;
        ClearPreparedPlayback();
        _stale = true;
        Saving = true;
        Begin("Сохраняю монтаж…", async token =>
            await PrepareOpeningAsync(await api.EditMontageAsync(command, selected, token), token, "Монтаж сохранён"));
    }

    internal SceneViewModel ReviewPlayer(BackendRenderedMontage review, BackendConnection backend)
    {
        ClearPreparedPlayback();
        return new(new RenderedReviewSource(review), new BackendFrameDecoder(backend), _playback);
    }
}
