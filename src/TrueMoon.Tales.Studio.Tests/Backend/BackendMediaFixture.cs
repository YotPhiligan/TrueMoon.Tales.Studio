using System.Diagnostics;
using System.Text.Json.Nodes;
using TrueMoon.Tales.Studio.Backend;
using TrueMoon.Tales.Studio.Persistence;
using TrueMoon.Tales.Studio.Tests.Persistence;
using Xunit;

namespace TrueMoon.Tales.Studio.Tests.Backend;

internal sealed class BackendMediaFixture : IAsyncDisposable
{
    internal BackendHttpFixture Backend { get; } = new();
    internal PersistenceFixture Files => Backend.Files.Files;
    internal string VideoPath { get; }
    internal string SecondVideoPath { get; }
    internal string PngPath => Path.Combine(Files.Root, "frame20.png");
    internal string LegacyRoot { get; }
    internal OpenedBackendProject Baseline { get; private set; } = null!;
    private byte[]? _project;
    private Dictionary<string, byte[]>? _history;
    private readonly string _ffmpeg = Environment.GetEnvironmentVariable("STUDIO_TEST_FFMPEG")!;

    internal BackendMediaFixture()
    {
        var media = Path.Combine(Files.Root, "media");
        Directory.CreateDirectory(Path.Combine(media, "new"));
        VideoPath = Path.Combine(media, "new", "Кадр с пробелом.mp4");
        SecondVideoPath = Path.Combine(media, "new", "Другой материал.mp4");
        LegacyRoot = Path.Combine(Files.Root, "legacy-never-created");
        var config = JsonNode.Parse(File.ReadAllText(Backend.Files.Base.ConfigPath))!;
        config["legacy_root"] = LegacyRoot;
        config["legacy_read_enabled"] = false;
        config["projects_root"] = Files.ProjectDirectory;
        config["mounts"] = new JsonObject { ["videos"] = media };
        config["ffmpeg_executable"] = _ffmpeg;
        config["ffprobe_executable"] = Environment.GetEnvironmentVariable("STUDIO_TEST_FFPROBE");
        config["path_aliases"] = new JsonArray(
            new JsonObject { ["from"] = "Old/Media", ["to"] = "@videos/old" },
            new JsonObject { ["from"] = Path.Combine(LegacyRoot, "Absolute"), ["to"] = "@videos/old" },
            new JsonObject { ["from"] = "@videos/old", ["to"] = "@videos/new" },
            new JsonObject { ["from"] = "Other/Media", ["to"] = "@videos/new" });
        File.WriteAllText(Backend.Files.Base.ConfigPath, config.ToJsonString());
        var project = JsonNode.Parse(File.ReadAllText(Files.ProjectPath))!;
        project["assets"]!["video-a"]!["path"] = "Old/Media/Кадр с пробелом.mp4";
        project["assets"]!["video-b"]!["path"] = Path.Combine(LegacyRoot, "Absolute", "Кадр с пробелом.mp4");
        project["assets"]!["video-named"] = new JsonObject { ["path"] = "@videos/old/Кадр с пробелом.mp4" };
        project["blocks"]![0]!["takes"]![1]!["video_asset_id"] = "video-named";
        var clip = project["timeline"]!["clips"]![0]!;
        clip["source_in_frame"] = 4;
        clip["source_out_frame"] = 52;
        clip["speed"] = 2;
        clip["duration_frames"] = 24;
        File.WriteAllText(Files.ProjectPath, project.ToJsonString());
    }

    internal async Task StartAsync(bool missing = false, bool distinctSecond = false)
    {
        if (!missing)
            await RunFfmpegAsync(["-nostdin", "-v", "error", "-f", "lavfi", "-i", "testsrc2=size=320x240:rate=30:duration=3",
                "-map", "0:v:0", "-frames:v", "90", "-an", "-c:v", "libx264", "-preset", "ultrafast", "-crf", "18",
                "-pix_fmt", "yuv420p", "-threads", "1", "-n", VideoPath], 4096);
        if (distinctSecond)
        {
            await RunFfmpegAsync(["-nostdin", "-v", "error", "-f", "lavfi", "-i", "color=c=magenta:size=320x240:rate=30:duration=3",
                "-map", "0:v:0", "-frames:v", "90", "-an", "-c:v", "libx264", "-preset", "ultrafast", "-crf", "18",
                "-pix_fmt", "yuv420p", "-threads", "1", "-n", SecondVideoPath], 4096);
            var project = JsonNode.Parse(File.ReadAllText(Files.ProjectPath))!;
            project["assets"]!["video-b"]!["path"] = "Other/Media/Другой материал.mp4";
            File.WriteAllText(Files.ProjectPath, project.ToJsonString());
        }
        await Backend.StartAsync();
        Baseline = await Backend.Connection.OpenAsync();
        Assert.Equal(12, Baseline.Project.Revision);
        Assert.Single(Files.HistoryFiles);
        _project = File.ReadAllBytes(Files.ProjectPath);
        _history = Files.HistoryFiles.ToDictionary(path => Path.GetFileName(path)!, File.ReadAllBytes);
    }

