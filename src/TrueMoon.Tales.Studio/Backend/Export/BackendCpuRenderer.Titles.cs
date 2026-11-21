using System.Text;
using SkiaSharp;

namespace TrueMoon.Tales.Studio.Backend.Export;

internal sealed partial class BackendCpuRenderer
{
    private sealed class ExportTitles(RenderRequest request, Func<string, string> path) : IDisposable
    {
        private readonly Dictionary<RenderTitle, TitleLayout> _layouts = [];
        private readonly Dictionary<string, SKTypeface> _fonts = new(StringComparer.Ordinal);
        private readonly SKPaint _fill = new() { IsAntialias = true };
        private readonly SKPaint _outline = new() { IsAntialias = true, Style = SKPaintStyle.Stroke, StrokeJoin = SKStrokeJoin.Round, Color = SKColors.Black };
        private readonly SKPaint _opacity = new();
        private readonly float _scale = Math.Min(request.Size[0] / (float)request.TitleCanvas[0], request.Size[1] / (float)request.TitleCanvas[1]);

        internal bool IsActive(int frame) => request.Titles.Any(title => Active(title, frame));

        internal void Draw(SKCanvas canvas, int frame)
        {
            foreach (var title in request.Titles)
            {
                if (!Active(title, frame)) continue;
                if (!_layouts.TryGetValue(title, out var layout)) _layouts.Add(title, layout = Layout(title));
                var level = Fade(title, frame);
                var alpha = (byte)Math.Round(level * 255);
                if (title.Background == "black") canvas.DrawColor(SKColors.Black.WithAlpha(alpha), SKBlendMode.SrcOver);
                var saved = canvas.Save();
                try
                {
                    canvas.Translate((request.Size[0] - request.TitleCanvas[0] * _scale) / 2, (request.Size[1] - request.TitleCanvas[1] * _scale) / 2);
                    canvas.Scale(_scale);
                    if (alpha < 255)
                    {
                        _opacity.Color = SKColors.White.WithAlpha(alpha);
                        canvas.SaveLayer(_opacity);
                    }
                    _fill.Color = layout.Color;
                    _outline.StrokeWidth = layout.Stroke * 2;
                    foreach (var line in layout.Lines)
                    {
                        if (title.Outline)
                            canvas.DrawText(line.Text, line.X, line.Y, SKTextAlign.Left, layout.Font, _outline);
                        canvas.DrawText(line.Text, line.X, line.Y, SKTextAlign.Left, layout.Font, _fill);
                    }
                }
                finally { canvas.RestoreToCount(saved); }
            }
        }

        private TitleLayout Layout(RenderTitle title)
        {
            if (!_fonts.TryGetValue(title.FontAssetId, out var face))
            {
                face = SKTypeface.FromFile(path(title.FontAssetId)) ?? throw new InvalidDataException("A frozen title font could not be opened.");
                _fonts.Add(title.FontAssetId, face);
            }
            var font = new SKFont(face, title.FontSize);
            try
            {
                if (!SKColor.TryParse(title.Color, out var color)) throw Invalid();
                var width = request.TitleCanvas[0];
                var height = request.TitleCanvas[1];
                var stroke = title.Outline ? Math.Max(1, (float)Math.Round(title.FontSize / 28d)) : 0;
                var lines = Wrap(title.Text, font, width * .84f - 2 * stroke).ToArray();
                var lineHeight = font.Metrics.Descent - font.Metrics.Ascent;
                var advance = lineHeight + Math.Max(1, (float)Math.Round(title.FontSize * .25));
                var maxWidth = lines.Max(row => font.MeasureText(row)) + 2 * stroke;
                var textHeight = (lines.Length - 1) * advance + lineHeight + 2 * stroke;
                if (maxWidth > width * .90f || textHeight > height * .84f + title.FontSize * .25f)
                    throw new InvalidDataException("The frozen title does not fit its canvas.");
                var y = title.Position switch
                {
                    "upper" => height * .08f,
                    "lower" => height * .92f - textHeight,
                    _ => (height - textHeight) / 2
                };
                var positioned = lines.Select((line, index) => new TitleLine(line,
                    title.Align switch
                    {
                        "left" => width * .08f + stroke,
                        "right" => width * .92f - font.MeasureText(line) - stroke,
                        _ => (width - font.MeasureText(line)) / 2
                    }, y + stroke - font.Metrics.Ascent + index * advance)).ToArray();
                return new(font, color, stroke, positioned);
            }
            catch { font.Dispose(); throw; }
        }

        private static IEnumerable<string> Wrap(string text, SKFont font, float width)
        {
            foreach (var paragraph in text.Split('\n'))
            {
                var line = "";
                foreach (var word in paragraph.Split(' '))
                {
                    var candidate = line.Length == 0 ? word : line + " " + word;
                    if (font.MeasureText(candidate) <= width) { line = candidate; continue; }
                    if (line.Length > 0) { yield return line; line = ""; }
                    foreach (var rune in word.EnumerateRunes())
                    {
                        var letter = rune.ToString();
                        if (line.Length > 0 && font.MeasureText(line + letter) > width) { yield return line; line = ""; }
                        line += letter;
                    }
                }
                yield return line;
            }
        }

        private static bool Active(RenderTitle title, int frame) => frame >= title.StartFrame && frame < title.StartFrame + title.DurationFrames;

        private static double Fade(RenderTitle title, int frame)
        {
            var offset = frame - title.StartFrame;
            if (offset < title.FadeInFrames) return (offset + .5) / title.FadeInFrames;
            var remaining = title.DurationFrames - offset;
            return remaining <= title.FadeOutFrames ? (remaining - .5) / title.FadeOutFrames : 1;
        }

        public void Dispose()
        {
            _fill.Dispose();
            _outline.Dispose();
            _opacity.Dispose();
            foreach (var layout in _layouts.Values) layout.Font.Dispose();
            foreach (var font in _fonts.Values) font.Dispose();
        }

        private sealed record TitleLayout(SKFont Font, SKColor Color, float Stroke, TitleLine[] Lines);
        private sealed record TitleLine(string Text, float X, float Y);
    }
}
