using System.Security.Cryptography;
using System.Text.Json;
using TrueMoon.Tales.Studio.Contracts.Projects;
using TrueMoon.Tales.Studio.Contracts.Serialization;

namespace TrueMoon.Tales.Studio.Backend;

internal readonly record struct BackendReviewRange(int Start, int End)
{
    internal bool IsValid => Start >= 0 && End > Start;
}

// Builds the renderer request solely from saved placements and workspace-approved inputs.
internal sealed record BackendExportPlan(byte[] Request, int Frames, double Fps, int Width = 0, int Height = 0)
{
    private const int MaxSeconds = 600;
    private const int MaxAssets = 128;
    private const long MaxInputBytes = 2L * 1024 * 1024 * 1024;
    private static readonly JsonSerializerOptions Json = new() { PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower };
    private static readonly StringComparer PathComparer = OperatingSystem.IsWindows() ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal;

    internal static async Task<BackendExportPlan> CreateAsync(ProjectContract project, string fingerprint, string profile,
        string jobDirectory, BackendMediaResolver resolver, BackendMediaDecoder decoder, CancellationToken token,
        BackendReviewRange? range = null)
    {
        try
        {
            return await CreateCoreAsync(project, fingerprint, profile, jobDirectory, resolver, decoder, range, token).ConfigureAwait(false);
        }
        catch (Exception error) when (error is JsonException or InvalidOperationException or KeyNotFoundException or FormatException or ScenarioEditException)
        {
            throw new InvalidDataException("Invalid saved metadata for CPU export.", error);
        }
    }

