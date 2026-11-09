using System.Net;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using TrueMoon.Alloy.Runtime;
using TrueMoon.Argentis;
using TrueMoon.Tales.Studio.Configuration;
using TrueMoon.Tales.Studio.Editing;
using TrueMoon.Tales.Studio.Infrastructure;
using TrueMoon.Tales.Studio.Media;
using TrueMoon.Tales.Studio.Models;
using TrueMoon.Tales.Studio.Presentation.Scene;

namespace TrueMoon.Tales.Studio.Tests.Support;

internal sealed class InteractionFixture : IDisposable
{
    public readonly FakeStudio Server = new();
    public readonly FakeFrames Frames = new();
    private readonly StudioHttpClient _api;
    public SceneViewModel Model { get; }

    public InteractionFixture(IPreviewMedia? preview = null)
    {
        _api = new(new StudioOptions(new("http://127.0.0.1:12345/"), "qa", "unused-ffmpeg"), Server);
        Model = new(_api, Frames, preview);
        Model.Open();
        Model.Poll();
        if (!Model.Loaded) throw new InvalidOperationException(Model.Status);
    }

    public void Dispose() { Model.Dispose(); _api.Dispose(); }
}

internal sealed class FakeFrames : IFrameDecoder
{
    public bool Block;
    public readonly List<(int Frame, TaskCompletionSource<byte[]> Completion)> Calls = [];
    public static byte[] Pixels(int frame)
    {
        var pixels = new byte[FrameDecoder.Width * FrameDecoder.Height * 4];
        pixels[0] = (byte)frame;
        return pixels;
    }
    public Task<byte[]> ReadAsync(Uri media, int frame, double fps, CancellationToken cancellationToken)
    {
        var complete = new TaskCompletionSource<byte[]>(TaskCreationOptions.RunContinuationsAsynchronously);
        Calls.Add((frame, complete));
        if (!Block) complete.SetResult(Pixels(frame));
        cancellationToken.Register(() => complete.TrySetCanceled(cancellationToken));
        return complete.Task;
    }
}

internal sealed class FakeStudio : HttpMessageHandler
{
    public Clip Current = new("clip-A", "segment", "block", "take-A", "Клип A", 0, 8, 0, 8, 1);
    public int Revision = 5;
    public HttpStatusCode? RejectWrite;
    public readonly List<JsonObject> Posts = [];
    protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        if (request.Method == HttpMethod.Post)
        {
            var body = JsonNode.Parse(request.Content!.ReadAsStringAsync(cancellationToken).GetAwaiter().GetResult())!.AsObject();
            Posts.Add(body);
            if (RejectWrite is { } rejected) return Reply(new { error = "Тестовый отказ" }, rejected);
            if (body["expected_revision"]!.GetValue<int>() != Revision) return Reply(new { error = "Ревизия" }, HttpStatusCode.Conflict);
            if (!request.Headers.TryGetValues("X-Studio-Token", out var tokens) || tokens.Single() != "qa-session")
                return Reply(new { error = "Токен" }, HttpStatusCode.Forbidden);
            var payload = body["payload"]!;
            var start = payload["source_in_frame"]!.GetValue<double>();
            var end = payload["source_out_frame"]!.GetValue<double>();
            Current = Current with { SourceInFrame = start, SourceOutFrame = end,
                StartFrame = payload["start_frame"]?.GetValue<int>() ?? Current.StartFrame,
                DurationFrames = TimelineTrim.Duration(start, end, Current.Speed ?? 1) };
            Revision++;
        }
        if (request.RequestUri!.AbsolutePath == "/api/bootstrap") return Reply(new { token = "qa-session" });
        var takeA = new Take("take-A", "video-A", new(24, .5, 12), "Дубль A");
        var takeB = new Take("take-B", "video-B", new(24, .5, 12), "Дубль B");
        return Reply(new
        {
            name = "QA", revision = Revision, fps = 24,
            blocks = new[] { new { id = "block", title = "Клип A", label = "QA-01", takes = new[] { takeA, takeB } } },
            timeline = new { segments = new[] { new Segment("segment", "Тест", 0, 12) }, clips = new[] { Current } }
        });
    }
    private static Task<HttpResponseMessage> Reply(object value, HttpStatusCode status = HttpStatusCode.OK) => Task.FromResult(new HttpResponseMessage(status)
    {
        Content = new StringContent(JsonSerializer.Serialize(value, new JsonSerializerOptions { PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower }), Encoding.UTF8, "application/json")
    });
}

internal sealed class HeadlessSurface : IUiRenderSurface, ITextLayoutService
{
    public ITextLayoutService TextLayout => this;
    public Size Measure(string text, float fontSize, string fontFamily) => new(text.Length * fontSize * .52f, fontSize * 1.3f);
    public void VerifyAvailable() { }
    public void Resize(UiViewport viewport) { }
    public void Render(Element root, UiViewport viewport) { }
    public void Dispose() { }
}
