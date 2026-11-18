using System.Diagnostics;
using System.Security.Cryptography;
using System.Text.Json;
using TrueMoon.Tales.Studio.Backend;
using TrueMoon.Tales.Studio.Contracts.Projects;
using Xunit;

namespace TrueMoon.Tales.Studio.Tests.Backend;

public sealed class BackendExportPlanTests
{
    private const string Fingerprint = "fedcba9876543210fedcba9876543210fedcba9876543210fedcba9876543210";

    [PythonWriterFact]
    public async Task ExportPlan_PinsExactInputsAndProjectsSavedClipsAndRelativeSounds()
    {
        using var fixture = new PlanFixture();
        var plan = await fixture.CreateAsync(fixture.Project, "draft");
        using var document = JsonDocument.Parse(plan.Request);
        var request = document.RootElement;
        Assert.Equal(94, plan.Frames);
        Assert.Equal(24, plan.Fps);
        Assert.Equal(94, request.GetProperty("frames").GetInt32());
        Assert.Equal(24, request.GetProperty("fps").GetInt32());
        Assert.Equal(1, request.GetProperty("version").GetInt32());
        Assert.True(request.GetProperty("backend_mode").GetBoolean());
        Assert.Equal(73, request.GetProperty("project_revision").GetInt64());
        Assert.Equal(Fingerprint.ToUpperInvariant(), request.GetProperty("project_fingerprint").GetString());
        Assert.Equal(new[] { 896, 516 }, request.GetProperty("size").Deserialize<int[]>());
        Assert.Equal(25, request.GetProperty("encoding").GetProperty("crf").GetInt32());
        Assert.True(request.GetProperty("clip_audio").GetBoolean());

        var clips = request.GetProperty("clips");
        Assert.Equal(2, clips.GetArrayLength());
        Assert.Equal("a", clips[0].GetProperty("id").GetString());
        Assert.Equal(12, clips[0].GetProperty("start_frame").GetInt32());
        Assert.Equal(24, clips[0].GetProperty("source_out_frame").GetDouble());
        Assert.Equal("b", clips[1].GetProperty("id").GetString());
        Assert.Equal(46, clips[1].GetProperty("start_frame").GetInt32());
        Assert.Equal(2, clips[1].GetProperty("speed").GetDouble());
        Assert.Equal(6, clips[1].GetProperty("source_in_frame").GetDouble());
        Assert.Equal(30, clips[1].GetProperty("source_out_frame").GetDouble());
        Assert.True(clips[1].GetProperty("mute_native_audio").GetBoolean());

        var music = request.GetProperty("music");
        Assert.Equal(90, music.GetProperty("duration_frames").GetInt32());
        Assert.Equal(.3, music.GetProperty("gain").GetDouble());
        var sounds = request.GetProperty("sounds");
        Assert.Equal(2, sounds.GetArrayLength());
        Assert.Equal(32, sounds[0].GetProperty("start_frame").GetInt32());
        Assert.Equal(4, sounds[0].GetProperty("duration_frames").GetInt32());
        Assert.Equal(1, sounds[0].GetProperty("stream").GetInt32());
        Assert.Equal(86, sounds[1].GetProperty("start_frame").GetInt32());
        Assert.Equal(8, sounds[1].GetProperty("duration_frames").GetInt32());

        var assets = request.GetProperty("assets");
        var videoHash = Convert.ToHexStringLower(SHA256.HashData(new byte[] { 1, 2, 3 }));
        foreach (var key in new[] { "video-a", "video-b" })
        {
            Assert.Equal(videoHash, assets.GetProperty(key).GetProperty("expected_sha256").GetString());
            Assert.Equal(fixture.Video, assets.GetProperty(key).GetProperty("path").GetString());
            Assert.Equal("mov", assets.GetProperty(key).GetProperty("format").GetString());
        }
        Assert.Equal(Convert.ToHexStringLower(SHA256.HashData(new byte[] { 4, 5, 6 })),
            assets.GetProperty("sound").GetProperty("expected_sha256").GetString());
        Assert.Equal("wav", assets.GetProperty("sound").GetProperty("format").GetString());
        var policy = request.GetProperty("path_policy");
        Assert.Equal(new[] { fixture.Video, fixture.Audio }, policy.GetProperty("read_roots").Deserialize<string[]>());
        Assert.Equal(new[] { fixture.Job }, policy.GetProperty("write_roots").Deserialize<string[]>());
        Assert.Equal(1, fixture.VideoProbes);
        Assert.Equal(2, fixture.AudioProbes);
        Assert.False(Directory.Exists(fixture.Job));
        Assert.False(Directory.Exists(fixture.LegacyRoot));
        Assert.Equal(new byte[] { 1, 2, 3 }, File.ReadAllBytes(fixture.Video));
        Assert.Equal(new byte[] { 4, 5, 6 }, File.ReadAllBytes(fixture.Audio));
    }