    private static async Task<BackendExportPlan> CreateCoreAsync(ProjectContract project, string fingerprint, string profile,
        string jobDirectory, BackendMediaResolver resolver, BackendMediaDecoder decoder, BackendReviewRange? range, CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        ProjectContractJson.Validate(project);
        if (fingerprint.Length != 64 || !fingerprint.All(Uri.IsHexDigit))
            throw new InvalidDataException("An exact project SHA256 is required.");
        if (profile is not ("draft" or "project" or "final")) throw new InvalidDataException("Unknown export profile.");
        if (range is { } selection && (profile != "draft" || !selection.IsValid))
            throw new InvalidDataException("Invalid working review range.");
        if (project.Fps is < 1 or > 120 || project.Fps != Math.Truncate(project.Fps))
            throw new InvalidDataException("CPU export requires integral project FPS from 1 to 120.");
        var fps = (int)project.Fps;
        var frameLimit = MaxSeconds * fps;
        var montage = project.Montage;
        var titles = BackendTrackEdits.ReadTitles(BackendTrackEdits.Rows(montage, "titles"));
        if (montage.Clips.Length > 2000 || montage.Windows.Length > 200 ||
            range is null && montage.Clips.Length == 0 && titles.Length == 0)
            throw new InvalidDataException("CPU export requires saved clips or titles, with at most 2000 clips and 200 windows.");
        var transitionWindows = BackendBlackTransitions.Compile(montage);

        var windows = montage.Windows.OrderBy(row => row.StartFrame).ToArray();
        int frames = 0;
        foreach (var window in windows)
        {
            if (window.StartFrame < frames) throw new InvalidDataException("Saved montage windows overlap.");
            frames = End(window.StartFrame, window.DurationFrames, frameLimit);
        }
        var clips = montage.Clips.OrderBy(row => row.StartFrame).ToArray();
        var sources = project.Sources.ToDictionary(row => row.Id, StringComparer.Ordinal);
        var placements = new List<VideoPlacement>();
        int clipEnd = 0;
        foreach (var clip in clips)
        {
            if (clip.StartFrame < clipEnd || clip.AnchorFrame is { } anchor && anchor != clip.StartFrame)
                throw new InvalidDataException("Saved clips overlap or conflict with an anchor.");
            clipEnd = End(clip.StartFrame, clip.DurationFrames, frameLimit);
            if (!Intersects(clip.StartFrame, clip.DurationFrames)) continue;
            var assetId = sources[clip.SourceId].Takes.FirstOrDefault(take => take.Id == clip.TakeId)?.VideoAssetId;
            if (string.IsNullOrWhiteSpace(assetId)) throw new InvalidDataException("An export clip lacks a selected video take.");
            var exit = clip.SourceOutFrame ?? (clip.SourceInFrame + clip.DurationFrames * clip.Speed);
            if (!double.IsFinite(exit) || exit <= clip.SourceInFrame ||
                Math.Ceiling((exit - clip.SourceInFrame) / clip.Speed - 1e-8) != clip.DurationFrames)
                throw new InvalidDataException("Saved clip duration does not match its source range and speed.");
            placements.Add(new(clip.Id, assetId, clip.StartFrame, clip.DurationFrames, clip.SourceInFrame, exit,
                clip.Speed, clip.MuteNativeAudio));
        }

        var clipAudio = montage.Properties.TryGetValue("clip_audio", out var native) && native.GetBoolean();
        var music = ReadMusic(montage.Properties);
        var sounds = PlaceSounds(montage);
        if (music is not null) frames = Math.Max(frames, End(music.StartFrame, music.DurationFrames, frameLimit));
        foreach (var sound in sounds) frames = Math.Max(frames, End(sound.StartFrame, sound.DurationFrames, frameLimit));
        foreach (var (_, title) in titles) frames = Math.Max(frames, End(title.StartFrame, title.DurationFrames, frameLimit));
        if (range is { } bounds && bounds.End > frames)
            throw new InvalidDataException("Working review range exceeds the saved montage.");
        if (music is not null && !Intersects(music.StartFrame, music.DurationFrames)) music = null;
        sounds.RemoveAll(sound => !Intersects(sound.StartFrame, sound.DurationFrames));
        var size = Size(project.Properties, profile);
        var titleCanvas = Size(project.Properties, "project");
        var registry = new Inputs(project, resolver, decoder);
        foreach (var clip in placements)
        {
            var asset = await registry.GetAsync(clip.AssetId, video: true, token).ConfigureAwait(false);
            var info = await registry.VideoAsync(asset, token).ConfigureAwait(false);
            var capacity = info.FrameCount / info.Fps * fps;
            if (clip.SourceOutFrame > capacity + 1e-7)
                throw new InvalidDataException("Saved clip range exceeds the probed video extent.");
        }
        if (music is not null)
            await CheckAudio(music.AssetId, 0, music.SourceInFrame, music.DurationFrames, music.Gain, music.Muted).ConfigureAwait(false);
        foreach (var sound in sounds)
            await CheckAudio(sound.AssetId, sound.Stream, sound.SourceInFrame, sound.DurationFrames, sound.Gain, sound.Muted).ConfigureAwait(false);
        var titleRows = new List<Dictionary<string, JsonElement>>();
        foreach (var (id, title) in titles)
        {
            if (!Intersects(title.StartFrame, title.DurationFrames)) continue;
            var fontInput = await registry.FontAsync(title.FontId, token).ConfigureAwait(false);
            var fields = JsonSerializer.SerializeToElement(title, Json).EnumerateObject().ToDictionary(row => row.Name, row => row.Value);
            fields["id"] = JsonSerializer.SerializeToElement(id);
            fields["font_asset_id"] = JsonSerializer.SerializeToElement(fontInput);
            titleRows.Add(fields);
        }

        var final = profile == "final";
        var startFrame = range?.Start ?? 0;
        frames = (range?.End ?? frames) - startFrame;
        var request = JsonSerializer.SerializeToUtf8Bytes(new
        {
            version = 1, backend_mode = true, project_id = project.Id, project_name = project.Name,
            project_revision = project.Revision, project_fingerprint = fingerprint.ToUpperInvariant(),
            book = jobDirectory,
            path_policy = new
            {
                read_roots = registry.Assets.Values.Select(row => row.Path).Distinct(PathComparer).ToArray(),
                write_roots = new[] { jobDirectory }, protected_roots = Array.Empty<string>()
            },
            fps, frames, start_frame = startFrame, seconds = frames / (double)fps, size, profile,
            encoding = new
            {
                video_codec = "libx264", pixel_format = "yuv420p",
                preset = final ? "medium" : "veryfast", crf = final ? 18 : 25,
                audio_codec = "aac", audio_bitrate_kbps = final ? 320 : 192
            },
            clips = placements, transition_windows = transitionWindows, music, clip_audio = clipAudio, sounds,
            titles = titleRows, title_canvas = titleCanvas, assets = registry.Assets, timeout_seconds = 300
        }, Json);
        token.ThrowIfCancellationRequested();
        return new(request, frames, fps, size[0], size[1]);

        bool Intersects(int start, int length) =>
            range is null || start < range.Value.End && (long)start + length > range.Value.Start;

        async Task CheckAudio(string id, int stream, double start, int duration, double gain, bool muted)
        {
            var asset = await registry.GetAsync(id, video: false, token).ConfigureAwait(false);
            if (muted || gain == 0) return;
            var seconds = await registry.AudioAsync(asset, stream, token).ConfigureAwait(false);
            if (start + duration > Math.Floor(seconds * fps + 1e-6))
                throw new InvalidDataException("Saved audio range exceeds the probed stream duration.");
        }
    }

    private static int End(int start, int duration, int limit)
    {
        if (start < 0 || duration < 1 || (long)start + duration > limit)
            throw new InvalidDataException("Export timing is outside the supported extent.");
        return start + duration;
    }

