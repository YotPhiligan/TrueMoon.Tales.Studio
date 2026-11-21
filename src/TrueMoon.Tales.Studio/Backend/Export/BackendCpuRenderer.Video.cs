using System.Globalization;
using System.Runtime.InteropServices;
using SkiaSharp;
using TrueMoon.Alloy.Rendering.Skia;

namespace TrueMoon.Tales.Studio.Backend.Export;

internal sealed partial class BackendCpuRenderer
{
    private async Task RenderVideoAsync(IEnumerable<Part> parts)
    {
        SkiaNativeLibrary.Initialize();
        var imageInfo = new SKImageInfo(Width, Height, SKColorType.Bgra8888, SKAlphaType.Opaque);
        using var bitmap = new SKBitmap(imageInfo);
        using var canvas = new SKCanvas(bitmap);
        var buffer = new byte[checked(Width * Height * 4)];
        var pixels = bitmap.GetPixels();
        if (pixels == IntPtr.Zero || bitmap.ByteCount != buffer.Length) throw Invalid();

        using var titles = new ExportTitles(request, AssetPath);
        using var tint = new SKPaint { Color = SKColors.Black };
        var video = JobFile("Video.mp4", allowMissing: true);
        var encoding = request.Encoding;
        await using var encoder = new RenderProcess(ffmpeg,
            ["-f", "rawvideo", "-pixel_format", "bgra", "-video_size", $"{Width}x{Height}", "-framerate", request.Fps.ToString(CultureInfo.InvariantCulture),
             "-i", "pipe:0", "-map", "0:v:0", "-an", "-c:v", encoding.VideoCodec, "-pix_fmt", encoding.PixelFormat, "-bf", "0",
             "-threads:v", "2", "-preset", encoding.Preset, "-crf", encoding.Crf.ToString(CultureInfo.InvariantCulture), "-n", video], token, encode: true);
        var effectIndex = 0;
        var stillIndex = 0;
        SKBitmap? still = null;
        try
        {
            foreach (var part in parts)
            {
                var clip = part.Clip;
                await using var source = clip is null ? null : OpenSource(clip, part.Start, buffer.Length);
                for (var frame = part.Start; frame < part.End; frame++)
                {
                    token.ThrowIfCancellationRequested();
                    while (effectIndex < request.TransitionWindows.Length && frame >= request.TransitionWindows[effectIndex].InEndFrame)
                        effectIndex++;
                    var level = effectIndex < request.TransitionWindows.Length ? Brightness(request.TransitionWindows[effectIndex], frame) : 1;
                    byte[]? sourcePixels = source is null ? null : await source.AtAsync(Position(clip!, frame), token).ConfigureAwait(false);
                    while (stillIndex < request.AnimaticStills.Length && frame >= request.AnimaticStills[stillIndex].StartFrame + request.AnimaticStills[stillIndex].DurationFrames)
                    {
                        still?.Dispose();
                        still = null;
                        stillIndex++;
                    }
                    var onStill = sourcePixels is null && stillIndex < request.AnimaticStills.Length && frame >= request.AnimaticStills[stillIndex].StartFrame;
                    if (sourcePixels is not null && level == 1 && !titles.IsActive(frame))
                        await encoder.Input.WriteAsync(sourcePixels, token).ConfigureAwait(false);
                    else
                    {
                        if (sourcePixels is null) canvas.Clear(SKColors.Black);
                        else
                        {
                            Marshal.Copy(sourcePixels, 0, pixels, buffer.Length);
                            bitmap.NotifyPixelsChanged();
                        }
                        if (onStill)
                        {
                            still ??= ReadStill(AssetPath(request.AnimaticStills[stillIndex].AssetId));
                            DrawStill(canvas, still);
                        }
                        if (level < 1)
                            canvas.DrawColor(tint.Color.WithAlpha((byte)Math.Round((1 - level) * 255)), SKBlendMode.SrcOver);
                        titles.Draw(canvas, frame);
                        canvas.Flush();
                        Marshal.Copy(pixels, buffer, 0, buffer.Length);
                        await encoder.Input.WriteAsync(buffer, token).ConfigureAwait(false);
                    }
                    Progress("Изображение", 5 + 70d * (frame - request.StartFrame + 1) / request.Frames);
                }
            }
        }
        finally { still?.Dispose(); await encoder.Input.DisposeAsync().ConfigureAwait(false); }
        await encoder.CompleteAsync(token).ConfigureAwait(false);
    }

