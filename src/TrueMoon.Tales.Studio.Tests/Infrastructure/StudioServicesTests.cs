using System.Net;
using TrueMoon.Argentis;
using TrueMoon.Cobalt;
using TrueMoon.Tales.Studio.Configuration;
using TrueMoon.Tales.Studio.Infrastructure;
using TrueMoon.Tales.Studio.Media;
using TrueMoon.Tales.Studio.Presentation.Scene;
using TrueMoon.Tales.Studio.Presentation.Shell;
using Xunit;

namespace TrueMoon.Tales.Studio.Tests.Infrastructure;

public sealed class StudioServicesTests
{
    [Fact]
    public async Task Cobalt_ConstructsGeneratedStudioGraphAsSingletons()
    {
        await using var app = CreateApp();
        var resolver = Assert.IsType<CobaltServiceResolver>(app.Services);

        var model = resolver.Resolve<SceneViewModel>();
        Assert.Same(model, resolver.Resolve<SceneViewModel>());
        var api = resolver.Resolve<StudioHttpClient>();
        Assert.Same(api, resolver.Resolve<StudioHttpClient>());
        Assert.Equal("http://127.0.0.1:12345/api/media?project=qa&asset=video%20A", api.Media("video A").AbsoluteUri);
        Assert.IsType<FrameDecoder>(resolver.Resolve<IFrameDecoder>());
        Assert.Same(resolver.Resolve<IFrameDecoder>(), resolver.Resolve<IFrameDecoder>());
        Assert.IsType<FfmpegPreview>(resolver.Resolve<IPreviewMedia>());
        Assert.Same(resolver.Resolve<IPreviewMedia>(), resolver.Resolve<IPreviewMedia>());
        Assert.IsType<SceneViewStyle>(resolver.Resolve<IViewStyle<SceneView>>());
        Assert.Same(resolver.Resolve<IViewStyle<SceneView>>(), resolver.Resolve<IViewStyle<SceneView>>());
        Assert.IsType<StudioTitleBarStyle>(resolver.Resolve<IViewStyle<StudioTitleBar>>());
        Assert.Same(resolver.Resolve<IViewStyle<StudioTitleBar>>(), resolver.Resolve<IViewStyle<StudioTitleBar>>());
        Assert.IsType<SceneViewFactory>(resolver.Resolve<IFactory<SceneView>>());
        Assert.Same(resolver.Resolve<IFactory<SceneView>>(), resolver.Resolve<IFactory<SceneView>>());
        Assert.IsType<StudioTitleBarFactory>(resolver.Resolve<IFactory<StudioTitleBar, StudioTitleBarArguments>>());
        Assert.Same(resolver.Resolve<IFactory<StudioTitleBar, StudioTitleBarArguments>>(),
            resolver.Resolve<IFactory<StudioTitleBar, StudioTitleBarArguments>>());

        await app.StartAsync();
        await app.StopAsync();
    }

