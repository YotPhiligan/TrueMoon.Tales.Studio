using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using TrueMoon.Tales.Studio.Configuration;
using TrueMoon.Tales.Studio.Editing;
using TrueMoon.Tales.Studio.Models;

namespace TrueMoon.Tales.Studio.Infrastructure;

internal sealed class StudioHttpClient : IDisposable
{
    private static readonly JsonSerializerOptions Json = new() { PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower };
    private readonly HttpClient _http;
    private readonly string _projectPath;
    private readonly string _projectId;
    private readonly string? _segmentId;

    public StudioHttpClient(StudioOptions options)
        : this(options, new SocketsHttpHandler { AllowAutoRedirect = false, UseProxy = false }) { }

    // The service also owns an injected handler; tests use this constructor without opening a socket.
    internal StudioHttpClient(StudioOptions options, HttpMessageHandler handler)
    {
        _http = new(handler);
        try
        {
            ArgumentNullException.ThrowIfNull(options);
            _http.BaseAddress = options.Endpoint;
            _http.Timeout = TimeSpan.FromSeconds(10);
            _projectPath = $"api/projects/{Uri.EscapeDataString(options.ProjectId)}";
            _projectId = options.ProjectId;
            _segmentId = options.SegmentId;
        }
        catch { _http.Dispose(); throw; }
    }

    public Uri Media(string assetId) => new(_http.BaseAddress!,
        $"api/media?project={Uri.EscapeDataString(_projectId)}&asset={Uri.EscapeDataString(assetId)}");

    public Task<SceneSnapshot> OpenAsync(string? clipId, CancellationToken cancellationToken) =>
        ReadSceneAsync(new(HttpMethod.Get, _projectPath), clipId, cancellationToken);

    public async Task<bool> HasAudioAsync(string assetId, int stream, CancellationToken token)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, $"{_projectPath}/audio-streams?asset={Uri.EscapeDataString(assetId)}");
        var streams = await SendAsync<JsonElement[]>(request, token);
        return stream >= 0 && stream < streams.Length;
    }

    public async Task<SceneSnapshot> TrimAsync(SceneSnapshot scene, double start, double end, CancellationToken cancellationToken, int? timelineStart = null)
    {
        using var bootstrap = new HttpRequestMessage(HttpMethod.Get, "api/bootstrap");
        var session = await SendAsync<StudioSession>(bootstrap, cancellationToken);
        var payload = new Dictionary<string, object?>
        {
            ["id"] = scene.Clip.Id, ["source_in_frame"] = start, ["source_out_frame"] = end
        };
        if (timelineStart is { } position)
        {
            payload["start_frame"] = position;
            payload["duration_frames"] = TimelineTrim.Duration(start, end, scene.Clip.Speed ?? 1);
            payload["speed"] = scene.Clip.Speed ?? 1;
        }
        var body = JsonSerializer.Serialize(new
        {
            expected_revision = scene.Revision,
            actor = "studio:argentis",
            action = timelineStart is null ? "edit_clip_source" : "update_clip",
            payload
        });
        using var request = new HttpRequestMessage(HttpMethod.Post, $"{_projectPath}/timeline")
        {
            // The Python endpoint reads a Content-Length bounded JSON body.
            Content = new StringContent(body, Encoding.UTF8, "application/json")
        };
        request.Headers.Add("X-Studio-Token", session.Token);
        return await ReadSceneAsync(request, scene.Clip.Id, cancellationToken);
    }

    public async Task<MediaInfo> MediaInfoAsync(Take take, CancellationToken cancellationToken)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get,
            $"{_projectPath}/media-info?asset={Uri.EscapeDataString(take.VideoAssetId)}");
        var info = take.VideoInfo ?? await SendAsync<MediaInfo>(request, cancellationToken);
        if (!double.IsFinite(info.Fps) || info.Fps <= 0 || info.FrameCount < 1)
            throw new InvalidDataException("Для дубля недоступна частота или длина видео.");
        return info;
    }

    private async Task<SceneSnapshot> ReadSceneAsync(HttpRequestMessage request, string? clipId, CancellationToken cancellationToken)
    {
        using (request)
        {
            var project = await SendAsync<Project>(request, cancellationToken);
            if (project.Timeline is null || project.Fps <= 0)
                throw new InvalidDataException("У проекта нет монтажной шкалы.");

            var choices = new List<ClipChoice>();
            foreach (var clip in project.Timeline.Clips)
            {
                if (_segmentId is not null && _segmentId != clip.SegmentId)
                    continue;

                var block = project.Blocks.FirstOrDefault(block => block.Id == clip.BlockId);
                var takes = block?.Takes.Where(take => !string.IsNullOrEmpty(take.VideoAssetId)).ToArray();
                if (takes is not { Length: >= 1 } || !takes.Any(take => take.Id == clip.TakeId))
                    continue;

                var segment = project.Timeline.Segments.FirstOrDefault(row => row.Id == clip.SegmentId);
                if (segment is not null)
                    choices.Add(new(clip, segment, clip.StoryTitle ?? block!.Title ?? block.Label, takes));
            }
            var selected = clipId is null ? choices.FirstOrDefault() : choices.FirstOrDefault(row => row.Clip.Id == clipId);
            if (selected is null)
                throw new InvalidDataException("Выбранный монтажный клип недоступен. Нужен клип с назначенным видеодублем.");
            var audio = AudioTimeline.Build(selected.Clip, selected.Takes.First(take => take.Id == selected.Clip.TakeId).VideoAssetId,
                project.Fps, project.Timeline.Clips, project.Timeline.Segments, project.Timeline.ClipAudio, project.Timeline.Music, project.Timeline.SoundEvents);
            return new(project.Name, project.Revision, project.Fps, selected.Segment, selected.Clip,
                selected.Title, selected.Takes, choices.ToArray(), project.Timeline.Clips.Where(row => row.SegmentId == selected.Segment.Id).ToArray(), audio);
        }
    }

    private async Task<T> SendAsync<T>(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        using var response = await _http.SendAsync(request, cancellationToken);
        if (!response.IsSuccessStatusCode)
        {
            var failure = await response.Content.ReadFromJsonAsync<StudioFailure>(Json, cancellationToken);
            throw new HttpRequestException($"HTTP {(int)response.StatusCode} · {failure?.Error}", null, response.StatusCode);
        }
        return await response.Content.ReadFromJsonAsync<T>(Json, cancellationToken)
            ?? throw new InvalidDataException("Studio вернула пустой ответ.");
    }

    private sealed record StudioSession(string Token);
    private sealed record StudioFailure(string Error);
    private sealed record Project(string Name, int Revision, double Fps, Block[] Blocks, Timeline? Timeline);
    private sealed record Block(string Id, string? Title, string Label, Take[] Takes);
    private sealed record Timeline(Segment[] Segments, Clip[] Clips, bool ClipAudio = false, AudioMusic? Music = null, AudioEvent[]? SoundEvents = null);

    public void Dispose() => _http.Dispose();
}