    // Decode from frame zero and select by decoded sample number. No -ss or production decoder is used.
    internal Task<byte[]> ReferenceFrameAsync(int frame) => ReferenceFrameAsync(VideoPath, frame);

    internal async Task<byte[]> ReferenceFrameAsync(string videoPath, int frame)
    {
        var bytes = await RunFfmpegAsync(["-nostdin", "-v", "error", "-threads", "1", "-i", videoPath,
            "-map", "0:v:0", "-an", "-sn", "-dn", "-vf",
            $"select=eq(n\\,{frame}),scale=480:270:force_original_aspect_ratio=decrease,pad=480:270:(ow-iw)/2:(oh-ih)/2",
            "-frames:v", "1", "-fps_mode", "passthrough", "-threads", "1", "-pix_fmt", "bgra", "-f", "rawvideo", "pipe:1"], BackendMediaDecoder.FrameBytes);
        Assert.Equal(BackendMediaDecoder.FrameBytes, bytes.Length);
        return bytes;
    }

    internal void AssertUnchanged()
    {
        Assert.NotNull(_project); Assert.NotNull(_history);
        Assert.Equal(_project, File.ReadAllBytes(Files.ProjectPath));
        var actual = Files.HistoryFiles.ToDictionary(path => Path.GetFileName(path)!, File.ReadAllBytes);
        Assert.Equal(_history.Keys.OrderBy(key => key), actual.Keys.OrderBy(key => key));
        foreach (var pair in _history) Assert.Equal(pair.Value, actual[pair.Key]);
        var current = ProjectFileCodec.Read(File.ReadAllBytes(Files.ProjectPath));
        Assert.Equal(12, current.Project.Revision);
        Assert.Equal("Z:/not-an-allowed-write-root", current.Project.Properties["root"].GetString());
        Assert.False(File.Exists(Files.JournalPath));
        Assert.False(Directory.Exists(LegacyRoot));
    }

    private async Task<byte[]> RunFfmpegAsync(IEnumerable<string> arguments, int outputLimit)
    {
        var start = new ProcessStartInfo(_ffmpeg)
        { UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true, RedirectStandardError = true };
        foreach (var argument in arguments) start.ArgumentList.Add(argument);
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(20));
        using var process = Process.Start(start) ?? throw new InvalidOperationException("Fixture FFmpeg did not start.");
        var output = Limited(process.StandardOutput.BaseStream, outputLimit, timeout.Token);
        var error = Limited(process.StandardError.BaseStream, 16 * 1024, timeout.Token);
        try
        {
            await Task.WhenAll(output, error, process.WaitForExitAsync(timeout.Token)).WaitAsync(timeout.Token);
            Assert.True(process.ExitCode == 0, $"Fixture FFmpeg exit={process.ExitCode}: {System.Text.Encoding.UTF8.GetString(await error)}");
            return await output;
        }
        finally
        {
            if (!process.HasExited) process.Kill(entireProcessTree: true);
            await process.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(5));
            try { await Task.WhenAll(output, error).WaitAsync(TimeSpan.FromSeconds(5)); }
            catch (Exception failure) when (failure is IOException or OperationCanceledException) { }
        }
    }

    private static async Task<byte[]> Limited(Stream stream, int limit, CancellationToken token)
    {
        using var memory = new MemoryStream();
        var buffer = new byte[8192];
        while (true)
        {
            var count = await stream.ReadAsync(buffer, token);
            if (count == 0) return memory.ToArray();
            if (memory.Length + count > limit) throw new InvalidDataException("Fixture process output exceeded its limit.");
            memory.Write(buffer, 0, count);
        }
    }

    public ValueTask DisposeAsync() => Backend.DisposeAsync();
}

public sealed class BackendMediaTheoryAttribute : TheoryAttribute
{
    public BackendMediaTheoryAttribute(bool requirePython = true)
    {
        Skip = BackendMediaEnvironment.SkipReason(requirePython);
    }
}

public sealed class BackendMediaFactAttribute : FactAttribute
{
    public BackendMediaFactAttribute(bool requirePython = true)
    {
        Skip = BackendMediaEnvironment.SkipReason(requirePython);
    }
}

internal static class BackendMediaEnvironment
{
    internal static bool Missing => SkipReason(requirePython: true) is not null;

    internal static string? SkipReason(bool requirePython)
    {
        string[] keys = requirePython ? ["STUDIO_TEST_PYTHON", "STUDIO_TEST_FFMPEG", "STUDIO_TEST_FFPROBE"] : ["STUDIO_TEST_FFMPEG", "STUDIO_TEST_FFPROBE"];
        var missing = keys.Where(key => string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable(key))).ToArray();
        return missing.Length == 0 ? null : "Set " + string.Join(", ", missing) + ".";
    }
}
