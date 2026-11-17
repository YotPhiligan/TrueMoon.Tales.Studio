using TrueMoon.Argentis;
using TrueMoon.Tales.Studio.Editing;
using TrueMoon.Tales.Studio.Models;

namespace TrueMoon.Tales.Studio.Presentation.Scene;

internal sealed class SceneStrip(SceneViewModel model, Action updated) : Element
{
    private IInputContext? _context;
    private Rect _gestureBounds;
    private float _originX;
    private bool _trimming;
    private bool _body, _moving, _pendingMove;
    private double _clickFrame;
    public bool IsDragging => _context is not null;
    public override bool Focusable => true;

    private float Scale => Bounds.Width / Math.Max(1, model.Scene!.Segment.DurationFrames);
    private double FrameAt(float x) => model.Scene!.Segment.StartFrame + (x - Bounds.X) / Math.Max(float.Epsilon, Scale);
    internal Rect ClipBounds => model.Scene is { } scene
        ? new(Bounds.X + (model.TimelineStart - scene.Segment.StartFrame) * Scale, Bounds.Y + 24,
            model.TimelineDuration * Scale, Math.Max(0, Math.Min(38, Bounds.Height - 24))) : default;

    public override bool HandleInput(UiInput input, IInputContext context)
    {
        if (IsDragging)
        {
            if (input.Kind == InputKind.FocusLost || input.Kind == InputKind.KeyDown && input.Key == UiKey.Escape)
            { CancelGesture(); return true; }
            if (Bounds != _gestureBounds || !IsVisible || !IsEffectivelyEnabled)
            { CancelGesture(); return true; }
            if (input.Kind == InputKind.PointerMove) { Move(input.X); return true; }
            if (input.Kind == InputKind.PointerUp && input.Button == 0)
            {
                if (!float.IsFinite(input.X)) { CancelGesture(); return true; }
                try
                {
                    Move(input.X);
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
        }
        else if (frame < model.TimelineStart || frame >= model.TimelineStart + model.TimelineDuration) return false;
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

    private void Move(float x)
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
            model.MoveTrim((int)Math.Clamp(delta, int.MinValue + 1d, int.MaxValue));
        }
        else if (_moving)
        {
            var delta = Math.Round(((double)x - _originX) / Scale, MidpointRounding.AwayFromZero);
            model.MoveClip((int)Math.Clamp(delta, int.MinValue + 1d, int.MaxValue));
        }
        else if (!_body) model.SeekTimeline(FrameAt(x));
        updated();
    }

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
        if (model.Scene is not { } scene) return;
        var area = Bounds;
        var frames = Math.Max(1, scene.Segment.DurationFrames);
        for (var i = 0; i <= 6; i++)
        {
            var x = area.X + area.Width * i / 6;
            context.Fill(new(x, area.Y + 18, 1, Math.Max(0, area.Height - 18)), Theme.Border with { A = 55 });
            if (i < 6) context.Text($"{scene.Segment.StartFrame + frames * i / 6} к", x + 4, area.Y, Theme.Disabled, 10, Theme.FontFamily);
        }
        var bar = ClipBounds;
        foreach (var clip in model.PreviewClips.Where(clip => clip.Id != scene.Clip.Id))
        {
            var neighbor = new Rect(area.X + (clip.StartFrame - scene.Segment.StartFrame) * Scale, bar.Y,
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
        if (waveHeight > 0)
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
            context.Fill(new(area.X + (float)(frame - scene.Segment.StartFrame) * Scale, area.Y + 18, 2, Math.Max(0, area.Height - 18)), Theme.Focus);
    }
}
