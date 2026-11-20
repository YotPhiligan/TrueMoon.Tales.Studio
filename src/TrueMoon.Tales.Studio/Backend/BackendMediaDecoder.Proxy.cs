using System.Globalization;
using System.Text.Json;

namespace TrueMoon.Tales.Studio.Backend;

internal sealed record ProxyVideo(BackendVideoInfo Info, string Codec, int Timescale, long[] Frames, int LongestGop);

internal sealed partial class BackendMediaDecoder
{
    private const int MaxProxyFrames = 36_000;
    private const int MaxTimelineBytes = 16 * 1024 * 1024;

    internal async Task<ProxyVideo> ProbeProxyVideoAsync(string path, CancellationToken token)
    {
        string[] args = ["-v", "error", .. Input(path), "-select_streams", "v:0", "-show_packets",
            "-show_entries", "stream=codec_type,codec_name,width,height,avg_frame_rate,r_frame_rate,nb_frames,duration,time_base,sample_aspect_ratio:stream_side_data=rotation:format=duration:packet=pts,flags",
            "-of", "json"];
        var bytes = await RunAsync(ffprobeExecutable, args, MaxTimelineBytes, token).ConfigureAwait(false);
        using var json = JsonDocument.Parse(bytes);
        var info = Parse(JsonSerializer.SerializeToUtf8Bytes(new
        {
            streams = json.RootElement.GetProperty("streams"),
            format = json.RootElement.GetProperty("format")
        }));
        var stream = json.RootElement.GetProperty("streams")[0];
        var aspect = stream.TryGetProperty("sample_aspect_ratio", out var value) ? value.GetString() : null;
        var rotated = stream.TryGetProperty("side_data_list", out var sideData) &&
            sideData.EnumerateArray().Any(row => row.TryGetProperty("rotation", out var rotation) && rotation.GetInt32() != 0);
        var timeBase = stream.GetProperty("time_base").GetString()?.Split('/');
        if (info.FrameCount > MaxProxyFrames || rotated || aspect is not (null or "1:1" or "N/A") ||
            timeBase is not ["1", var timescaleValue] ||
            !int.TryParse(timescaleValue, NumberStyles.None, CultureInfo.InvariantCulture, out var timescale) || timescale < 1)
            throw new InvalidDataException("Unsupported proxy video geometry or time base.");
        var packets = json.RootElement.GetProperty("packets").EnumerateArray()
            .Select(row => (Pts: row.GetProperty("pts").GetInt64(), Key: row.GetProperty("flags").GetString()!.Contains('K')))
            .OrderBy(row => row.Pts).ToArray();
        if (packets.Length != info.FrameCount || packets[0].Pts != 0 || !packets[0].Key)
            throw new InvalidDataException("Proxy requires a zero-based, independently decodable CFR video.");
        var prevKey = 0;
        var longest = 0;
        for (var index = 1; index < packets.Length; index++)
        {
            var row = packets[index];
            if (row.Pts <= packets[index - 1].Pts || Math.Abs(row.Pts - (double)index * timescale / info.Fps) > 1.00001)
                throw new InvalidDataException("Proxy input has a variable video frame grid.");
            if (!row.Key) continue;
            longest = Math.Max(longest, index - prevKey);
            prevKey = index;
        }
        return new(info, stream.GetProperty("codec_name").GetString()!, timescale,
            packets.Select(row => row.Pts).ToArray(), Math.Max(longest, packets.Length - prevKey));
    }

    internal async Task<string> ProxyAudioFingerprintAsync(string path, CancellationToken token)
    {
        string[] args = ["-v", "error", .. Input(path), "-select_streams", "a", "-show_packets", "-show_data_hash", "sha256",
            "-show_entries", "stream=index,codec_name,time_base,start_pts,sample_rate,channels,channel_layout:packet=stream_index,pts,duration,data_hash:packet_side_data=side_data_type,skip_samples,discard_padding",
            "-of", "json"];
        var bytes = await RunAsync(ffprobeExecutable, args, MaxTimelineBytes, token).ConfigureAwait(false);
        using var json = JsonDocument.Parse(bytes);
        var tracks = json.RootElement.GetProperty("streams").EnumerateArray().ToArray();
        JsonElement[] packets = json.RootElement.TryGetProperty("packets", out var rows) ? rows.EnumerateArray().ToArray() : [];
        var ids = tracks.Select((row, index) => (Id: row.GetProperty("index").GetInt32(), index))
            .ToDictionary(row => row.Id, row => row.index);
        var proof = JsonSerializer.SerializeToUtf8Bytes(new
        {
            tracks = tracks.Select(row => row.EnumerateObject().Where(field => field.Name != "index")
                .ToDictionary(field => field.Name, field => field.Value)),
            packets = packets.Select(row => new
                {
                    track = ids[row.GetProperty("stream_index").GetInt32()],
                    fields = row.EnumerateObject().Where(field => field.Name != "stream_index")
                        .ToDictionary(field => field.Name, field => field.Value)
                }).GroupBy(row => row.track).OrderBy(group => group.Key)
        });
        return Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(proof));
    }

    internal async Task EncodeProxyAsync(string source, string target, int timescale, CancellationToken token)
    {
        string[] args = ["-nostdin", "-v", "error", "-xerror", "-n", "-filter_threads", "2", "-threads", "4", .. Input(source),
            "-map", "0:v:0", "-map", "0:a?", "-vf", "scale=960:-2:flags=lanczos,setsar=1",
            "-c:v", "libx264", "-threads", "4", "-preset", "veryfast", "-crf", "18", "-pix_fmt", "yuv420p",
            "-g", "12", "-keyint_min", "12", "-sc_threshold", "0", "-bf", "0", "-fps_mode", "passthrough",
            "-video_track_timescale", timescale.ToString(CultureInfo.InvariantCulture), "-c:a", "copy", "-movflags", "+faststart", target];
        await RunAsync(ffmpegExecutable, args, 1024, token, TimeSpan.FromMinutes(3)).ConfigureAwait(false);
    }
}
