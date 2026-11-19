using TrueMoon.Argentis;
using TrueMoon.Tales.Studio.Editing;
using TrueMoon.Tales.Studio.Models;

namespace TrueMoon.Tales.Studio.Presentation.Scene;

internal sealed class SceneStrip(SceneViewModel model, Action updated, Action<TrackSpan?>? openTrack = null) : Element
{
    private IInputContext? _context;
    private Rect _gestureBounds;
    private float _originX;
    private bool _trimming;
    private bool _body, _moving, _pendingMove;
    private bool _leftGrip;
    private double _clickFrame;
    private string? _window;
    private double _offset, _zoom = 1;
    public bool IsDragging => _context is not null;
    public override bool Focusable => true;

    private double Span => Math.Max(1, model.Scene!.Segment.DurationFrames / _zoom);
    private float Scale => (float)(Bounds.Width / Span);
    private double Origin => model.Scene!.Segment.StartFrame + _offset;
    private float FrameX(double frame) => Bounds.X + (float)((frame - Origin) * Scale);
    private double FrameAt(float x) => Origin + (x - Bounds.X) / Math.Max(float.Epsilon, Scale);
    internal Rect ClipBounds => model.Scene is { } scene
        ? new(FrameX(model.TimelineStart), Bounds.Y + 24,
            model.TimelineDuration * Scale, Math.Max(0, Math.Min(38, Bounds.Height - 24))) : default;

    public override bool HandleInput(UiInput input, IInputContext context)
    {
        if (model.Scene is not null && !IsDragging && input.Kind == InputKind.Wheel && Bounds.Contains(input.X, input.Y) &&
            float.IsFinite(input.WheelDelta) && input.WheelDelta != 0 && Bounds.Width > 0)
        {
            SyncWindow();
            var before = FrameAt(input.X);
            if (input.Control)
            {
                _zoom = Math.Clamp(_zoom * (input.WheelDelta > 0 ? 2 : .5), 1, 16);
                _offset = before - model.Scene.Segment.StartFrame - (input.X - Bounds.X) / Scale;
            }
            else if (_zoom > 1) _offset -= Math.Sign(input.WheelDelta) * Span * .2;
            else return false;
            _offset = Math.Clamp(_offset, 0, Math.Max(0, model.Scene.Segment.DurationFrames - Span));
            Invalidate();
            return true;
        }
        if (IsDragging)
        {
            if (input.Kind == InputKind.FocusLost || input.Kind == InputKind.KeyDown && input.Key == UiKey.Escape)
            { CancelGesture(); return true; }
            if (Bounds != _gestureBounds || !IsVisible || !IsEffectivelyEnabled)
            { CancelGesture(); return true; }
            if (input.Kind == InputKind.PointerMove) { Move(input.X, input.Shift); return true; }
            if (input.Kind == InputKind.PointerUp && input.Button == 0)
            {
                if (!float.IsFinite(input.X)) { CancelGesture(); return true; }
                try
                {
                    Move(input.X, input.Shift);
                    var trim = _trimming;
                    var move = _moving;
                    var click = _pendingMove;
                    Release();
                    if (trim) model.CompleteTrim();
                    else if (move) model.CompleteMove();
                    else if (click) model.SeekTimeline(_clickFrame);
                }
                catch { model.CancelTrim(); model.CancelMove(); CancelGesture(); throw; }
                finally { updated(); }
                return true;
            }
            return input.Kind is InputKind.PointerDown or InputKind.KeyDown or InputKind.KeyUp or InputKind.Text;
        }
        if (input.Kind != InputKind.PointerDown || input.Button != 0 || (!model.CanSeek && !model.CanBeginMove) ||
            !float.IsFinite(input.X) || !float.IsFinite(input.Y) || Bounds.Width <= 0 || !Bounds.Contains(input.X, input.Y)) return false;
        SyncWindow();
        if (openTrack is not null && model.CanReload && model.Scene?.Tracks is { } tracks &&
            tracks.LastOrDefault(item => TrackBounds(item).Contains(input.X, input.Y)) is { } track)
        { openTrack(track); return true; }
        var bar = ClipBounds;
        var frame = FrameAt(input.X);
        var grip = Math.Min(10, bar.Width / 3);
        var left = bar.Contains(input.X, input.Y) && input.X <= bar.X + grip;
        var right = bar.Contains(input.X, input.Y) && input.X >= bar.X + bar.Width - grip;
        if (left || right)
        {
            if (!model.BeginTrim(left ? TrimEdge.Start : TrimEdge.End))
            { model.Explain("Для обрезки сохраните/отмените черновик и выберите монтажный дубль; закреплённое начало не перемещается"); updated(); return true; }
            _trimming = true;
            _leftGrip = left;
        }
        else if (frame < model.TimelineStart || frame >= model.TimelineStart + model.TimelineDuration)
        {
            if (input.Y < bar.Y || input.Y >= bar.Y + bar.Height || !model.CanReload) return false;
            var neighbor = model.Scene!.Choices.FirstOrDefault(choice => choice.Clip.SegmentId == model.Scene.Segment.Id &&
                frame >= choice.Clip.StartFrame && frame < (long)choice.Clip.StartFrame + choice.Clip.DurationFrames &&
                choice.Takes.Any(take => take.Id == choice.Clip.TakeId));
            if (neighbor is null) return false;
            model.SelectClip(neighbor.Clip.Id);
            updated();
            return true;
        }
        else if (bar.Contains(input.X, input.Y)) { _body = true; _pendingMove = true; _clickFrame = frame; }
        _originX = input.X; _gestureBounds = Bounds;
        try
        {
            context.Focus(this);
            _context = context;
            context.Capture(this);
            if (!_trimming && !_body) model.SeekTimeline(frame);
            updated();
        }
        catch { CancelGesture(); throw; }
        return true;
    }

