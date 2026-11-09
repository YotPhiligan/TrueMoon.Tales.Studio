using System.Runtime.InteropServices;
using System.Text.Json;
using TrueMoon.Tales.Studio.Configuration;
using TrueMoon.Tales.Studio.Infrastructure;
using TrueMoon.Tales.Studio.Media;
using TrueMoon.Tales.Studio.Models;
using Xunit;

namespace TrueMoon.Tales.Studio.Tests.Integration;

public sealed class PlaybackQaFactAttribute : FactAttribute
{
    public PlaybackQaFactAttribute()
    {
        if (Environment.GetEnvironmentVariable("STUDIO_PLAYBACK_QA_ENDPOINT") is null)
            Skip = "Requires the isolated PlaybackQA fixture; no user project or device is opened by the normal suite";
    }
}

public sealed class PlaybackIntegrationTests
{
    [PlaybackQaFact]
    [Trait("Category", "PlaybackIntegration")]
    public async Task Ffmpeg_AudibleMixAndEveryFrame_MatchFixtureAndReleaseProcesses()
    {
        var endpoint = Environment.GetEnvironmentVariable("STUDIO_PLAYBACK_QA_ENDPOINT")!;
        var ffmpeg = Environment.GetEnvironmentVariable("STUDIO_PLAYBACK_QA_FFMPEG")!;
        var run = Path.GetFullPath(Environment.GetEnvironmentVariable("STUDIO_PLAYBACK_QA_RUN")!);
        if (!run.Contains(Path.Combine(".local", "qa-sessions"), StringComparison.OrdinalIgnoreCase)) throw new InvalidOperationException("QA folder required");
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(55));
        using var http = new HttpClient(new SocketsHttpHandler { UseProxy = false }) { BaseAddress = new(endpoint), Timeout = TimeSpan.FromSeconds(10) };
        var before = await http.GetByteArrayAsync("api/projects/argentis-qa", deadline.Token);
        using var api = new StudioHttpClient(new StudioOptions(new(endpoint), "argentis-qa", ffmpeg));
        var scene = await api.OpenAsync(null, deadline.Token);
        var take = Assert.Single(scene.Takes);
        var media = await api.MediaInfoAsync(take, deadline.Token);
        Assert.Equal((12, 144, 24d, 168d), (scene.Clip.StartFrame, scene.Clip.DurationFrames, scene.Clip.SourceInFrame, scene.Clip.SourceEnd));
        Assert.Equal(3, scene.Audio!.Length);
        var service = new FfmpegPreview(ffmpeg, api);
        var audio = await service.PrepareAsync(scene, deadline.Token);
        Assert.Equal(288000, audio.SampleFrames);
        Assert.Contains(audio.Peaks, value => value > .1f);
        Assert.All(audio.Samples, value => Assert.InRange(value, -1, 1));
        var pcm = MemoryMarshal.AsBytes(audio.Samples.AsSpan()).ToArray();
        await File.WriteAllBytesAsync(Path.Combine(run, "preview-audio.f32"), pcm, deadline.Token);
        var rows = new List<object>();
        var decoder = new FrameDecoder(ffmpeg);
        foreach (var (rate, frames) in new[] { (1d, 144), (.5, 288), (2d, 72) })
        {
            var check = scene with { Clip = scene.Clip with { Speed = rate, DurationFrames = frames } };
            using var sequence = service.Video(check, media, 0, deadline.Token);
            var received = 0;
            var sampled = new List<int>();
            while (!sequence.Complete)
            {
                if (sequence.TryRead(out var frame))
                {
                    Assert.Equal(received, frame!.LocalFrame);
                    var expected = 24 + (rate == .5 ? received / 2 : received * (int)rate);
                    Assert.Equal(expected, frame.NativeFrame);
                    if (received == 0 || received == 1 || received == frames / 2 || received == frames - 1)
                    {
                        var reference = await decoder.ReadAsync(api.Media(take.VideoAssetId), expected, 24, deadline.Token);
                        Assert.Equal(reference, frame.Pixels);
                        sampled.Add(expected);
                        if (rate == 1)
                            await File.WriteAllBytesAsync(Path.Combine(run, $"video-{received:000}.bgra"), frame.Pixels, deadline.Token);
                    }
                    received++;
                }
                else await Task.Delay(1, deadline.Token);
            }
            await sequence.Completion.WaitAsync(TimeSpan.FromSeconds(5), deadline.Token);
            Assert.Null(sequence.Error);
            Assert.Equal(frames, received);
            rows.Add(new { speed = rate, frames = received, checked_source_frames = sampled });
        }
        // Explicit cancellation while the bounded queue is full must drain the producer.
        var cancelled = service.Video(scene, media, 0, deadline.Token);
        await Task.Delay(150, deadline.Token);
        cancelled.Dispose();
        await cancelled.Completion.WaitAsync(TimeSpan.FromSeconds(5), deadline.Token);
        Assert.Null(cancelled.Error);
        string? warning;
        long played;
        using (var sound = service.Sound(audio, 0))
        {
            sound.Play();
            await Task.Delay(1000, deadline.Token);
            played = sound.PlayedSamples;
            warning = sound.Warning;
            Console.WriteLine($"Audio clock: {played} samples; warning={warning}; error={sound.Error}");
            Assert.Null(sound.Error);
            Assert.InRange(played, 1, 96000);
        }
        Assert.Equal(before, await http.GetByteArrayAsync("api/projects/argentis-qa", deadline.Token));
        var result = new { ok = true, revision = scene.Revision, fps = scene.Fps, audio_sample_frames = audio.SampleFrames,
            peaks = audio.Peaks, video = rows, audio_device = warning is null, audio_warning = warning,
            played_samples = played, project_unchanged = true, producer_cancelled_and_drained = true };
        await File.WriteAllTextAsync(Path.Combine(run, "playback-integration.json"), JsonSerializer.Serialize(result, new JsonSerializerOptions { WriteIndented = true }), deadline.Token);
    }
}