    private static int[] Size(IReadOnlyDictionary<string, JsonElement> properties, string profile)
    {
        int[] size = [1280, 736];
        if (properties.TryGetValue("output_size", out var value))
        {
            if (value.ValueKind != JsonValueKind.Array || value.GetArrayLength() != 2 ||
                !value[0].TryGetInt32(out size[0]) || !value[1].TryGetInt32(out size[1]) || size.Any(n => n is < 2 or > 4096))
                throw new InvalidDataException("Invalid project export size.");
        }
        var scale = profile == "draft" ? Math.Min(1, 896d / Math.Max(size[0], size[1])) : 1;
        return size.Select(n => Math.Max(2, (int)Math.Round(n * scale / 2) * 2)).ToArray();
    }

    private static MusicPlacement? ReadMusic(IReadOnlyDictionary<string, JsonElement> properties)
    {
        if (!properties.TryGetValue("music", out var row) || row.ValueKind == JsonValueKind.Null) return null;
        var music = new MusicPlacement(Text(row, "asset_id"), row.GetProperty("start_frame").GetInt32(),
            row.GetProperty("duration_frames").GetInt32(), row.GetProperty("source_in_frame").GetDouble(),
            row.GetProperty("gain").GetDouble(), row.GetProperty("muted").GetBoolean());
        CheckAudioTiming(music.SourceInFrame, music.Gain, 0);
        return music;
    }

    private static List<SoundPlacement> PlaceSounds(MontageContract montage)
    {
        if (!montage.Properties.TryGetValue("sound_events", out var rows) || rows.ValueKind == JsonValueKind.Null) return [];
        if (rows.ValueKind != JsonValueKind.Array || rows.GetArrayLength() > 64)
            throw new InvalidDataException("Invalid export sound events.");
        var result = new List<SoundPlacement>();
        foreach (var row in rows.EnumerateArray())
        {
            var id = Text(row, "asset_id");
            var offset = row.GetProperty("offset_frames").GetInt32();
            var duration = row.GetProperty("duration_frames").GetInt32();
            var source = row.GetProperty("source_in_frame").GetDouble();
            var gain = row.GetProperty("gain").GetDouble();
            var muted = row.GetProperty("muted").GetBoolean();
            var stream = row.GetProperty("stream").GetInt32();
            CheckAudioTiming(source, gain, stream);
            if (offset < 0 || duration < 1) throw new InvalidDataException("Invalid export sound placement.");
            int ownerStart;
            if (row.TryGetProperty("clip_id", out var clipId) && clipId.ValueKind != JsonValueKind.Null)
            {
                var clip = montage.Clips.FirstOrDefault(clip => clip.Id == clipId.GetString())
                    ?? throw new InvalidDataException("Export sound refers to an unknown clip.");
                ownerStart = clip.StartFrame;
                duration = Math.Min(duration, clip.DurationFrames - offset);
                if (duration <= 0) continue;
            }
            else
            {
                var segment = Text(row, "segment_id");
                var window = montage.Windows.FirstOrDefault(window => window.Id == segment);
                if (window is null)
                {
                    var matching = montage.Windows.Where(window => window.SegmentId == segment).Take(2).ToArray();
                    window = matching.Length == 1 ? matching[0] : throw new InvalidDataException("Export sound has no unique saved window.");
                }
                ownerStart = window.StartFrame;
            }
            if ((long)ownerStart + offset > int.MaxValue) throw new InvalidDataException("Export sound placement overflows.");
            result.Add(new(id, ownerStart + offset, duration, source, stream, gain, muted));
        }
        return result;
    }

    private static void CheckAudioTiming(double source, double gain, int stream)
    {
        if (!double.IsFinite(source) || source < 0 || !double.IsFinite(gain) || gain is < 0 or > 1 || stream is < 0 or > 63)
            throw new InvalidDataException("Invalid saved audio source, gain or stream.");
    }

    private static string Text(JsonElement row, string name) =>
        row.GetProperty(name).GetString() is { Length: > 0 and <= 4096 } value && !string.IsNullOrWhiteSpace(value)
            ? value : throw new InvalidDataException("Invalid export metadata string.");

    private sealed record VideoPlacement(string Id, string AssetId, int StartFrame, int DurationFrames,
        double SourceInFrame, double SourceOutFrame, double Speed, bool MuteNativeAudio);
    private sealed record MusicPlacement(string AssetId, int StartFrame, int DurationFrames, double SourceInFrame, double Gain, bool Muted);
    private sealed record SoundPlacement(string AssetId, int StartFrame, int DurationFrames,
        double SourceInFrame, int Stream, double Gain, bool Muted);
    private sealed record Asset(string Path, string Format, string ExpectedSha256);

