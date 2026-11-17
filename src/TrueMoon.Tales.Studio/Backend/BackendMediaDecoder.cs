using System.Diagnostics;
using System.Globalization;
using System.Text.Json;
using TrueMoon.Tales.Studio.Media;

namespace TrueMoon.Tales.Studio.Backend;

internal sealed record BackendVideoInfo(double Fps, int FrameCount, int Width, int Height);

// Input paths and executable names have already been selected by trusted workspace policy.
// No shell, network protocols, playlists or external MOV data references are admitted here.
internal sealed class BackendMediaDecoder(string ffmpegExecutable, string ffprobeExecutable,
    Func<ProcessStartInfo, Process>? startProcess = null)
{
    internal const int FrameBytes = FrameDecoder.Width * FrameDecoder.Height * 4;
    internal const int MaxProbeBytes = 64 * 1024;
    internal const int MaxErrorBytes = 16 * 1024;
    internal const int MaxFrames = 10_000_000;
    private readonly Func<ProcessStartInfo, Process> _start = startProcess ??
        (start => Process.Start(start) ?? throw new IOException("Media process did not start."));

    internal async Task<BackendVideoInfo> ProbeAsync(string physicalPath, CancellationToken token)
    {
        var arguments = new List<string> { "-v", "error", "-threads", "1", "-max_alloc", "67108864" };
        arguments.AddRange(Input(physicalPath));
        arguments.AddRange(["-select_streams", "v:0", "-show_entries",
            "stream=codec_type,width,height,avg_frame_rate,r_frame_rate,nb_frames,duration:format=duration", "-of", "json"]);
        return Parse(await RunAsync(ffprobeExecutable, arguments, MaxProbeBytes, token).ConfigureAwait(false));
    }

    internal async Task<byte[]> ReadAsync(string physicalPath, int frame, BackendVideoInfo info, CancellationToken token)
    {
        Validate(info);
        if (frame < 0 || frame >= info.FrameCount) throw new ArgumentOutOfRangeException(nameof(frame));
        // Seek just before the requested CFR sample, matching the existing preview's sample rule.
        var seconds = (Math.Max(0, frame - 0.25) / info.Fps).ToString("F9", CultureInfo.InvariantCulture);
        var arguments = new List<string> { "-nostdin", "-v", "error", "-threads", "1", "-max_alloc", "67108864", "-ss", seconds };
        arguments.AddRange(Input(physicalPath));
        arguments.AddRange(["-map", "0:v:0", "-frames:v", "1", "-an", "-sn", "-dn", "-vf",
            $"scale={FrameDecoder.Width}:{FrameDecoder.Height}:force_original_aspect_ratio=decrease,pad={FrameDecoder.Width}:{FrameDecoder.Height}:(ow-iw)/2:(oh-ih)/2",
            "-threads", "1", "-pix_fmt", "bgra", "-f", "rawvideo", "pipe:1"]);
        var pixels = await RunAsync(ffmpegExecutable, arguments, FrameBytes, token).ConfigureAwait(false);
        if (pixels.Length != FrameBytes) throw new InvalidDataException("FFmpeg returned an incomplete video frame.");
        return pixels;
    }

    internal static BackendVideoInfo Parse(ReadOnlyMemory<byte> json)
    {
        if (json.Length > MaxProbeBytes) throw new InvalidDataException("Media probe exceeded its output limit.");
        try
        {
            using var document = JsonDocument.Parse(json, new JsonDocumentOptions { MaxDepth = 8 });
            var streams = document.RootElement.GetProperty("streams");
            if (streams.ValueKind != JsonValueKind.Array || streams.GetArrayLength() != 1)
                throw new InvalidDataException("Exactly one selected video stream is required.");
            var stream = streams[0];
            if (stream.GetProperty("codec_type").GetString() != "video") throw new InvalidDataException("A video stream is required.");
            var fps = Rate(stream.GetProperty("avg_frame_rate").GetString());
            var nominal = Rate(stream.GetProperty("r_frame_rate").GetString());
            if (Math.Abs(fps - nominal) > Math.Max(fps, nominal) * 0.000001)
                throw new InvalidDataException("Variable frame-rate media is not supported by this frame reader.");
            int frames;
            if (stream.TryGetProperty("nb_frames", out var count) && count.GetString() is { Length: > 0 } value && value != "N/A")
            {
                if (!int.TryParse(value, NumberStyles.None, CultureInfo.InvariantCulture, out frames))
                    throw new InvalidDataException("Invalid video frame count.");
            }
            else
            {
                var duration = stream.TryGetProperty("duration", out var ownDuration) && ownDuration.GetString() is { } own && own != "N/A"
                    ? own : document.RootElement.GetProperty("format").GetProperty("duration").GetString();
                if (!double.TryParse(duration, NumberStyles.Float, CultureInfo.InvariantCulture, out var seconds) ||
                    !double.IsFinite(seconds) || seconds <= 0 || seconds > 86400)
                    throw new InvalidDataException("Invalid video duration.");
                var extent = Math.Ceiling(seconds * fps - 0.000000001);
                if (extent < 1 || extent > MaxFrames) throw new InvalidDataException("Video frame extent is outside the supported range.");
                frames = checked((int)extent);
            }
            var info = new BackendVideoInfo(fps, frames, stream.GetProperty("width").GetInt32(), stream.GetProperty("height").GetInt32());
            Validate(info);
            return info;
        }
        catch (Exception error) when (error is JsonException or KeyNotFoundException or InvalidOperationException or FormatException or OverflowException)
        { throw new InvalidDataException("Invalid media probe response.", error); }
    }

    private static double Rate(string? value)
    {
        var parts = value?.Split('/');
        if (parts is not { Length: 2 } || !long.TryParse(parts[0], NumberStyles.None, CultureInfo.InvariantCulture, out var numerator) ||
            !long.TryParse(parts[1], NumberStyles.None, CultureInfo.InvariantCulture, out var denominator) || numerator <= 0 || denominator <= 0)
            throw new InvalidDataException("Invalid video frame rate.");
        var rate = (double)numerator / denominator;
        if (!double.IsFinite(rate) || rate <= 0 || rate > 240) throw new InvalidDataException("Video frame rate is outside the supported range.");
        return rate;
    }

    private static void Validate(BackendVideoInfo info)
    {
        if (info is null || !double.IsFinite(info.Fps) || info.Fps <= 0 || info.Fps > 240 || info.FrameCount is < 1 or > MaxFrames ||
            info.Width is < 1 or > 8192 || info.Height is < 1 or > 8192 || (long)info.Width * info.Height > 16_777_216)
            throw new InvalidDataException("Video metadata is outside the supported range.");
    }

    private static string[] Input(string path)
    {
        if (string.IsNullOrWhiteSpace(path) || !Path.IsPathFullyQualified(path) || path.StartsWith("\\\\", StringComparison.Ordinal) ||
            path.Contains('\0')) throw new UnauthorizedAccessException("A resolved local media path is required.");
        var format = Path.GetExtension(path).ToLowerInvariant() switch
        {
            ".mp4" or ".m4v" or ".mov" or ".3gp" => "mov",
            ".mkv" or ".webm" => "matroska",
            ".avi" => "avi",
            _ => throw new InvalidDataException("Unsupported local video container.")
        };
        var arguments = new List<string> { "-protocol_whitelist", "file,pipe", "-f", format };
        if (format == "mov") arguments.AddRange(["-enable_drefs", "0", "-use_absolute_path", "0"]);
        arguments.AddRange(["-probesize", "5242880", "-analyzeduration", "5000000", "-i", path]);
        return arguments.ToArray();
    }

    private async Task<byte[]> RunAsync(string executable, IEnumerable<string> arguments, int outputLimit, CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(token);
        deadline.CancelAfter(TimeSpan.FromSeconds(8));
        var start = new ProcessStartInfo(executable)
        {
            UseShellExecute = false, CreateNoWindow = true,
            RedirectStandardOutput = true, RedirectStandardError = true
        };
        foreach (var argument in arguments) start.ArgumentList.Add(argument);
        using var process = _start(start);
        using var cancellation = deadline.Token.Register(() => Kill(process));
        var output = ReadLimitedAsync(process.StandardOutput.BaseStream, outputLimit, deadline.Token);
        var errors = ReadLimitedAsync(process.StandardError.BaseStream, MaxErrorBytes, deadline.Token);
        var exited = process.WaitForExitAsync(deadline.Token);
        try
        {
            // Observe stream faults immediately, rather than waiting for a child blocked on a full pipe.
            var pending = new List<Task> { output, errors, exited };
            while (pending.Count != 0)
            {
                var ready = await Task.WhenAny(pending).ConfigureAwait(false);
                await ready.ConfigureAwait(false);
                pending.Remove(ready);
            }
            deadline.Token.ThrowIfCancellationRequested();
            if (process.ExitCode != 0) throw new InvalidDataException($"Media process exited with code {process.ExitCode}.");
            return await output.ConfigureAwait(false);
        }
        finally
        {
            Kill(process);
            await process.WaitForExitAsync(CancellationToken.None).WaitAsync(TimeSpan.FromSeconds(3)).ConfigureAwait(false);
            // Observe both readers on every failure path, including cancellation and oversized output.
            try { await Task.WhenAll(output, errors).WaitAsync(TimeSpan.FromSeconds(3)).ConfigureAwait(false); }
            catch (Exception error) when (error is IOException or OperationCanceledException) { }
            try { await exited.WaitAsync(TimeSpan.FromSeconds(3)).ConfigureAwait(false); }
            catch (OperationCanceledException) { }
        }
    }

    private static async Task<byte[]> ReadLimitedAsync(Stream stream, int limit, CancellationToken token)
    {
        using var bytes = new MemoryStream(Math.Min(limit, 8192));
        var buffer = new byte[8192];
        while (true)
        {
            var count = await stream.ReadAsync(buffer, token).ConfigureAwait(false);
            if (count == 0) return bytes.ToArray();
            if (bytes.Length + count > limit) throw new InvalidDataException("Media process exceeded its output limit.");
            bytes.Write(buffer, 0, count);
        }
    }

    private static void Kill(Process process)
    {
        try { if (!process.HasExited) process.Kill(entireProcessTree: true); }
        catch (InvalidOperationException) { }
        catch (System.ComponentModel.Win32Exception) { }
    }
}
