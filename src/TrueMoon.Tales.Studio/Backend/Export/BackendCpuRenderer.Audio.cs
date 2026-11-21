namespace TrueMoon.Tales.Studio.Backend.Export;

internal sealed partial class BackendCpuRenderer
{
    private async Task RenderNativeAudioAsync(IReadOnlyList<Part> parts)
    {
        var concat = new List<string>();
        var audio = new Dictionary<string, bool>();
        foreach (var part in parts)
        {
            Progress("Звук клипов", 75 + 15d * concat.Count / parts.Count, force: true);
            var name = $"Audio-{concat.Count:D4}.wav";
            concat.Add($"file '{name}'");
            var start = Sample(part.Start);
            var samples = Sample(part.End) - start;
            List<string> args = ["-threads", "2"];
            var clip = part.Clip;
            var path = clip is { MuteNativeAudio: false } ? AssetPath(clip.AssetId) : null;
            if (path is not null && !audio.ContainsKey(path))
                audio.Add(path, await _media.HasAudioAsync(path, 0, token).ConfigureAwait(false));
            if (path is not null && audio[path])
            {
                var offset = start - Sample(clip!.StartFrame);
                var filters = new List<string>
                {
                    $"atrim=start={Number(clip.SourceInFrame / request.Fps)}:end={Number(clip.SourceOutFrame / request.Fps)}",
                    "asetpts=PTS-STARTPTS"
                };
                var rate = clip.Speed;
                while (rate < .5) { filters.Add("atempo=0.5"); rate /= .5; }
                while (rate > 2) { filters.Add("atempo=2"); rate /= 2; }
                if (rate != 1) filters.Add($"atempo={Number(rate)}");
                filters.AddRange(["aresample=48000", "apad", offset == 0 ? $"atrim=end_sample={samples}" :
                    $"atrim=start_sample={offset}:end_sample={offset + samples},asetpts=PTS-STARTPTS"]);
                args.AddRange(BackendMediaDecoder.Input(path, audio: true));
                args.AddRange(["-map", "0:a:0", "-vn", "-af", string.Join(',', filters)]);
            }
            else
                args.AddRange(["-f", "lavfi", "-i", "anullsrc=r=48000:cl=stereo", "-af", $"atrim=end_sample={samples}"]);
            args.AddRange(["-ar", "48000", "-ac", "2", "-c:a", "pcm_s16le", "-n", JobFile(name, allowMissing: true)]);
            await RunFfmpegAsync(args).ConfigureAwait(false);
        }
        await WriteTextAsync("Audio.concat.txt", string.Join('\n', concat) + "\n").ConfigureAwait(false);
    }

    private async Task MuxAsync()
    {
        List<string> args = ["-threads", "2"];
        args.AddRange(BackendMediaDecoder.Input(JobFile("Video.mp4")));
        List<string> filters = [];
        List<string> labels = [];
        var index = 1;
        if (request.ClipAudio)
        {
            args.AddRange(["-protocol_whitelist", "file,pipe", "-f", "concat", "-safe", "1", "-i", JobFile("Audio.concat.txt")]);
            filters.Add($"[{index++}:a]apad,atrim=end_sample={Samples},asetpts=PTS-STARTPTS[native]");
            labels.Add("[native]");
        }
        if (request.Music is { Muted: false, Gain: > 0 } music)
            AddTrack(music.AssetId, music.StartFrame, music.SourceInFrame, music.DurationFrames, music.Gain, 0);
        foreach (var sound in request.Sounds)
            if (!sound.Muted && sound.Gain > 0)
                AddTrack(sound.AssetId, sound.StartFrame, sound.SourceInFrame, sound.DurationFrames, sound.Gain, sound.Stream);
        filters.Add(labels.Count switch
        {
            0 => $"anullsrc=r=48000:cl=stereo,atrim=end_sample={Samples}[mixed]",
            1 => labels[0] + "anull[mixed]",
            _ => string.Concat(labels) + $"amix=inputs={labels.Count}:duration=longest:normalize=0[mixed]"
        });
        filters.Add("[mixed]asetpts=N/SR/TB[sound]");
        var filter = await WriteTextAsync("Audio.filter.txt", string.Join(';', filters)).ConfigureAwait(false);
        args.AddRange(["-filter_complex_threads", "1", "-/filter_complex", filter, "-map", "0:v:0", "-map", "[sound]",
            "-c:v", "copy", "-c:a", request.Encoding.AudioCodec, "-b:a", $"{request.Encoding.AudioBitrateKbps}k",
            "-ar", "48000", "-ac", "2", "-t", Number(request.Frames / (double)request.Fps),
            "-movflags", "+faststart", "-n", JobFile("Draft.mp4", allowMissing: true)]);
        await RunFfmpegAsync(args).ConfigureAwait(false);

        void AddTrack(string asset, int start, double source, int length, double gain, int stream)
        {
            args.Add("-threads");
            args.Add("2");
            args.AddRange(BackendMediaDecoder.Input(AssetPath(asset), audio: true));
            var offset = Sample(start) - Sample(request.StartFrame);
            var crop = offset < 0 ? $"atrim=start_sample={-offset},asetpts=PTS-STARTPTS," : "";
            var label = $"[track{index}]";
            filters.Add($"[{index++}:a:{stream}]atrim=start={Number(source / request.Fps)}:duration={Number(length / (double)request.Fps)}," +
                $"asetpts=PTS-STARTPTS,aresample=48000,aformat=channel_layouts=stereo,volume={Number(gain)}," +
                $"{crop}adelay={Math.Max(0, offset)}S:all=1,apad,atrim=end_sample={Samples}{label}");
            labels.Add(label);
        }
    }

    private async Task<string> WriteTextAsync(string name, string text)
    {
        var path = JobFile(name, allowMissing: true);
        await using var file = new FileStream(path, FileMode.CreateNew, FileAccess.Write, FileShare.None, 4096, FileOptions.Asynchronous);
        await using var writer = new StreamWriter(file);
        await writer.WriteAsync(text.AsMemory(), token).ConfigureAwait(false);
        return path;
    }
}
