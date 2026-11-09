using TrueMoon.Tales.Studio.Models;

namespace TrueMoon.Tales.Studio.Editing;

// Uses the same absolute project frame grid and placement rules as sound.placed / CPU export.
internal static class AudioTimeline
{
    public const int SampleRate = 48000;
    public const int Channels = 2;
    public static long Sample(double frame, double fps) => checked((long)Math.Round(frame / fps * SampleRate, MidpointRounding.ToEven));

    public static AudioSlice[] Build(Clip selected, string videoAsset, double fps, Clip[] clips, Segment[] segments,
        bool native, AudioMusic? music, AudioEvent[]? sounds)
    {
        List<AudioSlice> result = [];
        var left = selected.StartFrame;
        var right = left + selected.DurationFrames;
        void Add(string asset, int stream, int start, int duration, double source, double rate, double gain,
            bool muted, bool optional = false, double? sourceEnd = null)
        {
            var begin = Math.Max(left, start);
            var end = Math.Min(right, start + duration);
            if (muted || gain == 0 || begin >= end) return;
            var sourceStart = source + (begin - start) * rate;
            var sourceLength = Math.Min((end - begin) * rate, (sourceEnd ?? double.MaxValue) - sourceStart);
            if (sourceLength <= 0) return;
            result.Add(new(asset, stream, sourceStart / fps, sourceLength / fps,
                checked((int)(Sample(begin, fps) - Sample(left, fps))), checked((int)(Sample(end, fps) - Sample(begin, fps))), rate, gain, optional));
        }
        if (native) Add(videoAsset, 0, left, selected.DurationFrames, selected.SourceInFrame, selected.Speed ?? 1,
            1, selected.MuteNativeAudio, true, selected.SourceEnd);
        if (music is not null) Add(music.AssetId, 0, music.StartFrame, music.DurationFrames, music.SourceInFrame, 1, music.Gain, music.Muted);
        foreach (var sound in sounds ?? [])
        {
            var owner = sound.ClipId is { } id ? clips.FirstOrDefault(clip => clip.Id == id) : null;
            var segment = sound.ClipId is null ? segments.FirstOrDefault(row => row.Id == sound.SegmentId) : null;
            if (owner is null && segment is null) continue;
            var duration = owner is null ? sound.DurationFrames : Math.Min(sound.DurationFrames, owner.DurationFrames - sound.OffsetFrames);
            Add(sound.AssetId, sound.Stream, (owner?.StartFrame ?? segment!.StartFrame) + sound.OffsetFrames,
                duration, sound.SourceInFrame, 1, sound.Gain, sound.Muted);
        }
        return result.ToArray();
    }
}