    [Theory]
    [InlineData("overlap")]
    [InlineData("anchor")]
    [InlineData("duration")]
    [InlineData("placeholder")]
    [InlineData("fractional-fps")]
    [InlineData("size")]
    [InlineData("titles")]
    [InlineData("transitions")]
    [InlineData("timeline-limit")]
    [InlineData("audio-owner")]
    public async Task ExportPlan_RejectsInvalidSavedLayoutBeforeMediaIo(string invalid)
    {
        using var fixture = new PlanFixture();
        var project = fixture.Project;
        var montage = project.Montage;
        var clips = montage.Clips;
        var rows = new Dictionary<string, JsonElement>(montage.Properties);
        switch (invalid)
        {
            case "overlap": clips[0] = clips[0] with { StartFrame = 23 }; break;
            case "anchor": clips[0] = clips[0] with { AnchorFrame = 47 }; break;
            case "duration": clips[0] = clips[0] with { DurationFrames = 13 }; break;
            case "placeholder": clips[0] = clips[0] with { TakeId = null, SourceOutFrame = null, SourceInFrame = 0, Speed = 1 }; break;
            case "fractional-fps": project = project with { Fps = 24000d / 1001 }; break;
            case "size": project = project with { Properties = new Dictionary<string, JsonElement>(project.Properties)
                { ["output_size"] = JsonSerializer.SerializeToElement(new[] { 1280, 4097 }) } }; break;
            case "titles": rows["titles"] = JsonSerializer.SerializeToElement(new[] { new { text = "Титр" } }); break;
            case "transitions": rows["transitions"] = JsonSerializer.SerializeToElement(new[] { new { left = "a", right = "b" } }); break;
            case "timeline-limit": montage.Windows[0] = montage.Windows[0] with { DurationFrames = 14401 }; break;
            case "audio-owner": rows["sound_events"] = JsonSerializer.SerializeToElement(new[]
                { new { asset_id = "sound", segment_id = "missing", offset_frames = 2, duration_frames = 4, source_in_frame = 0, stream = 0, gain = 1, muted = false } }); break;
        }
        project = project with { Montage = montage with { Properties = rows } };
        await Assert.ThrowsAsync<InvalidDataException>(() => fixture.CreateAsync(project, "final"));
        Assert.Equal(0, fixture.VideoProbes + fixture.AudioProbes);
        Assert.False(Directory.Exists(fixture.Job));
    }

    [Theory]
    [InlineData("https://example.invalid/remote.mp4")]
    [InlineData("Old/Leaf/../../elsewhere.mp4")]
    public async Task ExportPlan_DeniesUntrustedReferencesBeforeAProbe(string reference)
    {
        using var fixture = new PlanFixture(reference);
        await Assert.ThrowsAsync<UnauthorizedAccessException>(() => fixture.CreateAsync(fixture.Project, "project"));
        Assert.Equal(0, fixture.VideoProbes + fixture.AudioProbes);
        Assert.False(Directory.Exists(fixture.Job));
    }