    [Fact]
    public void Cobalt_ViewFactoriesShareModelAndLeaveItsLifetimeToApp()
    {
        var handler = new StudioHandler();
        using var app = CreateApp(handler);
        var resolver = Assert.IsType<CobaltServiceResolver>(app.Services);
        var model = resolver.Resolve<SceneViewModel>();
        var factory = resolver.Resolve<IFactory<SceneView>>();

        using (var first = Assert.IsType<SceneView>(factory.Create()))
        using (var second = Assert.IsType<SceneView>(factory.Create()))
        {
            using var firstHeader = first.CreateTitleBar(() => { });
            using var secondHeader = second.CreateTitleBar(() => { });
            Assert.NotSame(first, second);
            Assert.NotSame(firstHeader, secondHeader);
            Assert.Same(model, first.DataContext);
            Assert.Same(model, second.DataContext);
            Assert.Same(model, firstHeader.DataContext);
            Assert.Same(model, secondHeader.DataContext);
            Assert.Single(handler.Requests);
        }

        model.Poll();
        model.Open();
        model.Poll();
        Assert.Equal(2, handler.Requests.Count);
        Assert.All(handler.Requests, request => Assert.Equal(HttpMethod.Get, request.Method));
        Assert.False(handler.Disposed);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Cobalt_DisposalCancelsModelWorkAndDisposesOwnedHttpOnce(bool asynchronously)
    {
        var handler = new StudioHandler { Block = true };
        await using var app = CreateApp(handler);
        var resolver = Assert.IsType<CobaltServiceResolver>(app.Services);
        var api = resolver.Resolve<StudioHttpClient>();
        resolver.Resolve<SceneViewModel>().Open();
        var request = Assert.Single(handler.Requests);
        Assert.False(request.Token.IsCancellationRequested);

        if (asynchronously) await app.DisposeAsync();
        else app.Dispose();

        Assert.True(request.Token.IsCancellationRequested);
        Assert.Throws<ObjectDisposedException>(() => resolver.Resolve<SceneViewModel>());
        Assert.Equal(1, handler.DisposeCount);
        await Assert.ThrowsAsync<ObjectDisposedException>(() => api.OpenAsync(null, CancellationToken.None));
        Assert.Single(handler.Requests);
        api.Dispose();
        Assert.Equal(1, handler.DisposeCount);
    }

    [Fact]
    public void HttpClient_ConstructorFailureDisposesOwnedHandler()
    {
        var handler = new StudioHandler();
        var invalid = Options with { Endpoint = new("relative", UriKind.Relative) };
        Assert.Throws<ArgumentException>(() => new StudioHttpClient(invalid, handler));
        Assert.Equal(1, handler.DisposeCount);
        Assert.Empty(handler.Requests);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task RunStudio_OwnsHttpOnSuccessAndStartupFailure(bool failStartup)
    {
        var handler = new StudioHandler();
        var starts = new List<Uri>();
        var builder = App.Builder(_ => { }).Setup(app => app.UseStudioServices().Services(services => services
            .Singleton<StudioHttpClient>(resolver => new(resolver.Resolve<StudioOptions>(), handler))
            .Singleton<IStartable>(resolver => new FinishStartup(resolver.Resolve<StudioHttpClient>(),
                resolver.Resolve<IAppLifetime>(), failStartup, starts.Add))));
        var args = new[] { "http://127.0.0.1:12345/", "qa", typeof(StudioOptions).Assembly.Location };

        if (failStartup)
        {
            var error = await Assert.ThrowsAsync<InvalidOperationException>(() => builder.RunStudioAsync(args));
            Assert.Equal("Startup failure", error.Message);
        }
        else Assert.Equal(0, await builder.RunStudioAsync(args));

        Assert.Equal("http://127.0.0.1:12345/api/media?project=qa&asset=startup", Assert.Single(starts).AbsoluteUri);
        Assert.Equal(1, handler.DisposeCount);
        Assert.Empty(handler.Requests);
    }

    private static readonly StudioOptions Options = new(new("http://127.0.0.1:12345/"), "qa", "unused-ffmpeg");

    private static IApp CreateApp(StudioHandler? handler = null) => App.Build(app =>
    {
        app.Services(services => services.Instance(Options)).UseStudioServices();
        if (handler is not null)
            app.Services(services => services.Singleton<StudioHttpClient>(_ => new(Options, handler)));
    });

    private sealed class FinishStartup(StudioHttpClient api, IAppLifetime lifetime, bool fail, Action<Uri> started) : IStartable
    {
        public Task StartAsync(CancellationToken cancellationToken = default)
        {
            started(api.Media("startup"));
            if (fail) throw new InvalidOperationException("Startup failure");
            lifetime.Cancel();
            return Task.CompletedTask;
        }
    }

    private sealed class StudioHandler : HttpMessageHandler
    {
        public bool Block;
        public int DisposeCount { get; private set; }
        public bool Disposed => DisposeCount != 0;
        public List<(HttpMethod Method, CancellationToken Token)> Requests { get; } = [];

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken token)
        {
            Requests.Add((request.Method, token));
            return Block ? UntilCancelled(token) : Task.FromResult(new HttpResponseMessage(HttpStatusCode.ServiceUnavailable)
            {
                Content = new StringContent("{\"error\":\"Изолированная DI-проверка\"}")
            });
        }

        private static async Task<HttpResponseMessage> UntilCancelled(CancellationToken token)
        {
            var completion = new TaskCompletionSource<HttpResponseMessage>(TaskCreationOptions.RunContinuationsAsynchronously);
            using var registration = token.Register(() => completion.TrySetCanceled(token));
            return await completion.Task;
        }

        protected override void Dispose(bool disposing) { DisposeCount++; base.Dispose(disposing); }
    }
}
