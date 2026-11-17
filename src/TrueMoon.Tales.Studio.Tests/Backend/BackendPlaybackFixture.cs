using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text.Json.Nodes;
using TrueMoon;
using TrueMoon.Dependencies;
using TrueMoon.Tales.Studio.Backend;
using TrueMoon.Tales.Studio.Media;
using TrueMoon.Tales.Studio.Models;
using TrueMoon.Tales.Studio.Persistence;
using Xunit;

namespace TrueMoon.Tales.Studio.Tests.Backend;

// All media is generated inside this fixture. Only the physical sound device is substituted.
internal sealed class BackendPlaybackFixture : IAsyncDisposable
{
    internal BackendWriteFixture Files { get; } = new();
    internal IApp App { get; }
    internal BackendConnection Connection => App.Services.Resolve<BackendConnection>()!;
    internal OpenedBackendProject Baseline { get; private set; } = null!;
    internal DateTime BackendStartedAt { get; private set; }
    internal string VideoPath { get; }
    internal string MusicPath { get; }
    internal string EventPath { get; }
    internal readonly List<ControlledPlaybackSound> Sounds = [];
    internal readonly List<PreviewAudio> PreparedSounds = [];
    internal readonly List<int> SoundStarts = [];
    internal readonly List<IPreviewVideo> Videos = [];
    internal BackendPreviewMedia Playback => _playback ?? throw new InvalidOperationException("Resolve the preview adapter first.");
    private BackendPreviewMedia? _playback;
    private readonly string _ffmpeg = Environment.GetEnvironmentVariable("STUDIO_TEST_FFMPEG")!;
    private byte[]? _project;
    private Dictionary<string, byte[]>? _history;
    private Process? _backend;
    private readonly string? _videoFilter;

    internal BackendPlaybackFixture(bool muteNative = false, bool muteMusic = false, bool muteEvent = false, string? videoFilter = null)
    {
        _videoFilter = videoFilter;
        var media = Path.Combine(Files.Files.Root, "media");
        Directory.CreateDirectory(media);
        VideoPath = Path.Combine(media, "Непрерывный клип.mp4");
        MusicPath = Path.Combine(media, "Музыка.wav");
        EventPath = Path.Combine(media, "Реплика.wav");
        var config = JsonNode.Parse(File.ReadAllText(Files.Base.ConfigPath))!;
        config["projects_root"] = Files.Files.ProjectDirectory;
        config["mounts"] = new JsonObject { ["videos"] = media };
        config["ffmpeg_executable"] = _ffmpeg;
        config["ffprobe_executable"] = Environment.GetEnvironmentVariable("STUDIO_TEST_FFPROBE");
        config["path_aliases"] = new JsonArray(new JsonObject { ["from"] = "Old/Media", ["to"] = "@videos" });
        File.WriteAllText(Files.Base.ConfigPath, config.ToJsonString());
        var project = JsonNode.Parse(File.ReadAllText(Files.Files.ProjectPath))!;
        project["assets"]!["video-a"]!["path"] = "Old/Media/Непрерывный клип.mp4";
        project["assets"]!["video-b"]!["path"] = "@videos/Непрерывный клип.mp4";
        project["assets"]!["music"] = new JsonObject { ["path"] = "@videos/Музыка.wav" };
        project["assets"]!["event"] = new JsonObject { ["path"] = "@videos/Реплика.wav" };
        var timeline = project["timeline"]!;
        var clip = timeline["clips"]![0]!;
        clip["source_in_frame"] = 4;
        clip["source_out_frame"] = 124;
        clip["duration_frames"] = 96;
        clip["speed"] = 1.25;
        clip["mute_native_audio"] = muteNative;
        timeline["clip_audio"] = true;
        timeline["music"] = new JsonObject
        {
            ["asset_id"] = "music", ["start_frame"] = 0, ["duration_frames"] = 192,
            ["source_in_frame"] = 0, ["gain"] = .3, ["muted"] = muteMusic
        };
        timeline["sound_events"] = new JsonArray(new JsonObject
        {
            ["id"] = "event-main", ["asset_id"] = "event", ["segment_id"] = "segment-main", ["clip_id"] = "clip-main",
            ["offset_frames"] = 24, ["duration_frames"] = 24, ["source_in_frame"] = 0,
            ["stream"] = 0, ["gain"] = .5, ["muted"] = muteEvent
        });
        File.WriteAllText(Files.Files.ProjectPath, project.ToJsonString());
        App = TrueMoon.App.Builder(_ => { }).Setup(context => context.UseProjectBackend(Files.Options, false)
            .UseBackendScene().Services(r => r.Singleton<IPreviewMedia>(resolver =>
            {
                _playback = new BackendPreviewMedia(resolver.Resolve<BackendConnection>()!, Sound);
                return new TrackingPreviewMedia(_playback, Videos);
            }))).Build();
    }