    [PythonTrimTheory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task ExportPlan_RejectsClipAndAudioRangesBeyondProbedSource(bool video)
    {
        using var fixture = new PlanFixture(videoFrames: video ? 12 : 120, audioSeconds: video ? "10" : "0.5");
        await Assert.ThrowsAsync<InvalidDataException>(() => fixture.CreateAsync(fixture.Project, "final"));
        Assert.Equal(1, fixture.VideoProbes);
        Assert.Equal(video ? 0 : 1, fixture.AudioProbes);
        Assert.False(Directory.Exists(fixture.Job));
    }

    private sealed class PlanFixture : IDisposable
    {
        private readonly BackendFixture _files = new();
        private readonly BackendMediaResolver _resolver;
        private readonly BackendMediaDecoder _decoder;
        internal ProjectContract Project { get; }
        internal string Video { get; }
        internal string Audio { get; }
        internal string Job { get; }
        internal string LegacyRoot { get; }
        internal int VideoProbes { get; private set; }
        internal int AudioProbes { get; private set; }

        internal PlanFixture(string reference = "Old/Leaf/Исходник.mp4", int videoFrames = 120, string audioSeconds = "10")
        {
            var media = Path.Combine(_files.Files.Root, "media");
            LegacyRoot = Path.Combine(_files.Files.Root, "legacy");
            Job = Path.Combine(_files.Files.Root, "exports", "job");
            Directory.CreateDirectory(media);
            Video = Path.Combine(media, "Исходник.mp4");
            Audio = Path.Combine(media, "Фон.wav");
            File.WriteAllBytes(Video, [1, 2, 3]);
            File.WriteAllBytes(Audio, [4, 5, 6]);
            var config = new
            {
                version = 1, projects_root = _files.Files.ProjectDirectory, legacy_root = LegacyRoot,
                legacy_read_enabled = false, mounts = new { media },
                path_aliases = new[] { new { from = "Old/Leaf", to = "@media" } }
            };
            File.WriteAllText(_files.ConfigPath, JsonSerializer.Serialize(config));
            _resolver = new(_files.Options);
            _decoder = new("unused-ffmpeg", "controlled-ffprobe", requested => StartProbe(requested, videoFrames, audioSeconds));
            Project = new()
            {
                Id = "project-a", Name = "Проверка экспорта", Revision = 73, Fps = 24,
                Sources =
                [
                    new() { Id = "source-a", Takes = [new() { Id = "take-a", VideoAssetId = "video-a" }] },
                    new() { Id = "source-b", Takes = [new() { Id = "take-b", VideoAssetId = "video-b" }] }
                ],
                Properties = new Dictionary<string, JsonElement>
                {
                    ["output_size"] = JsonSerializer.SerializeToElement(new[] { 1281, 737 }),
                    ["assets"] = JsonSerializer.SerializeToElement(new Dictionary<string, object>
                    {
                        ["video-a"] = new { path = reference, kind = "video" },
                        ["video-b"] = new { path = "@media/Исходник.mp4", kind = "video" },
                        ["sound"] = new { path = "@media/Фон.wav", kind = "audio" }
                    })
                },
                Montage = new()
                {
                    Windows = [new() { Id = "window", StartFrame = 10, DurationFrames = 60 }],
                    Clips =
                    [
                        new() { Id = "b", SourceId = "source-b", TakeId = "take-b", WindowId = "window", StartFrame = 46,
                            DurationFrames = 12, SourceInFrame = 6, SourceOutFrame = 30, Speed = 2, MuteNativeAudio = true },
                        new() { Id = "a", SourceId = "source-a", TakeId = "take-a", WindowId = "window", StartFrame = 12,
                            DurationFrames = 24 }
                    ],
                    Properties = new Dictionary<string, JsonElement>
                    {
                        ["clip_audio"] = JsonSerializer.SerializeToElement(true),
                        ["music"] = JsonSerializer.SerializeToElement(new
                        {
                            asset_id = "sound", start_frame = 0, duration_frames = 90, source_in_frame = 0, gain = .3, muted = false
                        }),
                        ["sound_events"] = JsonSerializer.SerializeToElement(new object[]
                        {
                            new { asset_id = "sound", clip_id = "a", offset_frames = 20, duration_frames = 16,
                                source_in_frame = 4, stream = 1, gain = .5, muted = false },
                            new { asset_id = "sound", segment_id = "window", offset_frames = 76, duration_frames = 8,
                                source_in_frame = 0, stream = 1, gain = .5, muted = false },
                            new { asset_id = "sound", clip_id = "a", offset_frames = 24, duration_frames = 4,
                                source_in_frame = 0, stream = 0, gain = .5, muted = false }
                        })
                    }
                }
            };
        }

        internal Task<BackendExportPlan> CreateAsync(ProjectContract project, string profile) =>
            BackendExportPlan.CreateAsync(project, Fingerprint, profile, Job, _resolver, _decoder, CancellationToken.None);

        private Process StartProbe(ProcessStartInfo request, int frames, string seconds)
        {
            var isVideo = request.ArgumentList.Contains("v:0");
            string output;
            if (isVideo)
            {
                VideoProbes++;
                output = JsonSerializer.Serialize(new { streams = new[]
                    { new { codec_type = "video", width = 320, height = 240, avg_frame_rate = "30/1", r_frame_rate = "30/1",
                        nb_frames = frames.ToString(System.Globalization.CultureInfo.InvariantCulture) } } });
            }
            else
            {
                AudioProbes++;
                output = JsonSerializer.Serialize(new { streams = new[] { new { codec_type = "audio", duration = seconds } } });
            }
            var start = new ProcessStartInfo(Environment.GetEnvironmentVariable("STUDIO_TEST_PYTHON")!)
            { UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true, RedirectStandardError = true };
            foreach (var arg in new[] { "-s", "-B", "-c", "import sys; print(sys.argv[1])", output }) start.ArgumentList.Add(arg);
            return Process.Start(start) ?? throw new InvalidOperationException("Controlled probe did not start.");
        }

        public void Dispose() => _files.Dispose();
    }
}
