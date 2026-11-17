using SkiaSharp;
using System.Text.Json;
using TrueMoon.Alloy.Runtime;
using TrueMoon.Argentis;
using TrueMoon.Tales.Studio.Tests.Support;
using Xunit;

namespace TrueMoon.Tales.Studio.Tests.Raster;

public sealed class RasterHostTests
{
    [Fact]
    public void Host_PublishesResizeFocusAndSuspension_AndRejectsInvalidResize()
    {
        using var host = new RasterWindow<TestWindow>(new(), "host-state", 320, 200);
        var window = host.Window;
        Assert.True(window.IsHosted); Assert.True(window.IsActive);
        Assert.Equal(new Size(320, 200), window.ActualClientSize);
        host.Resize(400, 240, 1.5f);
        Assert.Equal(new UiViewport(600, 360, 1.5f), host.Session.Viewport);
        Assert.Equal(new Size(400, 240), window.ActualClientSize);
        RasterAssert.Dimensions(host.Capture("resized", new { }), 600, 360);
        Assert.Throws<ArgumentOutOfRangeException>(() => host.Resize(1, 1, float.NaN));
        Assert.Equal(new UiViewport(600, 360, 1.5f), host.Session.Viewport);
        Assert.Equal(new Size(400, 240), window.ActualClientSize);
        window.Minimize();
        Assert.Equal(WindowPresentationState.Minimized, window.State);
        Assert.False(window.IsActive); Assert.True(host.Session.Viewport.IsEmpty);
        Assert.Equal(new Size(0, 0), window.ActualClientSize);
        window.Restore();
        Assert.Equal(WindowPresentationState.Normal, window.State);
        Assert.True(window.IsActive); Assert.False(host.Session.Viewport.IsEmpty);
        window.Maximize(); Assert.Equal(WindowPresentationState.Maximized, window.State);
        RasterAssert.Dimensions(host.Capture("restored", new { }), 600, 360);
    }

    [Fact]
    public async Task Close_AwaitsEachCallback_VetoesThenRetries_AndDisposesBeforeCompletion()
    {
        using var host = new RasterWindow<TestWindow>(new(), "host-close", 320, 200);
        var owner = Environment.CurrentManagedThreadId;
        var gate = new TaskCompletionSource<bool>();
        var calls = new List<int>();
        host.Window.Closing += reason =>
        {
            Assert.Equal(owner, Environment.CurrentManagedThreadId);
            Assert.Equal(WindowCloseReason.Application, reason);
            calls.Add(1); return gate.Task;
        };
        host.Window.Closing += _ => { calls.Add(2); return Task.FromResult(true); };
        var veto = host.Window.RequestCloseAsync();
        Assert.False(veto.IsCompleted); Assert.Equal(new[] { 1 }, calls);
        gate.SetResult(false); host.Update();
        Assert.True(veto.IsCompletedSuccessfully); Assert.False(await veto);
        Assert.False(host.Window.IsDisposed); Assert.False(host.Closed);
        Assert.Equal(new[] { 1 }, calls);
        gate = new();
        var accepted = host.Window.RequestCloseAsync();
        Assert.False(accepted.IsCompleted);
        gate.SetResult(true); host.Update();
        Assert.True(accepted.IsCompletedSuccessfully); Assert.True(await accepted);
        Assert.Equal(new[] { 1, 1, 2 }, calls);
        Assert.True(host.Closed); Assert.True(host.Window.IsDisposed);
        Assert.True(host.Session.IsDisposed); Assert.False(host.Window.IsHosted);
        Assert.Throws<ObjectDisposedException>(() => host.Update());
        host.Dispose();
    }

