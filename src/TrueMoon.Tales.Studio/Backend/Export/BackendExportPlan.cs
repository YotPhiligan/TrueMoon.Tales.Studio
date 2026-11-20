using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Serialization;
using TrueMoon.Tales.Studio.Contracts.Projects;
using TrueMoon.Tales.Studio.Contracts.Serialization;
using TrueMoon.Tales.Studio.Backend.Export;

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
        BackendReviewRange? range = null, bool animatic = false)
    {
        try
        {
            return (await CreateCoreAsync(project, fingerprint, profile, jobDirectory, resolver, decoder, range, token,
                animatic: animatic).ConfigureAwait(false))!;
        }
        catch (Exception error) when (error is JsonException or InvalidOperationException or KeyNotFoundException or FormatException or ScenarioEditException)
        {
            throw new InvalidDataException("Invalid saved metadata for CPU export.", error);
        }
    }

    internal static async Task<BackendReadinessReport> ReadinessAsync(ProjectContract project, string fingerprint,
        BackendOptions options, CancellationToken token)
    {
        List<BackendReadinessIssue> issues = [];
        try
        {
            await CreateCoreAsync(project, fingerprint, "draft", "", new BackendMediaResolver(options),
                BackendMediaReader.Decoder(options), null, token, issues).ConfigureAwait(false);
        }
        catch (InvalidDataException error) when (error.InnerException is DisabledLegacyOverlapException)
        { issues.Add(new("legacy_root_overlap", project.Revision, fingerprint)); }
        catch (Exception error) when (error is JsonException or InvalidDataException or InvalidOperationException or
            KeyNotFoundException or FormatException or ScenarioEditException or ArgumentException or OverflowException)
        { issues.Add(new("montage_invalid", project.Revision, fingerprint)); }
        token.ThrowIfCancellationRequested();
        return new(project.Revision, fingerprint, issues.Count,
            issues.OrderBy(issue => issue.Frame ?? -1).Where(issue => issue.Valid).Take(BackendReadinessReport.MaxRows).ToArray());
    }

    private static async Task<BackendExportPlan?> CreateCoreAsync(ProjectContract project, string fingerprint, string profile,
        string jobDirectory, BackendMediaResolver resolver, BackendMediaDecoder decoder, BackendReviewRange? range, CancellationToken token,
        List<BackendReadinessIssue>? issues = null, bool animatic = false)
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
        if (montage.Clips.Length > 2000 || montage.Windows.Length > 200)
            throw new InvalidDataException("CPU export supports at most 2000 clips and 200 windows.");
        if (range is null && montage.Clips.Length == 0 && titles.Length == 0 && (!animatic || montage.Windows.Length == 0))
        {
            if (issues is null) throw new InvalidDataException("CPU export requires saved clips or titles.");
            issues.Add(new("montage_empty", project.Revision, fingerprint));
            return null;
        }
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
            var end = (long)clip.StartFrame + clip.DurationFrames;
            var invalidTiming = clip.StartFrame < clipEnd || clip.AnchorFrame is { } anchor && anchor != clip.StartFrame ||
                clip.DurationFrames <= 0 || end > frameLimit;
            clipEnd = Math.Max(clipEnd, (int)Math.Min(end, frameLimit));
            if (invalidTiming)
            {
                Record(Reject("clip_timing", clip.Id, clip.StartFrame));
                continue;
            }
            if (!Intersects(clip.StartFrame, clip.DurationFrames)) continue;
            var assetId = sources[clip.SourceId].Takes.FirstOrDefault(take => take.Id == clip.TakeId)?.VideoAssetId;
            if (string.IsNullOrWhiteSpace(assetId))
            {
                if (animatic) continue;
                Record(Reject("clip_take_missing", clip.Id, clip.StartFrame));
                continue;
            }
            var exit = clip.SourceOutFrame ?? (clip.SourceInFrame + clip.DurationFrames * clip.Speed);
            if (!double.IsFinite(exit) || exit <= clip.SourceInFrame ||
                Math.Ceiling((exit - clip.SourceInFrame) / clip.Speed - 1e-8) != clip.DurationFrames)
            {
                Record(Reject("clip_timing", clip.Id, clip.StartFrame, assetId));
                continue;
            }
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
        var titleRows = new List<Dictionary<string, JsonElement>>();
        var stillRows = new List<AnimaticImage>();
        var unreadableVideo = issues is null ? null : new Dictionary<string, Exception>(StringComparer.Ordinal);
        foreach (var clip in placements)
        {
            if (unreadableVideo?.TryGetValue(clip.AssetId, out var unreadable) == true)
            {
                Record(DescribeMedia(unreadable, clip.AssetId, clip.StartFrame, clip.Id));
                continue;
            }
            BackendVideoInfo info;
            try
            {
                var asset = await registry.GetAsync(clip.AssetId, video: true, token).ConfigureAwait(false);
                info = await registry.VideoAsync(asset, token).ConfigureAwait(false);
            }
            catch (Exception error) when (IsMediaFailure(error))
            {
                unreadableVideo?.Add(clip.AssetId, error);
                Record(DescribeMedia(error, clip.AssetId, clip.StartFrame, clip.Id));
                continue;
            }
            var capacity = info.FrameCount / info.Fps * fps;
            if (clip.SourceOutFrame > capacity + 1e-7)
                Record(Reject("clip_source_range", clip.Id, clip.StartFrame, clip.AssetId));
        }
        if (music is not null)
            await CheckAudio(music.AssetId, 0, music.StartFrame, music.SourceInFrame, music.DurationFrames, music.Gain, music.Muted, "music", "music")
                .ConfigureAwait(false);
        foreach (var sound in sounds)
        {
            if (sound.SubtitleNeedsReview)
                Record(new("invalid_export", new("subtitle_review", project.Revision, fingerprint, AssetId: sound.AssetId,
                    Track: "sound_events", TrackId: sound.TrackId, Frame: sound.StartFrame)));
            await CheckAudio(sound.AssetId, sound.Stream, sound.StartFrame, sound.SourceInFrame, sound.DurationFrames, sound.Gain, sound.Muted,
                "sound_events", sound.TrackId, sound.OwnerClipId, sound.ExpectedSpeechSha).ConfigureAwait(false);
        }
        if (animatic)
        {
            foreach (var card in BackendAnimatic.Cards(project))
            {
                if (card.Still is { } reference)
                {
                    var image = await registry.ImageAsync(reference, token).ConfigureAwait(false);
                    stillRows.Add(new(image, card.StartFrame, card.DurationFrames));
                    continue;
                }
                var font = OperatingSystem.IsWindows() ? "arial" : "dejavu";
                var fontId = await registry.FontAsync(font, token).ConfigureAwait(false);
                var fontSize = Math.Clamp(Math.Min(titleCanvas[0] / 26, titleCanvas[1] / 9), 8, 52);
                AddTitle("animatic-" + Guid.NewGuid().ToString("N"),
                    new("Аниматик", card.Text, card.StartFrame, card.DurationFrames, 0, 0, font, fontSize,
                        "#FFFFFF", "center", "center", "transparent", false), fontId);
            }
        }
        foreach (var (id, title) in titles.Concat(sounds.Where(sound => sound.Subtitle is not null)
                     .Select(sound => ("subtitle-" + sound.TrackId, sound.Subtitle!))))
        {
            if (!Intersects(title.StartFrame, title.DurationFrames)) continue;
            string fontInput;
            try { fontInput = await registry.FontAsync(title.FontId, token).ConfigureAwait(false); }
            catch (Exception error) when (issues is not null && IsMediaFailure(error))
            {
                issues.Add(new("title_font", project.Revision, fingerprint, Frame: title.StartFrame));
                continue;
            }
            if (issues is not null) continue;
            AddTitle(id, title, fontInput);
        }

        if (issues is not null) return null;
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
            titles = titleRows, title_canvas = titleCanvas, assets = registry.Assets, timeout_seconds = 300,
            animatic_stills = stillRows
        }, Json);
        token.ThrowIfCancellationRequested();
        return new(request, frames, fps, size[0], size[1]);

        void AddTitle(string id, MontageTitle title, string font)
        {
            var fields = JsonSerializer.SerializeToElement(title, Json).EnumerateObject().ToDictionary(row => row.Name, row => row.Value);
            fields["id"] = JsonSerializer.SerializeToElement(id);
            fields["font_asset_id"] = JsonSerializer.SerializeToElement(font);
            titleRows.Add(fields);
        }

        bool Intersects(int start, int length) =>
            range is null || start < range.Value.End && (long)start + length > range.Value.Start;

        void Record(BackendReadinessException error)
        {
            if (issues is null) throw error;
            issues.Add(error.Issue);
        }

        BackendReadinessException Reject(string reason, string clip, int frame, string? asset = null) =>
            new("invalid_export", new(reason, project.Revision, fingerprint, clip, asset, Frame: frame));

        BackendReadinessException DescribeMedia(Exception error, string asset, int frame, string? clip = null, string? track = null, string? trackId = null)
        {
            var (code, reason) = error switch
            {
                FileNotFoundException or DirectoryNotFoundException => ("export_unavailable", "asset_missing"),
                UnauthorizedAccessException => ("access_denied", "asset_access"),
                System.ComponentModel.Win32Exception => ("export_unavailable", "asset_tools"),
                InvalidDataException or ArgumentException or FormatException or InvalidOperationException or JsonException =>
                    ("invalid_export", "asset_invalid"),
                _ => ("export_unavailable", "asset_invalid")
            };
            return new(code, new(reason, project.Revision, fingerprint, clip, asset, trackId is null ? null : track, trackId, frame), error);
        }

        async Task CheckAudio(string id, int stream, int frame, double start, int duration, double gain, bool muted,
            string track, string? trackId, string? clip = null, string? expectedSpeechSha = null)
        {
            Asset asset;
            try
            {
                asset = await registry.GetAsync(id, video: false, token).ConfigureAwait(false);
            }
            catch (Exception error) when (IsMediaFailure(error))
            { Record(DescribeMedia(error, id, frame, clip, track, trackId)); return; }
            if (expectedSpeechSha is not null && !string.Equals(expectedSpeechSha, asset.ExpectedSha256, StringComparison.OrdinalIgnoreCase))
            {
                Record(new("invalid_export", new("speech_source_changed", project.Revision, fingerprint, clip, id, track, trackId, frame)));
                return;
            }
            try
            {
                if (muted || gain == 0) return;
                var seconds = await registry.AudioAsync(asset, stream, token).ConfigureAwait(false);
                if (start + duration > Math.Floor(seconds * fps + 1e-6))
                    throw new InvalidDataException("Saved audio range exceeds the probed stream duration.");
            }
            catch (Exception error) when (IsMediaFailure(error))
            { Record(DescribeMedia(error, id, frame, clip, track, trackId)); }
        }
    }

    private static bool IsMediaFailure(Exception error) => error is IOException or InvalidDataException or
        UnauthorizedAccessException or ArgumentException or FormatException or InvalidOperationException or JsonException or
        System.ComponentModel.Win32Exception or NotSupportedException;

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
            string? ownerClip = null;
            string? ownerWindow = null;
            if (row.TryGetProperty("clip_id", out var clipId) && clipId.ValueKind != JsonValueKind.Null)
            {
                var clip = montage.Clips.FirstOrDefault(clip => clip.Id == clipId.GetString())
                    ?? throw new InvalidDataException("Export sound refers to an unknown clip.");
                ownerClip = clip.Id;
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
                ownerWindow = window.Id;
            }
            if ((long)ownerStart + offset + duration > int.MaxValue) throw new InvalidDataException("Export sound placement overflows.");
            var trackId = row.TryGetProperty("id", out var track) && track.ValueKind == JsonValueKind.String ? track.GetString() : null;
            var speech = BackendSpeech.Read(row);
            if (speech is not null && (ownerWindow is null || trackId is null || speech.Selected.AssetId != id || speech.Selected.Stream != stream))
                throw new InvalidDataException("Speech and sound placement disagree.");
            var review = speech is not null && BackendSpeech.NeedsReview(speech, ownerWindow!, offset, duration, source);
            var subtitle = speech?.Subtitle is { } caption && !review
                ? new MontageTitle(speech.Speaker, caption.Text, ownerStart + offset + caption.OffsetFrames, caption.DurationFrames, 0, 0,
                    OperatingSystem.IsWindows() ? "arial" : "dejavu", 36, "#FFFFFF", "center", "lower", "transparent", true)
                : null;
            result.Add(new(id, ownerStart + offset, duration, source, stream, gain, muted, trackId, ownerClip, speech?.Selected.Sha256, subtitle, review));
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
        double SourceInFrame, int Stream, double Gain, bool Muted,
        [property: JsonIgnore] string? TrackId, [property: JsonIgnore] string? OwnerClipId,
        [property: JsonIgnore] string? ExpectedSpeechSha, [property: JsonIgnore] MontageTitle? Subtitle,
        [property: JsonIgnore] bool SubtitleNeedsReview);
    private sealed record Asset(string Path, string Format, string ExpectedSha256);
    private sealed record AnimaticImage(string AssetId, int StartFrame, int DurationFrames);

    private sealed class Inputs(ProjectContract project, BackendMediaResolver resolver, BackendMediaDecoder decoder)
    {
        internal Dictionary<string, Asset> Assets { get; } = new(StringComparer.Ordinal);
        private readonly Dictionary<string, string> _hashes = new(PathComparer);
        private readonly Dictionary<string, BackendVideoInfo> _video = new(PathComparer);
        private readonly Dictionary<(string Path, int Stream), double> _audio = new();
        private readonly Dictionary<string, string> _fonts = new(StringComparer.Ordinal);
        private readonly Dictionary<string, string> _images = new(StringComparer.Ordinal);
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

        internal async Task<string> ImageAsync(string reference, CancellationToken token)
        {
            if (_images.TryGetValue(reference, out var id)) return id;
            if (Assets.Count == MaxAssets) throw new InvalidDataException("Export input count exceeds its limit.");
            var path = BackendAdaptation.ResolveStill(resolver, reference);
            id = "animatic-image-" + Guid.NewGuid().ToString("N");
            Assets.Add(id, await ReadInputAsync(path, "image", () =>
                PathComparer.Equals(BackendAdaptation.ResolveStill(resolver, reference), path), token).ConfigureAwait(false));
            return _images[reference] = id;
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
