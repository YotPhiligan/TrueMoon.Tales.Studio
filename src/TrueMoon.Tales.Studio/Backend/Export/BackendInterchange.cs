using System.Globalization;
using System.Text.Json;
using System.Xml.Linq;
using TrueMoon.Tales.Studio.Contracts.Projects;
using TrueMoon.Tales.Studio.Contracts.Serialization;

namespace TrueMoon.Tales.Studio.Backend.Export;

internal sealed record BackendInterchangeReceipt(string Directory, long ProjectRevision, string ProjectFingerprint,
    string XmlSha256, int IssueCount);

internal sealed record BackendInterchange(byte[] Xml, byte[] Report, int IssueCount)
{
    internal const string XmlName = "Timeline.xml";
    internal const string ReportName = "Report.json";

    private sealed record Media(string Path, int Frames, BackendVideoInfo? Video, bool Audio);
    private sealed record Item(string Id, string Name, Media File, int Start, int End, int In, int Out, double Gain = 1);
    private sealed record Marker(string? Id, string Name, int Frame);
    private sealed record Issue(string Code, string? ItemId, int? Frame);

    internal static async Task<BackendInterchange> CreateAsync(ProjectContract project, string fingerprint,
        BackendMediaResolver resolver, BackendMediaDecoder decoder, CancellationToken token)
    {
        ProjectContractJson.Validate(project);
        var montage = project.Montage;
        if (project.Fps is < 1 or > 120 || project.Fps != Math.Truncate(project.Fps) ||
            montage.Clips.Length > 2000 || montage.Windows.Length is 0 or > 200)
            throw new BackendExportException("interchange_timeline_invalid");
        var fps = (int)project.Fps;
        var size = BackendExportPlan.Size(project.Properties, "project");
        var sources = project.Sources.ToDictionary(source => source.Id, StringComparer.Ordinal);
        var video = new List<Item>();
        var native = new List<Item>();
        var music = new List<Item>();
        var sounds = new List<Item>();
        var issues = new List<Issue>();
        var media = new Dictionary<(string Id, bool Video), Media?>();
        var transitions = BackendBlackTransitions.Compile(montage);
        var incoming = transitions.ToDictionary(row => row.RightClipId, StringComparer.Ordinal);
        var outgoing = transitions.ToDictionary(row => row.LeftClipId, StringComparer.Ordinal);
        var clipAudio = montage.Properties.TryGetValue("clip_audio", out var enabled) && enabled.GetBoolean();
        var clips = montage.Clips.OrderBy(clip => clip.StartFrame).ThenBy(clip => clip.Id, StringComparer.Ordinal).ToArray();
        if (clips.Zip(clips.Skip(1)).Any(pair => (long)pair.First.StartFrame + pair.First.DurationFrames > pair.Second.StartFrame))
            throw new BackendExportException("interchange_timeline_invalid");
        var plannedClips = new List<object>(clips.Length);
        var sequenceEnd = montage.Windows.Max(window => checked(window.StartFrame + window.DurationFrames));

        foreach (var clip in clips)
        {
            token.ThrowIfCancellationRequested();
            var source = sources[clip.SourceId];
            var take = source.Takes.FirstOrDefault(row => row.Id == clip.TakeId);
            var assetId = take?.VideoAssetId;
            var sourceOut = clip.SourceOutFrame ?? (clip.SourceInFrame + clip.DurationFrames * clip.Speed);
            var end = checked(clip.StartFrame + clip.DurationFrames);
            sequenceEnd = Math.Max(sequenceEnd, end);
            var input = assetId is { Length: > 0 } ? await ResolveAsync(assetId, isVideo: true).ConfigureAwait(false) : null;
            plannedClips.Add(new { Clip = clip, AssetId = assetId, SourceOutFrame = sourceOut, OriginalUri =
                input is null ? null : new Uri(input.Path).AbsoluteUri });
            if (clip.Speed != 1 || !Whole(clip.SourceInFrame) || !Whole(sourceOut) || sourceOut - clip.SourceInFrame != clip.DurationFrames)
            {
                issues.Add(new("video_retime_or_subframe", clip.Id, clip.StartFrame));
                continue;
            }
            if (input is null || sourceOut > input.Frames)
            {
                issues.Add(new("video_unavailable_or_out_of_range", clip.Id, clip.StartFrame));
                continue;
            }
            var label = string.IsNullOrWhiteSpace(source.Title) ? Path.GetFileName(input.Path) : source.Title;
            var item = new Item(clip.Id, label, input, clip.StartFrame, end, (int)clip.SourceInFrame, (int)sourceOut);
            video.Add(item);
            if (input.Audio)
                native.Add(item with { Gain = clipAudio && !clip.MuteNativeAudio ? 1 : 0 });
        }

        if (BackendExportPlan.ReadMusic(montage.Properties) is { } song)
            await AddAudioAsync(music, "music", "Музыка", song.AssetId, song.StartFrame, song.DurationFrames,
                song.SourceInFrame, 0, song.Muted ? 0 : song.Gain).ConfigureAwait(false);
        var soundRows = BackendTrackEdits.Rows(montage, "sound_events");
        var soundNames = soundRows.OfType<System.Text.Json.Nodes.JsonObject>()
            .Where(row => row["id"] is not null).ToDictionary(row => (string)row["id"]!, row => (string?)row["name"], StringComparer.Ordinal);
        var placements = BackendExportPlan.PlaceSounds(montage);
        foreach (var sound in placements)
        {
            var id = sound.TrackId ?? $"sound-{sounds.Count}-{issues.Count}";
            await AddAudioAsync(sounds, id, soundNames.GetValueOrDefault(id) ?? "Звук", sound.AssetId, sound.StartFrame,
                sound.DurationFrames, sound.SourceInFrame, sound.Stream, sound.Muted ? 0 : sound.Gain).ConfigureAwait(false);
            if (sound.Subtitle is not null || sound.SubtitleNeedsReview)
                issues.Add(new("subtitle_report_only", id, sound.Subtitle?.StartFrame ?? sound.StartFrame));
        }
        var markers = BackendTrackEdits.Rows(montage, "markers").Select(node => new Marker((string?)node!["id"],
            (string)node["name"]!, (int)node["frame"]!)).OrderBy(row => row.Frame).ToArray();
        if (markers.Any(marker => marker.Frame < 0 || marker.Frame >= sequenceEnd))
            throw new BackendExportException("interchange_timeline_invalid");
        issues.AddRange(markers.Select(marker => new Issue("marker_open_shot_report_only", marker.Id, marker.Frame)));
        var titles = BackendTrackEdits.ReadTitles(BackendTrackEdits.Rows(montage, "titles"));
        foreach (var (id, title) in titles)
        {
            issues.Add(new("title_report_only", id, title.StartFrame));
            sequenceEnd = Math.Max(sequenceEnd, checked(title.StartFrame + title.DurationFrames));
        }
        var importedIds = video.Select(item => item.Id).ToHashSet(StringComparer.Ordinal);
        issues.AddRange(transitions.Where(row => !importedIds.Contains(row.LeftClipId) || !importedIds.Contains(row.RightClipId))
            .Select(row => new Issue("transition_incomplete", row.RightClipId, row.OutStartFrame)));

        var videoTracks = new XElement("video", new XElement("format", VideoProperties(size[0], size[1])),
            Track(video, (item, index) => ClipXml(item, $"video-{index}", false, Opacity(item))));
        var audioTracks = new XElement("audio", new XElement("channelcount", 2));
        foreach (var lane in new[] { native, music }.Concat(Lanes(sounds)).Where(lane => lane.Count > 0))
        {
            var key = audioTracks.Elements("track").Count();
            audioTracks.Add(Track(lane, (item, index) => ClipXml(item, $"audio-{key}-{index}", true,
                Effect("audiolevels", "Audio Levels", "audio", "level", 1, [(1, item.Gain)]))));
        }
        var sequence = new XElement("sequence", new XAttribute("id", "studio-sequence"), new XElement("name", project.Name),
            new XElement("duration", sequenceEnd), Rate(), new XElement("media", videoTracks, audioTracks),
            markers.Select(marker => new XElement("marker", new XElement("name", marker.Name), new XElement("in", marker.Frame), new XElement("out", -1))));
        var document = new XDocument(new XDeclaration("1.0", "utf-8", null), new XDocumentType("xmeml", null, null, null),
            new XElement("xmeml", new XAttribute("version", 4), sequence));
        using var buffer = new MemoryStream();
        document.Save(buffer);
        var xml = buffer.ToArray();
        var report = JsonSerializer.SerializeToUtf8Bytes(new
        {
            Version = 1, Format = "xmeml-4", TestedEditor = "OpenShot 4.1.1", ProjectId = project.Id,
            ProjectRevision = project.Revision, ProjectFingerprint = fingerprint, XmlSha256 = BackendExportStorage.Hash(xml),
            Fps = fps, Width = size[0], Height = size[1], Frames = sequenceEnd, montage.Windows, Clips = plannedClips,
            Markers = markers, Titles = titles.Select(row => new { row.Id, row.Title }), Transitions = transitions,
            SoundEvents = soundRows, Music = BackendTrackEdits.Object(montage, "music"), Issues = issues
        }, BackendExportStorage.Json);
        if (xml.Length > 8 * BackendExportStorage.MaxRequestBytes || report.Length > 2 * BackendExportStorage.MaxRequestBytes)
            throw new BackendExportException("interchange_timeline_invalid");
        return new(xml, report, issues.Count);

        async Task<Media?> ResolveAsync(string id, bool isVideo)
        {
            if (media.TryGetValue((id, isVideo), out var cached)) return cached;
            if (media.Count >= 128) throw new BackendExportException("interchange_timeline_invalid");
            Media? input = null;
            try
            {
                if (BackendScenarioEdit.TryReference(project, id) is { } reference)
                {
                    var path = resolver.Resolve(reference);
                    using var file = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
                    if (isVideo)
                    {
                        var info = await decoder.ProbeAsync(path, token).ConfigureAwait(false);
                        input = new(path, (int)Math.Floor(info.FrameCount / info.Fps * fps + 1e-8), info,
                            await decoder.HasAudioAsync(path, 0, token).ConfigureAwait(false));
                    }
                    else if (Path.GetExtension(path).ToLowerInvariant() is ".wav" or ".flac" or ".aac")
                    {
                        var seconds = await decoder.ProbeAudioDurationAsync(path, 0, token).ConfigureAwait(false);
                        input = new(path, (int)Math.Floor(seconds * fps + 1e-4), null, true);
                    }
                    if (resolver.Resolve(reference) != path) input = null;
                }
            }
            catch (Exception error) when (error is IOException or UnauthorizedAccessException or InvalidDataException or JsonException or ArgumentException or InvalidOperationException)
            { }
            return media[(id, isVideo)] = input;
        }

        async Task AddAudioAsync(List<Item> lane, string id, string name, string asset, int start, int frames,
            double sourceIn, int stream, double gain)
        {
            var end = checked(start + frames);
            sequenceEnd = Math.Max(sequenceEnd, end);
            if (start < 0 || frames <= 0 || stream != 0 || !Whole(sourceIn) ||
                await ResolveAsync(asset, isVideo: false).ConfigureAwait(false) is not { } file || sourceIn + frames > file.Frames)
            {
                issues.Add(new("audio_not_representable", id, start));
                return;
            }
            lane.Add(new(id, name, file, start, end, (int)sourceIn, (int)sourceIn + frames, gain));
        }

        XElement Rate() => new("rate", new XElement("timebase", fps), new XElement("ntsc", "FALSE"));
        XElement VideoProperties(int width, int height) => new("samplecharacteristics", Rate(), new XElement("width", width),
            new XElement("height", height), new XElement("anamorphic", "FALSE"), new XElement("pixelaspectratio", "square"),
            new XElement("fielddominance", "none"));
        XElement ClipXml(Item item, string id, bool audio, XElement? filter)
        {
            var file = item.File;
            return new XElement("clipitem", new XAttribute("id", id), new XElement("name", item.Name), new XElement("enabled", "TRUE"),
                new XElement("duration", item.End - item.Start), Rate(), new XElement("start", item.Start), new XElement("end", item.End),
                new XElement("in", item.In), new XElement("out", item.Out),
                new XElement("file", new XAttribute("id", "file-" + id), new XElement("name", Path.GetFileName(file.Path)),
                    new XElement("pathurl", new Uri(file.Path).AbsoluteUri), Rate(), new XElement("duration", file.Frames),
                    new XElement("media", file.Video is { } info ? new XElement("video", VideoProperties(info.Width, info.Height)) : null,
                        file.Audio ? new XElement("audio") : null)),
                audio ? new XElement("sourcetrack", new XElement("mediatype", "audio"), new XElement("trackindex", 1)) : null, filter);
        }

        XElement? Opacity(Item item)
        {
            incoming.TryGetValue(item.Id, out var before);
            outgoing.TryGetValue(item.Id, out var after);
            if (before is null && after is null) return null;
            var frames = new SortedSet<int> { item.Start, item.End - 1 };
            if (before is not null) AddEdges(before.InEndFrame);
            if (after is not null) { AddEdges(after.OutStartFrame); AddEdges(after.BlackStartFrame); }
            var points = frames.Select(frame => (item.In + frame - item.Start + 1, 100 * Value(frame))).ToArray();
            return Effect("opacity", "Opacity", "video", "opacity", 100, points);

            void AddEdges(int frame)
            {
                if (frame > item.Start && frame <= item.End) frames.Add(frame - 1);
                if (frame >= item.Start && frame < item.End) frames.Add(frame);
            }
            double Value(int frame) => before is not null && frame < before.InEndFrame ?
                (frame - before.RightStartFrame + .5) / before.FadeInFrames :
                after is not null && frame >= after.OutStartFrame ?
                    frame < after.BlackStartFrame ? (after.BlackStartFrame - frame - .5) / after.FadeOutFrames : 0 : 1;
        }
    }