    [Fact]
    public async Task Close_CallbackFailureKeepsHostAlive_AndCancellationOnlyCancelsWait()
    {
        using var host = new RasterWindow<TestWindow>(new(), "host-close-error", 320, 200);
        var gate = new TaskCompletionSource<bool>();
        host.Window.Closing += _ => gate.Task;
        var failed = host.Window.RequestCloseAsync();
        gate.SetException(new IOException("close failed")); host.Update();
        Assert.True(failed.IsFaulted);
        Assert.Equal("close failed", (await Assert.ThrowsAsync<IOException>(() => failed)).Message);
        Assert.False(host.Window.IsDisposed); Assert.False(host.Closed);
        gate = new();
        using var cancel = new CancellationTokenSource();
        var waiter = host.Window.RequestCloseAsync(cancel.Token);
        var otherWaiter = host.Window.RequestCloseAsync();
        cancel.Cancel();
        Assert.True(waiter.IsCanceled); Assert.False(otherWaiter.IsCompleted);
        Assert.False(host.Window.IsDisposed);
        gate.SetResult(true); host.Update();
        Assert.True(otherWaiter.IsCompletedSuccessfully); Assert.True(await otherWaiter);
        Assert.True(host.Window.IsDisposed); Assert.True(host.Session.IsDisposed);
    }

    [Fact]
    public void Dispose_CancelsPendingClose_AndRejectsFurtherInput()
    {
        using var host = new RasterWindow<TestWindow>(new(), "host-dispose", 320, 200);
        host.Window.Closing += _ => new TaskCompletionSource<bool>().Task;
        var pending = host.Window.RequestCloseAsync();
        host.Dispose();
        Assert.True(pending.IsCanceled);
        Assert.True(host.Window.IsDisposed); Assert.True(host.Session.IsDisposed);
        Assert.False(host.Closed);
        Assert.Throws<ObjectDisposedException>(() => host.Input(new(InputKind.KeyDown, Key: UiKey.Escape)));
        Assert.Throws<ObjectDisposedException>(() => host.Resize(100, 100, 1));
        if (string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable("STUDIO_RASTER_EVIDENCE")))
            Assert.False(Directory.Exists(host.EvidenceDirectory));
        else
        {
            using var manifest = JsonDocument.Parse(File.ReadAllText(Path.Combine(host.EvidenceDirectory, "manifest.json")));
            Assert.True(manifest.RootElement.GetProperty("root_disposed").GetBoolean());
            Assert.True(manifest.RootElement.GetProperty("session_disposed").GetBoolean());
            Assert.False(manifest.RootElement.GetProperty("closed").GetBoolean());
        }
    }

    [Fact]
    public void StartupFailure_DisposesAdoptedTreeAndDisconnectsHost()
    {
        var window = new TestWindow();
        window.HostStateChanged += _ => throw new IOException("failed host publication");
        Assert.Throws<IOException>(() => new RasterWindow<TestWindow>(window, "host-start-failed", 320, 200));
        Assert.True(window.IsDisposed); Assert.False(window.IsHosted);
    }

    [Fact]
    public void Capture_RejectsPathTraversalAndOverwrite_AndFontsContainCyrillic()
    {
        using var host = new RasterWindow<TestWindow>(new(), "host-capture", 320, 200);
        var first = host.Capture("frame", new { });
        Assert.Throws<ArgumentException>(() => host.Capture("../escape", new { }));
        Assert.Throws<IOException>(() => host.Capture("frame", new { }));
        Assert.Equal(first, File.ReadAllBytes(Path.Combine(host.EvidenceDirectory, "frame.png")));
        using var typeface = SKTypeface.FromFile(Path.Combine(AppContext.BaseDirectory, "Assets", "Fonts", "NotoSans-Regular.ttf"));
        using var font = new SKFont(typeface, 20);
        Assert.True(font.ContainsGlyphs("Сказки истинной луны Ёж перед рассветом"));
    }

    private sealed class TestWindow : Window<object>
    {
        internal TestWindow()
        {
            Content(null, _ => Text.Plain("Тест окна — Ёж перед рассветом"));
        }
    }
}
