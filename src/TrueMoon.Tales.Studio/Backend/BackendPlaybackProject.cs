using System.Text.Json;
using TrueMoon.Tales.Studio.Contracts.Projects;
using TrueMoon.Tales.Studio.Editing;
using TrueMoon.Tales.Studio.Models;

namespace TrueMoon.Tales.Studio.Backend;

internal static class BackendPlaybackProject
{
    private static readonly JsonSerializerOptions Json = new() { PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower };

    internal static SceneSnapshot Scene(ProjectContract project, string clipId)
    {
        if (string.IsNullOrWhiteSpace(clipId) || clipId.Length > 160 || !project.Montage.Clips.Any(row => row.Id == clipId))
            throw new InvalidDataException("Playback requires a saved montage clip.");
        var scene = BackendSceneMapping.Map(project, active: false, clipId);
        var clip = scene.Clip;
        if (!double.IsFinite(scene.Fps) || scene.Fps is <= 0 or > 240 || clip.DurationFrames < 1 ||
            clip.DurationFrames / scene.Fps > 600 || clip.StartFrame < 0 || (long)clip.StartFrame + clip.DurationFrames > int.MaxValue ||
            !double.IsFinite(clip.SourceInFrame) || clip.SourceInFrame < 0 || !double.IsFinite(clip.SourceEnd) ||
            !double.IsFinite(clip.Speed ?? 1) || (clip.Speed ?? 1) is < .25 or > 4)
            throw new InvalidDataException("Playback clip exceeds its duration or timing limits.");
        var properties = project.Montage.Properties;
        var native = properties.TryGetValue("clip_audio", out var enabled) && enabled.GetBoolean();
        var music = properties.TryGetValue("music", out var musicValue) && musicValue.ValueKind != JsonValueKind.Null
            ? musicValue.Deserialize<AudioMusic>(Json) : null;
        var events = properties.TryGetValue("sound_events", out var eventsValue) && eventsValue.ValueKind != JsonValueKind.Null
            ? eventsValue.Deserialize<AudioEvent[]>(Json) : [];
        if (events is null || events.Length > 64) throw new InvalidDataException("Too many audio events.");
        static void Placement(int start, int duration, double source, double gain)
        {
            if (start < 0 || duration < 1 || (long)start + duration > int.MaxValue ||
                !double.IsFinite(source) || source < 0 || !double.IsFinite(gain) || gain is < 0 or > 1)
                throw new InvalidDataException("Invalid saved audio placement.");
        }
        if (music is not null) Placement(music.StartFrame, music.DurationFrames, music.SourceInFrame, music.Gain);
        foreach (var sound in events)
        {
            if (sound is null || sound.Stream is < 0 or > 63) throw new InvalidDataException("Invalid saved sound event.");
            Placement(sound.OffsetFrames, sound.DurationFrames, sound.SourceInFrame, sound.Gain);
        }
        var clips = project.Montage.Clips.Select(row => new Clip(row.Id, row.WindowId, row.SourceId, row.TakeId, null,
            row.StartFrame, row.DurationFrames, row.SourceInFrame, row.SourceOutFrame, row.Speed, row.AnchorFrame, row.MuteNativeAudio)).ToArray();
        var segments = project.Montage.Windows.Select(row => new Segment(row.Id, "", row.StartFrame, row.DurationFrames)).ToArray();
        var video = scene.Takes.Single(row => row.Id == clip.TakeId).VideoAssetId;
        var slices = AudioTimeline.Build(clip, video, project.Fps, clips, segments, native, music, events);
        var total = AudioTimeline.Sample((double)clip.StartFrame + clip.DurationFrames, project.Fps) - AudioTimeline.Sample(clip.StartFrame, project.Fps);
        foreach (var slice in slices)
            if (string.IsNullOrWhiteSpace(slice.AssetId) || slice.AssetId.Length > 160 || slice.OutputSample < 0 || slice.Samples < 1 ||
                (long)slice.OutputSample + slice.Samples > total || !double.IsFinite(slice.SourceDuration) || slice.SourceDuration <= 0 ||
                !double.IsFinite(slice.SourceSeconds) || slice.SourceSeconds < 0)
                throw new InvalidDataException("Invalid projected audio slice.");
        return scene with { Audio = slices };
    }
}
