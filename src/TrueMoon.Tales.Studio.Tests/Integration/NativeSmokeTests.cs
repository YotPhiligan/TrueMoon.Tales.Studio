using System.Text.Json;
using TrueMoon.Alloy.Hosting;
using TrueMoon.Cobalt;
using TrueMoon.Tales.Studio.Configuration;
using TrueMoon.Tales.Studio.Infrastructure;
using TrueMoon.Tales.Studio.Presentation;
using TrueMoon.Tales.Studio.Presentation.Scene;
using Xunit;

namespace TrueMoon.Tales.Studio.Tests.Integration;

public sealed class NativeQaFactAttribute : FactAttribute
{
    public NativeQaFactAttribute()
    {
        if (Environment.GetEnvironmentVariable("STUDIO_NATIVE_QA_ENDPOINT") is null)
            Skip = "Requires explicit isolated native QA endpoint and interactive Windows desktop";
    }
}

[CollectionDefinition("Native QA", DisableParallelization = true)]
public sealed class NativeQaCollection;

[Collection("Native QA")]
public sealed class NativeSmokeTests
{
    [NativeQaFact]
    [Trait("Category", "NativeSmoke")]
    public async Task Native_RepeatedStartupAndClose_ReleasesWindowDuringLoadAndAfterVideo()
    {
        var endpoint = Environment.GetEnvironmentVariable("STUDIO_NATIVE_QA_ENDPOINT")!;
        var ffmpeg = Environment.GetEnvironmentVariable("STUDIO_NATIVE_QA_FFMPEG")!;
        var run = Path.GetFullPath(Environment.GetEnvironmentVariable("STUDIO_NATIVE_QA_RUN")!);
        if (!run.Contains(Path.Combine(".local", "qa-sessions"), StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException("QA folder required");
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(65));
        var rows = new List<object>();
        foreach (var waitForVideo in new[] { false, true })
        for (var iteration = 0; iteration < 5; iteration++)
        {
            var options = new StudioOptions(new(endpoint), "argentis-qa", ffmpeg);
            await using var app = App.Build(context => context.Services(services => services.Instance(options))
                .UseStudioServices().UseStudioWindow());
            var resolver = Assert.IsType<CobaltServiceResolver>(app.Services);
            var window = resolver.Resolve<HostedUiWindow>();
            var model = resolver.Resolve<SceneViewModel>();
            var shown = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            void Presented(TrueMoon.Alloy.Runtime.UiSession session)
            {
                if (model.Pixels is { Length: > 0 }) shown.TrySetResult();
            }
            window.FramePresented += Presented;
            try
            {
                await app.StartAsync(deadline.Token);
                if (waitForVideo) await shown.Task.WaitAsync(TimeSpan.FromSeconds(10), deadline.Token);
                if (iteration % 2 == 0)
                {
                    GC.Collect(2, GCCollectionMode.Forced, true, true);
                    GC.WaitForPendingFinalizers();
                }
                await app.StopAsync(deadline.Token);
                Assert.True(window.Completion.IsCompletedSuccessfully);
                if (waitForVideo) Assert.True(window.PresentedFrames > 0);
                rows.Add(new { wait_for_video = waitForVideo, iteration, frames = window.PresentedFrames, closed = window.IsClosed });
            }
            finally { window.FramePresented -= Presented; }
        }
        {
            var options = new StudioOptions(new(endpoint), "argentis-qa", ffmpeg);
            await using var app = App.Build(context => context.Services(services => services.Instance(options))
                .UseStudioServices().UseStudioWindow());
            var resolver = Assert.IsType<CobaltServiceResolver>(app.Services);
            var window = resolver.Resolve<HostedUiWindow>();
            var model = resolver.Resolve<SceneViewModel>();
            await app.StartAsync(deadline.Token);
            for (var step = 0; step < 400; step++)
            {
                window.Resize(1280 + step % 2, 820);
                if (step % 5 == 0) window.PostWindow(_ => { if (model.CanSeek) model.Step(model.CanNext ? 1 : -1); });
                if (step % 50 == 0) GC.Collect(2, GCCollectionMode.Forced, true, true);
                await Task.Delay(100, deadline.Token);
            }
            await app.StopAsync(deadline.Token);
            Assert.True(window.Completion.IsCompletedSuccessfully);
            Assert.True(window.PresentedFrames > 30);
            rows.Add(new { sustained_resize_and_seek = true, frames = window.PresentedFrames, closed = window.IsClosed });
        }
        Assert.Equal(11, rows.Count);
        await File.WriteAllTextAsync(Path.Combine(run, "native-lifecycle.json"), JsonSerializer.Serialize(rows), deadline.Token);
    }
}