    private void Move(float x, bool snap)
    {
        if (!float.IsFinite(x)) return;
        if (_pendingMove && Math.Abs(x - _originX) >= 4)
        {
            _pendingMove = false;
            _moving = model.BeginMove();
            if (!_moving) model.Explain("Для переноса сохраните/отмените черновик; закреплённый клип и конфликтный сценарий не перемещаются");
        }
        if (_trimming)
        {
            var delta = Math.Round(((double)x - _originX) / Scale, MidpointRounding.AwayFromZero);
            var origin = (long)model.Scene!.Clip.StartFrame + (_leftGrip ? 0 : model.Scene.Clip.DurationFrames);
            model.MoveTrim(Snap(delta, origin, snap));
        }
        else if (_moving)
        {
            var delta = Math.Round(((double)x - _originX) / Scale, MidpointRounding.AwayFromZero);
            var clip = model.Scene!.Clip;
            model.MoveClip(Snap(delta, clip.StartFrame, snap, clip.DurationFrames));
        }
        else if (!_body) model.SeekTimeline(FrameAt(x));
        updated();
    }

    private int Snap(double delta, long origin, bool enabled, int movingLength = 0)
    {
        var requested = origin + delta;
        if (enabled && model.Scene is { } scene)
        {
            var boundaries = scene.Choices.Where(choice => choice.Clip.Id != scene.Clip.Id && choice.Segment.Id == scene.Segment.Id)
                .SelectMany(choice => new long[] { choice.Clip.StartFrame, (long)choice.Clip.StartFrame + choice.Clip.DurationFrames });
            var candidates = boundaries.Concat((scene.MarkerFrames ?? []).Select(frame => (long)frame))
                .Concat([scene.Segment.StartFrame, (long)scene.Segment.StartFrame + scene.Segment.DurationFrames]);
            if (movingLength != 0) candidates = candidates.Concat(candidates.Select(frame => frame - movingLength));
            var nearest = candidates.MinBy(frame => Math.Abs(frame - requested));
            if (Math.Abs(nearest - requested) * Scale <= 8) delta = nearest - origin;
        }
        return (int)Math.Clamp(delta, int.MinValue + 1d, int.MaxValue);
    }

    private void SyncWindow()
    {
        var id = model.Scene?.Segment.Id;
        if (_window == id) return;
        _window = id;
        _offset = 0;
        _zoom = 1;
    }

    private Rect TrackBounds(TrackSpan item)
    {
        var rows = Math.Max(0, Bounds.Height - 64) / 4;
        var lane = Math.Min(3, item.LaneIndex);
        return item.LaneIndex == 4
            ? new(FrameX(item.StartFrame) - 3, Bounds.Y + 12, 7, 12)
            : new(FrameX(item.StartFrame), Bounds.Y + 64 + rows * lane, Math.Max(2, item.DurationFrames * Scale), Math.Max(0, rows - 2));
    }

    internal static bool HasVisibleTracks(SceneSnapshot? scene) => scene?.Tracks?.Any(item => item.LaneIndex < 4 &&
        item.StartFrame < (long)scene.Segment.StartFrame + scene.Segment.DurationFrames &&
        (long)item.StartFrame + item.DurationFrames > scene.Segment.StartFrame) == true;

    private void Release()
    {
        var context = _context;
        _context = null; _trimming = false; _body = false; _moving = false; _pendingMove = false;
        context?.ReleaseCapture(this);
    }

    public void CancelGesture()
    {
        if (!IsDragging && !_trimming) return;
        try { model.CancelTrim(); model.CancelMove(); }
        finally { try { Release(); } finally { updated(); } }
    }

    public override void OnInputCancelled() => CancelGesture();
    public override void Dispose()
    {
        try { CancelGesture(); }
        finally { base.Dispose(); }
    }