    private IPreviewSound Sound(PreviewAudio audio, int localFrame)
    {
        var sound = new ControlledPlaybackSound();
        PreparedSounds.Add(audio); SoundStarts.Add(localFrame); Sounds.Add(sound);
        return sound;
    }

    internal async Task StartAsync()
    {
        await RunAsync(["-nostdin", "-v", "error", "-f", "lavfi", "-i", "testsrc2=size=320x240:rate=30:duration=8",
            "-f", "lavfi", "-i", "sine=frequency=220:sample_rate=48000:duration=8", "-map", "0:v:0", "-map", "1:a:0",
            "-frames:v", "240", "-c:v", "libx264", "-preset", "ultrafast", "-crf", "18", "-pix_fmt", "yuv420p",
            "-c:a", "alac", "-ac", "2", "-threads", "1", ..(_videoFilter is { } filter ? new[] { "-vf", filter } : []), "-n", VideoPath], 4096);
        await ToneAsync(MusicPath, 440);
        await ToneAsync(EventPath, 880);
        var store = Files.Files.Store();
        await store.MigrateAsync(await store.OpenAsync(), 11, "playback-fixture");
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(25));
        await App.StartAsync(deadline.Token);
        Baseline = await Connection.OpenAsync(deadline.Token);
        _backend = Process.GetProcessById(Baseline.Identity.ProcessId);
        BackendStartedAt = _backend.StartTime;
        Assert.Equal(Environment.ProcessId, Baseline.Identity.ParentProcessId);
        Assert.NotEqual(Environment.ProcessId, _backend.Id);
        _project = File.ReadAllBytes(Files.Files.ProjectPath);
        _history = Files.Files.HistoryFiles.ToDictionary(path => Path.GetFileName(path)!, File.ReadAllBytes);
        Assert.Equal(12, Baseline.Project.Revision); Assert.Single(_history);
    }

    private Task<byte[]> ToneAsync(string path, int frequency) => RunAsync(["-nostdin", "-v", "error", "-f", "lavfi", "-i",
        $"sine=frequency={frequency}:sample_rate=48000:duration=8", "-ac", "2", "-c:a", "pcm_f32le", "-threads", "1", "-n", path], 4096);

    internal Task<byte[]> ReferenceFrameAsync(int nativeFrame) => RunAsync(["-nostdin", "-v", "error", "-threads", "1", "-i", VideoPath,
        "-map", "0:v:0", "-an", "-sn", "-dn", "-vf",
        $"select=eq(n\\,{nativeFrame}),scale=480:270:force_original_aspect_ratio=decrease,pad=480:270:(ow-iw)/2:(oh-ih)/2",
        "-frames:v", "1", "-fps_mode", "passthrough", "-threads", "1", "-pix_fmt", "bgra", "-f", "rawvideo", "pipe:1"], 480 * 270 * 4);

    // Independent, manually specified sound placement: clip begins at project sample24000,
    // event starts one second into its four-second extent. No production AudioTimeline is used.
    internal async Task<float[]> ReferenceAudioAsync(bool native = true, bool music = true, bool sound = true)
    {
        var samples = new float[192000 * 2];
        if (native) Add(samples, await AudioAsync(VideoPath,
            "atrim=start=0.166666666667:duration=5,asetpts=PTS-STARTPTS,atempo=1.250000000000,aresample=48000,aformat=sample_fmts=flt:channel_layouts=stereo,apad,atrim=end_sample=192000", 192000), 0, 1);
        if (music) Add(samples, await AudioAsync(MusicPath,
            "atrim=start=0.500000000000:duration=4,asetpts=PTS-STARTPTS,aresample=48000,aformat=sample_fmts=flt:channel_layouts=stereo,apad,atrim=end_sample=192000", 192000), 0, .3f);
        if (sound) Add(samples, await AudioAsync(EventPath,
            "atrim=start=0:duration=1,asetpts=PTS-STARTPTS,aresample=48000,aformat=sample_fmts=flt:channel_layouts=stereo,apad,atrim=end_sample=48000", 48000), 48000 * 2, .5f);
        for (var i = 0; i < samples.Length; i++) samples[i] = Math.Clamp(samples[i], -1, 1);
        return samples;
    }

    private Task<byte[]> AudioAsync(string source, string filter, int count) => RunAsync(["-nostdin", "-v", "error", "-threads", "1", "-i", source,
        "-map", "0:a:0", "-vn", "-af", filter, "-ar", "48000", "-ac", "2", "-threads", "1", "-f", "f32le", "pipe:1"], count * 2 * sizeof(float));

    private static void Add(float[] mix, byte[] bytes, int offset, float gain)
    {
        var values = MemoryMarshal.Cast<byte, float>(bytes);
        for (var i = 0; i < values.Length; i++) mix[offset + i] += values[i] * gain;
    }

    internal void AssertUnchanged(bool externallyAppended = false)
    {
        Assert.NotNull(_project); Assert.NotNull(_history);
        var expected = externallyAppended ? _project.Concat(new[] { (byte)' ' }).ToArray() : _project;
        Assert.Equal(expected, File.ReadAllBytes(Files.Files.ProjectPath));
        Assert.Equal(12, ProjectFileCodec.Read(expected).Project.Revision);
        var actual = Files.Files.HistoryFiles.ToDictionary(path => Path.GetFileName(path)!, File.ReadAllBytes);
        Assert.Equal(_history.Keys.Order(), actual.Keys.Order());
        foreach (var pair in _history) Assert.Equal(pair.Value, actual[pair.Key]);
        Assert.False(File.Exists(Files.Files.JournalPath));
    }

    internal void AssertVideoReleased()
    {
        // A live backend file lease or decoder would deny this exclusive handle.
        using var exclusive = new FileStream(VideoPath, FileMode.Open, FileAccess.Read, FileShare.None);
        Assert.True(exclusive.Length > 0);
    }

    internal static string Hash(byte[] value) => Convert.ToHexString(SHA256.HashData(value));

    private async Task<byte[]> RunAsync(IEnumerable<string> arguments, int limit)
    {
        var start = new ProcessStartInfo(_ffmpeg)
        { UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true, RedirectStandardError = true };
        foreach (var argument in arguments) start.ArgumentList.Add(argument);
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(25));
        using var child = Process.Start(start) ?? throw new IOException("Fixture FFmpeg did not start.");
        var output = LimitedAsync(child.StandardOutput.BaseStream, limit, timeout.Token);
        var error = LimitedAsync(child.StandardError.BaseStream, 16 * 1024, timeout.Token);
        try
        {
            await Task.WhenAll(output, error, child.WaitForExitAsync(timeout.Token)).WaitAsync(timeout.Token);
            Assert.True(child.ExitCode == 0, $"Fixture FFmpeg exit={child.ExitCode}: {System.Text.Encoding.UTF8.GetString(await error)}");
            return await output;
        }
        finally
        {
            if (!child.HasExited) child.Kill(entireProcessTree: true);
            await child.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(5));
            try { await Task.WhenAll(output, error).WaitAsync(TimeSpan.FromSeconds(5)); }
            catch (Exception failure) when (failure is IOException or OperationCanceledException) { }
        }
    }

    private static async Task<byte[]> LimitedAsync(Stream source, int limit, CancellationToken token)
    {
        using var bytes = new MemoryStream();
        var buffer = new byte[8192];
        while (true)
        {
            var count = await source.ReadAsync(buffer, token);
            if (count == 0) return bytes.ToArray();
            if (bytes.Length + count > limit) throw new InvalidDataException("Fixture output exceeded its limit.");
            bytes.Write(buffer, 0, count);
        }
    }

    public async ValueTask DisposeAsync()
    {
        try { await App.StopAsync(); }
        finally
        {
            try
            {
                await App.DisposeAsync();
                if (_backend is { } backend)
                {
                    await backend.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(8));
                    Assert.True(backend.HasExited);
                    AssertVideoReleased();
                }
            }
            finally { _backend?.Dispose(); Files.Dispose(); }
        }
    }

    private sealed class TrackingPreviewMedia(BackendPreviewMedia inner, List<IPreviewVideo> videos) : IPreviewMedia, IDisposable, IAsyncDisposable
    {
        public Task<PreviewAudio> PrepareAsync(SceneSnapshot scene, CancellationToken token) => inner.PrepareAsync(scene, token);
        public IPreviewSound Sound(PreviewAudio audio, int localFrame) => inner.Sound(audio, localFrame);
        public IPreviewVideo Video(SceneSnapshot scene, MediaInfo media, int localFrame, CancellationToken token)
        {
            var video = inner.Video(scene, media, localFrame, token);
            videos.Add(video); return video;
        }
        public void Dispose() => inner.Dispose();
        public ValueTask DisposeAsync() => inner.DisposeAsync();
    }
}

internal sealed class ControlledPlaybackSound : IPreviewSound
{
    public long PlayedSamples { get; set; }
    public string? Warning => null;
    public Exception? Error => null;
    internal int Plays { get; private set; }
    internal bool Disposed { get; private set; }
    public void Play() { Assert.False(Disposed); Plays++; }
    public void Dispose() => Disposed = true;
}
