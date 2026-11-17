using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text.Json;
using System.Text.Json.Nodes;
using TrueMoon.Tales.Studio.Backend;
using TrueMoon.Tales.Studio.Backend.Http;
using TrueMoon.Tales.Studio.Media;
using TrueMoon.Tales.Studio.Models;
using TrueMoon.Tales.Studio.Persistence;
using Xunit;

namespace TrueMoon.Tales.Studio.Tests.Backend;

internal sealed class BackendTrimFixture : IAsyncDisposable
{
    private static string MarkerFilter => "drawbox=x=0:y=0:w=320:h=120:color=black:t=fill," +
        string.Join(',', Enumerable.Range(0, 8).Select(bit => $"drawbox=x={bit * 40}:y=0:w=40:h=120:color=white:t=fill:enable='bitand(n,{1 << bit})'"));
    internal BackendPlaybackFixture Media { get; } = new(videoFilter: MarkerFilter);
    internal BackendHttpServer Http { get; }
    internal string ProjectPath => Media.Files.Files.ProjectPath;
    internal byte[] Initial { get; private set; } = [];
    internal Dictionary<string, byte[]> InitialHistory { get; private set; } = [];
    internal BackendTrimFixture(bool scenario = false, bool missing = false)
    {
        if (scenario)
        {
            var p = JsonNode.Parse(File.ReadAllText(ProjectPath))!;
            var timeline = p["timeline"]!;
            timeline["clips"]!.AsArray().Add(new JsonObject
            {
                ["id"] = "clip-follow", ["segment_id"] = "segment-main", ["block_id"] = "source-a", ["take_id"] = "take-a",
                ["start_frame"] = 112, ["duration_frames"] = 24, ["source_in_frame"] = 0, ["source_out_frame"] = 24, ["speed"] = 1
            });
            timeline["scenario"] = new JsonObject
            {
                ["version"] = 1, ["segments"] = new JsonArray(
                    new JsonObject { ["id"] = "segment-main", ["name"] = "Первый сегмент", ["start_frame"] = 0, ["duration_frames"] = 240,
                        ["items"] = new JsonArray(Item("clip-main", "source-a", "take-a", 96, 12, 4, 124, 1.25), Item("clip-follow", "source-a", "take-a", 24, 4, 0, 24, 1)) },
                    new JsonObject { ["id"] = "segment-extra", ["name"] = "Другой сегмент", ["start_frame"] = 300, ["duration_frames"] = 120,
                        ["items"] = new JsonArray(Item("clip-extra", "source-b", "take-b", 24, 12, 0, 24, 1)) })
            };
            File.WriteAllText(ProjectPath, p.ToJsonString());
        }
        if (missing)
        {
            var p = JsonNode.Parse(File.ReadAllText(ProjectPath))!;
            p["assets"]!["video-a"]!["path"] = "@videos/does-not-exist.mp4";
            File.WriteAllText(ProjectPath, p.ToJsonString());
        }
        Http = new(Media.Connection);
    }
    private static JsonObject Item(string id, string source, string take, int duration, int gap, int from, int to, double speed) => new()
    {
        ["id"] = id, ["block_id"] = source, ["take_id"] = take, ["title"] = id,
        ["duration_frames"] = duration, ["gap_before_frames"] = gap, ["source_in_frame"] = from, ["source_out_frame"] = to, ["speed"] = speed
    };
    internal async Task StartAsync()
    {
        await Media.StartAsync();
        await Http.StartAsync();
        Initial = File.ReadAllBytes(ProjectPath);
        InitialHistory = Media.Files.Files.HistoryFiles.ToDictionary(path => Path.GetFileName(path)!, File.ReadAllBytes);
    }
    internal void AssertBytes(byte[] expected, int revision, int histories)
    {
        Assert.Equal(expected, File.ReadAllBytes(ProjectPath));
        Assert.Equal(revision, ProjectFileCodec.Read(expected).Project.Revision);
        Assert.Equal(histories, Media.Files.Files.HistoryFiles.Length);
        foreach (var pair in InitialHistory)
            Assert.Equal(pair.Value, File.ReadAllBytes(Path.Combine(Media.Files.Files.HistoryDirectory, pair.Key)));
        Assert.False(File.Exists(Media.Files.Files.JournalPath));
    }
    internal static async Task<byte[]> RunAsync(string executable, IEnumerable<string> args, int limit, string? cwd = null)
    {
        var start = new ProcessStartInfo(executable) { UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true, RedirectStandardError = true };
        if (cwd is not null) start.WorkingDirectory = cwd;
        foreach (var arg in args) start.ArgumentList.Add(arg);
        using var scope = new CancellationTokenSource(TimeSpan.FromSeconds(45));
        using var child = Process.Start(start) ?? throw new IOException("Trim fixture child did not start.");
        var output = ReadAsync(child.StandardOutput.BaseStream, limit, scope.Token);
        var error = ReadAsync(child.StandardError.BaseStream, 64 * 1024, scope.Token);
        try
        {
            await Task.WhenAll(output, error, child.WaitForExitAsync(scope.Token)).WaitAsync(scope.Token);
            Assert.True(child.ExitCode == 0, $"Trim fixture child exit={child.ExitCode}: {System.Text.Encoding.UTF8.GetString(await error)}");
            return await output;
        }
        finally
        {
            if (!child.HasExited) child.Kill(entireProcessTree: true);
            await child.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(5));
            try { await Task.WhenAll(output, error).WaitAsync(TimeSpan.FromSeconds(5)); }
            catch (Exception e) when (e is IOException or OperationCanceledException) { }
        }
    }
    private static async Task<byte[]> ReadAsync(Stream stream, int limit, CancellationToken token)
    {
        using var bytes = new MemoryStream(); var chunk = new byte[8192];
        while (true)
        {
            var n = await stream.ReadAsync(chunk, token); if (n == 0) return bytes.ToArray();
            if (bytes.Length + n > limit) throw new InvalidDataException("Trim evidence child exceeded its output bound.");
            bytes.Write(chunk, 0, n);
        }
    }
    internal async Task<(byte[] Frame, float[] Audio, JsonNode Proof)> CpuAsync(string folder, int localFrame)
    {
        Directory.CreateDirectory(folder);
        var request = new
        {
            version = 1, profile = "final", project_id = "independent-trim-fixture", project_revision = 14,
            book = Media.Files.Files.Root, path_policy = new { read_roots = new[] { Media.Files.Files.Root }, write_roots = new[] { folder }, protected_roots = Array.Empty<string>() },
            fps = 24, frames = 96, seconds = 4, size = new[] { 480, 270 }, timeout_seconds = 35,
            encoding = new { video_codec = "libx264", pixel_format = "yuv420p", preset = "medium", crf = 18, audio_codec = "aac", audio_bitrate_kbps = 320 },
            clips = new[] { new { id = "clip-main", asset_id = "video", start_frame = 20, duration_frames = 64, source_in_frame = 14, source_out_frame = 94, speed = 1.25 } },
            assets = new Dictionary<string, object> { ["video"] = new { path = Media.VideoPath }, ["music"] = new { path = Media.MusicPath }, ["event"] = new { path = Media.EventPath } },
            clip_audio = true, music = new { asset_id = "music", start_frame = 0, duration_frames = 96, source_in_frame = 0, gain = .3, muted = false },
            sounds = new[] { new { asset_id = "event", start_frame = 44, duration_frames = 24, source_in_frame = 0, gain = .5, muted = false, stream = 0 } }
        };
        var requestPath = Path.Combine(folder, "Request.json");
        if (!File.Exists(requestPath))
        {
            await using (var file = new FileStream(requestPath, FileMode.CreateNew, FileAccess.Write)) await JsonSerializer.SerializeAsync(file, request);
            var source = new DirectoryInfo(AppContext.BaseDirectory);
            while (!File.Exists(Path.Combine(source.FullName, "TrueMoon.Tales.Studio.slnx"))) source = source.Parent!;
            await RunAsync(Environment.GetEnvironmentVariable("STUDIO_TEST_PYTHON")!, ["-s", "-B", Path.Combine(AppContext.BaseDirectory, "Assets", "BackendTrimCpu.py"), source.Parent!.FullName, folder], 64 * 1024);
        }
        var ffmpeg = Environment.GetEnvironmentVariable("STUDIO_TEST_FFMPEG")!;
        var mp4 = Path.Combine(folder, "Draft.mp4");
        var frame = await RunAsync(ffmpeg, ["-nostdin", "-v", "error", "-i", mp4, "-map", "0:v:0", "-an", "-vf", $"select=eq(n\\,{20 + localFrame})", "-frames:v", "1", "-fps_mode", "passthrough", "-pix_fmt", "bgra", "-f", "rawvideo", "pipe:1"], 480 * 270 * 4);
        var pcm = await RunAsync(ffmpeg, ["-nostdin", "-v", "error", "-i", mp4, "-map", "0:a:0", "-vn", "-af", "atrim=start_sample=40000:end_sample=168000,asetpts=PTS-STARTPTS", "-ar", "48000", "-ac", "2", "-f", "f32le", "pipe:1"], 128000 * 8);
        return (frame, MemoryMarshal.Cast<byte, float>(pcm).ToArray(), JsonNode.Parse(File.ReadAllText(Path.Combine(folder, "Acceptance.json")))!);
    }
    public async ValueTask DisposeAsync() { try { await Http.DisposeAsync(); } finally { await Media.DisposeAsync(); } }
}