    protected override Size MeasureCore(Size available, ITextLayoutService text) => new(available.Width, 80);
    protected override void DrawCore(IDrawingContext context)
    {
        if (model.EmptyProject is not null)
        {
            context.Text("Монтажных клипов пока нет", Bounds.X + 8, Bounds.Y + 16, Theme.Disabled, FontSize, FontFamily);
            return;
        }
        if (model.Scene is not { } scene) return;
        SyncWindow();
        var area = Bounds;
        context.Save();
        context.Clip(area);
        for (var i = 0; i <= 6; i++)
        {
            var x = area.X + area.Width * i / 6;
            context.Fill(new(x, area.Y + 18, 1, Math.Max(0, area.Height - 18)), Theme.Border with { A = 55 });
            if (i < 6) context.Text($"{(int)Math.Floor(Origin + Span * i / 6)} к", x + 4, area.Y, Theme.Disabled, 10, Theme.FontFamily);
        }
        foreach (var marker in scene.MarkerFrames ?? [])
        {
            var x = FrameX(marker);
            if (x >= area.X && x < area.X + area.Width)
                context.Fill(new(x, area.Y + 13, 2, Math.Max(0, area.Height - 13)), Theme.Modified);
        }
        var bar = ClipBounds;
        foreach (var clip in model.PreviewClips.Where(clip => clip.Id != scene.Clip.Id))
        {
            var neighbor = new Rect(FrameX(clip.StartFrame), bar.Y,
                clip.DurationFrames * Scale, bar.Height);
            context.Fill(neighbor, Theme.Border with { A = 100 }, 4);
            context.Save(); context.Clip(neighbor);
            context.Text(clip.StoryTitle ?? "Клип", neighbor.X + 5, neighbor.Y + 10, Theme.Disabled, 11, Theme.FontFamily);
            context.Restore();
        }
        context.Fill(bar, Theme.Accent with { A = 65 }, 4);
        context.Stroke(bar, model.Modified ? Theme.Modified : Theme.Accent, model.Trimming || model.Moving ? 2 : 1, 4);
        context.Save(); context.Clip(bar);
        context.Text(scene.ClipTitle, bar.X + 12, bar.Y + 10, Theme.Foreground, 12, Theme.FontFamily);
        var grip = Math.Min(8, bar.Width / 3);
        context.Fill(new(bar.X, bar.Y + 3, grip, Math.Max(0, bar.Height - 6)), Theme.Accent, 2);
        context.Fill(new(bar.X + bar.Width - grip, bar.Y + 3, grip, Math.Max(0, bar.Height - 6)), Theme.Accent, 2);
        context.Restore();
        var waveY = bar.Y + bar.Height + 6;
        var waveHeight = Math.Max(0, Math.Min(22, area.Y + area.Height - waveY - 2));
        if (HasVisibleTracks(scene))
        {
            var height = Math.Max(0, Bounds.Height - 64) / 4;
            if (height >= 15)
            {
                string[] lanes = ["Музыка", "Звук сегмента", "Звук клипа", "Титры"];
                for (var index = 0; index < lanes.Length; index++)
                {
                    var y = Bounds.Y + 64 + index * height;
                    context.Fill(new(Bounds.X, y + height - 1, Bounds.Width, 1), Theme.Border with { A = 72 });
                    context.Text(lanes[index], Bounds.X + 5, y + 1, Theme.Disabled, 10, Theme.FontFamily);
                }
            }
            foreach (var item in scene.Tracks!.Where(item => item.LaneIndex < 4))
            {
                var bounds = TrackBounds(item);
                if (bounds.Height < 2 || bounds.X >= area.X + area.Width || bounds.X + bounds.Width <= area.X) continue;
                var color = item.LaneIndex == 3 ? Theme.Modified : Theme.Accent;
                context.Fill(bounds, color with { A = (byte)(item.Muted ? 32 : 84) }, 3);
                context.Save(); context.Clip(bounds);
                if (bounds.Height >= 11)
                {
                    var left = Math.Max(bounds.X, Bounds.X);
                    var text = item.Track == "music" ? item.Lane : item.Name;
                    context.Text(text, left + 4, bounds.Y + 1, item.Muted ? Theme.Disabled : Theme.Foreground, 10, Theme.FontFamily);
                }
                context.Restore();
            }
        }
        else if (waveHeight > 0)
        {
            var middle = waveY + waveHeight / 2;
            context.Fill(new(bar.X, middle, bar.Width, 1), Theme.Border);
            if (model.AudioPeaks is { Length: > 0 } peaks)
            {
                var columns = Math.Max(1, Math.Min(peaks.Length, (int)(bar.Width / 2)));
                for (var column = 0; column < columns; column++)
                {
                    float peak = 0;
                    for (var bin = column * peaks.Length / columns; bin < (column + 1) * peaks.Length / columns; bin++) peak = Math.Max(peak, peaks[bin]);
                    if (peak <= 0) continue;
                    var height = Math.Max(1, peak * waveHeight);
                    context.Fill(new(bar.X + bar.Width * column / columns, middle - height / 2, 1, height), Theme.Accent);
                }
            }
        }
        var frame = model.TimelineCursor;
        if ((model.Assigned || IsDragging) && frame >= model.TimelineStart && frame < model.TimelineStart + model.TimelineDuration)
            context.Fill(new(FrameX(frame), area.Y + 18, 2, Math.Max(0, area.Height - 18)), Theme.Focus);
        context.Restore();
    }
}