    private FrameSource OpenSource(RenderClip clip, int first, int bufferSize)
    {
        var args = new List<string> { "-copyts", "-start_at_zero", "-noautorotate", "-threads", "2" };
        var firstSecond = Position(clip, first);
        if (firstSecond > 0) args.AddRange(["-ss", Number(firstSecond), "-noaccurate_seek"]);
        args.AddRange(BackendMediaDecoder.Input(AssetPath(clip.AssetId)));
        args.AddRange(["-map", "0:v:0", "-an", "-sn", "-dn", "-vf",
            $"scale={Width}:{Height}:force_original_aspect_ratio=decrease:flags=bilinear,pad={Width}:{Height}:(ow-iw)/2:(oh-ih)/2,setsar=1,format=bgra,settb=1/1000000,showinfo=checksum=0",
            "-fps_mode", "passthrough", "-threads:v", "1", "-f", "rawvideo", "-pix_fmt", "bgra", "pipe:1"]);
        return new(new RenderProcess(ffmpeg, args, token, decode: true), bufferSize);
    }

    private double Position(RenderClip clip, int frame) =>
        Math.Min((clip.SourceInFrame + (frame - clip.StartFrame) * clip.Speed) / request.Fps,
            Math.Max(clip.SourceInFrame / request.Fps, clip.SourceOutFrame / request.Fps - 1e-7));

    private static double Brightness(BlackTransition transition, int frame)
    {
        if (frame < transition.OutStartFrame) return 1;
        if (frame < transition.BlackStartFrame) return (transition.BlackStartFrame - frame - .5) / transition.FadeOutFrames;
        if (frame < transition.RightStartFrame) return 0;
        return (frame - transition.RightStartFrame + .5) / transition.FadeInFrames;
    }

    private static SKBitmap ReadStill(string path)
    {
        using var codec = SKCodec.Create(path);
        if (codec is null || codec.EncodedFormat is not (SKEncodedImageFormat.Png or SKEncodedImageFormat.Jpeg) ||
            (long)codec.Info.Width * codec.Info.Height > 16_000_000 || codec.FrameCount > 1)
            throw new InvalidDataException("Unsupported animatic still.");
        return SKBitmap.Decode(codec) ?? throw new InvalidDataException("Could not decode animatic still.");
    }

    private void DrawStill(SKCanvas canvas, SKBitmap bitmap)
    {
        var scale = Math.Min(Width / (double)bitmap.Width, Height / (double)bitmap.Height);
        var width = (float)Math.Max(1, Math.Round(bitmap.Width * scale));
        var height = (float)Math.Max(1, Math.Round(bitmap.Height * scale));
        canvas.DrawBitmap(bitmap, SKRect.Create(MathF.Floor((Width - width) / 2), MathF.Floor((Height - height) / 2), width, height),
            new SKSamplingOptions(SKFilterMode.Linear));
    }

    private sealed class FrameSource(RenderProcess process, int size) : IAsyncDisposable
    {
        private byte[] _current = new byte[size];
        private byte[] _next = new byte[size];
        private long? _nextTimestamp;
        private bool _initialized;

        internal async Task<byte[]> AtAsync(double seconds, CancellationToken token)
        {
            if (!_initialized)
            {
                if (await ReadFrameAsync(_current, token).ConfigureAwait(false) is null)
                    throw new InvalidDataException("No decodable export source frames.");
                _nextTimestamp = await ReadFrameAsync(_next, token).ConfigureAwait(false);
                _initialized = true;
            }
            var sample = (long)Math.Floor(seconds * 1_000_000 + .51);
            while (_nextTimestamp <= sample)
            {
                (_current, _next) = (_next, _current);
                _nextTimestamp = await ReadFrameAsync(_next, token).ConfigureAwait(false);
            }
            return _current;
        }

        private async Task<long?> ReadFrameAsync(byte[] buffer, CancellationToken token)
        {
            string? line;
            while ((line = await process.Diagnostics.ReadLineAsync(token).ConfigureAwait(false)) is not null)
            {
                var start = line.IndexOf(" pts:", StringComparison.Ordinal);
                if (start < 0 || !line.Contains("showinfo", StringComparison.Ordinal)) continue;
                var end = line.IndexOf("pts_time:", start, StringComparison.Ordinal);
                if (end < 0 || !long.TryParse(line.AsSpan(start + 5, end - start - 5), NumberStyles.Integer, CultureInfo.InvariantCulture, out var timestamp))
                    throw new InvalidDataException("FFmpeg returned an invalid export source timestamp.");
                await process.Output.ReadExactlyAsync(buffer, token).ConfigureAwait(false);
                return timestamp;
            }
            await process.CompleteAsync(token).ConfigureAwait(false);
            return null;
        }

        public ValueTask DisposeAsync() => process.DisposeAsync();
    }
}