    private static bool Whole(double value) => value is >= 0 and <= int.MaxValue && value == Math.Truncate(value);

    private static XElement Track(IEnumerable<Item> items, Func<Item, int, XElement> render) =>
        new("track", new XElement("enabled", "TRUE"), new XElement("locked", "FALSE"), items.Select(render));

    private static IEnumerable<List<Item>> Lanes(IEnumerable<Item> sounds)
    {
        var lanes = new List<List<Item>>();
        foreach (var sound in sounds.OrderBy(item => item.Start))
        {
            var lane = lanes.FirstOrDefault(row => row[^1].End <= sound.Start);
            if (lane is null) lanes.Add([sound]);
            else lane.Add(sound);
        }
        return lanes;
    }

    private static XElement Effect(string id, string name, string media, string parameter, int max, (int Frame, double Value)[] points) =>
        new("filter", new XElement("effect", new XElement("name", name), new XElement("effectid", id),
            new XElement("effectcategory", id == "opacity" ? "motion" : "audiolevels"),
            new XElement("effecttype", id == "opacity" ? "motion" : "audiolevels"), new XElement("mediatype", media),
            new XElement("parameter", new XElement("parameterid", parameter), new XElement("name", parameter),
                new XElement("valuemin", 0), new XElement("valuemax", max),
                points.Select(point => new XElement("keyframe", new XElement("when", point.Frame),
                    new XElement("value", point.Value.ToString("G17", CultureInfo.InvariantCulture)),
                    new XElement("interpolation", new XElement("name", "Linear")))))));
}