    private sealed class Inputs(ProjectContract project, BackendMediaResolver resolver, BackendMediaDecoder decoder)
    {
        internal Dictionary<string, Asset> Assets { get; } = new(StringComparer.Ordinal);
        private readonly Dictionary<string, string> _hashes = new(PathComparer);
        private readonly Dictionary<string, BackendVideoInfo> _video = new(PathComparer);
        private readonly Dictionary<(string Path, int Stream), double> _audio = new();
        private readonly Dictionary<string, string> _fonts = new(StringComparer.Ordinal);
        private long _inputBytes;

        internal async Task<Asset> GetAsync(string id, bool video, CancellationToken token)
        {
            token.ThrowIfCancellationRequested();
            if (id.Length > 160 || !project.Properties.TryGetValue("assets", out var entries) ||
                entries.ValueKind != JsonValueKind.Object || !entries.TryGetProperty(id, out var metadata) ||
                metadata.ValueKind != JsonValueKind.Object)
                throw new InvalidDataException("A registered export asset is missing.");
            if (metadata.TryGetProperty("kind", out var kind) &&
                (video ? kind.GetString() != "video" : kind.GetString() is not ("audio" or "video")))
                throw new InvalidDataException("Export asset kind does not match its placement.");
            if (Assets.TryGetValue(id, out var saved)) return saved;
            if (Assets.Count == MaxAssets) throw new InvalidDataException("Export input count exceeds its limit.");

            var logical = Text(metadata, "path");
            var path = resolver.Resolve(logical);
            var format = BackendMediaDecoder.LocalInputFormat(path, audio: !video);
            return Assets[id] = await ReadInputAsync(path, format, () => PathComparer.Equals(resolver.Resolve(logical), path), token)
                .ConfigureAwait(false);
        }

        internal async Task<string> FontAsync(string font, CancellationToken token)
        {
            if (_fonts.TryGetValue(font, out var id)) return id;
            id = "title-font-" + Guid.NewGuid().ToString("N");
            if (Assets.Count == MaxAssets) throw new InvalidDataException("Export input count exceeds its limit.");
            var filename = font switch
            {
                "arial" => "arial.ttf", "arial-bold" => "arialbd.ttf", "georgia" => "georgia.ttf",
                "times" => "times.ttf", "consolas" => "consola.ttf", "segoe" => "segoeui.ttf",
                "dejavu" when !OperatingSystem.IsWindows() => "DejaVuSans.ttf",
                _ => throw new InvalidDataException("Title font is unavailable.")
            };
            var directory = OperatingSystem.IsWindows() ? Environment.GetFolderPath(Environment.SpecialFolder.Fonts) :
                "/usr/share/fonts/truetype/dejavu";
            var path = Path.Combine(directory, filename);
            if (!Path.IsPathFullyQualified(path) || !File.Exists(path)) throw new InvalidDataException("Title font is unavailable.");
            BackendMediaResolver.CheckNoLinks(path);
            Assets.Add(id, await ReadInputAsync(path, "font", () =>
            {
                BackendMediaResolver.CheckNoLinks(path);
                return true;
            }, token).ConfigureAwait(false));
            return _fonts[font] = id;
        }

        private async Task<Asset> ReadInputAsync(string path, string format, Func<bool> unchanged, CancellationToken token)
        {
            if (!_hashes.TryGetValue(path, out var fingerprint))
            {
                await using var source = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read,
                    64 * 1024, FileOptions.Asynchronous | FileOptions.SequentialScan);
                var length = source.Length;
                if (length == 0 || length > MaxInputBytes - _inputBytes)
                    throw new InvalidDataException("Export media is empty or exceeds its aggregate byte limit.");
                fingerprint = Convert.ToHexStringLower(await SHA256.HashDataAsync(source, token).ConfigureAwait(false));
                if (source.Length != length || !unchanged())
                    throw new InvalidDataException("Media changed while recording its SHA256.");
                _hashes.Add(path, fingerprint);
                _inputBytes += length;
            }
            return new Asset(path, format, fingerprint);
        }

        internal async Task<BackendVideoInfo> VideoAsync(Asset asset, CancellationToken token)
        {
            if (_video.TryGetValue(asset.Path, out var info)) return info;
            return _video[asset.Path] = await decoder.ProbeAsync(asset.Path, token).ConfigureAwait(false);
        }

        internal async Task<double> AudioAsync(Asset asset, int stream, CancellationToken token)
        {
            var key = (asset.Path, stream);
            if (_audio.TryGetValue(key, out var seconds)) return seconds;
            return _audio[key] = await decoder.ProbeAudioDurationAsync(asset.Path, stream, token).ConfigureAwait(false);
        }
    }
}
