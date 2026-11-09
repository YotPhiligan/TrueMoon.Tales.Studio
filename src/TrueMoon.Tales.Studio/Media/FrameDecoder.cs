using System.Diagnostics;
using System.Globalization;

namespace TrueMoon.Tales.Studio.Media;

internal sealed class FrameDecoder(string executable) : IFrameDecoder
{
    public const int Width = 480;
    public const int Height = 270;

    public async Task<byte[]> ReadAsync(Uri media, int frame, double fps, CancellationToken cancellationToken)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(TimeSpan.FromSeconds(10));
        var start = new ProcessStartInfo(executable)
        {
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true
        };
        // Seek just before the requested CFR sample, so rounding the seek time cannot skip it.
        var seconds = (Math.Max(0, frame - 0.25) / fps).ToString("F9", CultureInfo.InvariantCulture);
        string[] arguments = ["-nostdin", "-v", "error", "-ss", seconds, "-i", media.AbsoluteUri,
            "-map", "0:v:0", "-frames:v", "1", "-an", "-vf",
            $"scale={Width}:{Height}:force_original_aspect_ratio=decrease,pad={Width}:{Height}:(ow-iw)/2:(oh-ih)/2",
            "-threads", "1", "-pix_fmt", "bgra", "-f", "rawvideo", "pipe:1"];
        foreach (var argument in arguments)
            start.ArgumentList.Add(argument);

        using var process = Process.Start(start) ?? throw new InvalidOperationException("FFmpeg не запустился.");
        var errors = process.StandardError.ReadToEndAsync();
        try
        {
            var pixels = new byte[Width * Height * 4];
            await process.StandardOutput.BaseStream.ReadExactlyAsync(pixels, timeout.Token);
            await process.WaitForExitAsync(timeout.Token);
            var error = await errors;
            if (process.ExitCode != 0)
                throw new InvalidDataException($"FFmpeg завершился с кодом {process.ExitCode}. {error.Trim()}");
            return pixels;
        }
        catch (EndOfStreamException)
        {
            var error = await errors.WaitAsync(timeout.Token);
            throw new InvalidDataException($"FFmpeg не вернул кадр {frame}. {error.Trim()}");
        }
        finally
        {
            if (!process.HasExited)
            {
                try { process.Kill(); }
                catch (InvalidOperationException) when (process.HasExited) { }
                await process.WaitForExitAsync();
            }
            await errors;
        }
    }
}
