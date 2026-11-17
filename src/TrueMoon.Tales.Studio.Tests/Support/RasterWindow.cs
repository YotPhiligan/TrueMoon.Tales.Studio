using System.Diagnostics;
using System.Reflection;
using System.Security.Cryptography;
using System.Text.Json;
using SkiaSharp;
using TrueMoon.Alloy.Rendering.Skia;
using TrueMoon.Alloy.Runtime;
using TrueMoon.Argentis;

namespace TrueMoon.Tales.Studio.Tests.Support;

// A test-only owner loop/window controller. No HWND, OS input, GPU or background threads.
internal sealed class RasterWindow<TWindow> : IWindowController, IDisposable where TWindow : Window
{
    private readonly WindowHostConnection _connection;
    private readonly List<object> _frames = [];
    private readonly List<UiInput> _inputs = [];
    private readonly bool _retain;
    private Size _size;
    private float _scale;
    private bool _active = true, _disposed;
    private WindowPresentationState _state = WindowPresentationState.Normal;
    private TaskCompletionSource<bool>? _close;
    private Func<WindowCloseReason, Task<bool>>[] _handlers = [];
    private int _handlerIndex;
    private Task<bool>? _handler;

    internal TWindow Window { get; }
    internal UiSession Session { get; }
    internal string EvidenceDirectory { get; }
    internal bool Closed { get; private set; }

    internal RasterWindow(TWindow window, string scenario, float width, float height, float scale = 1)
    {
        ValidateName(scenario);
        var viewport = CreateViewport(width, height, scale);
        Window = window;
        _size = new(width, height); _scale = scale;
        var requested = Environment.GetEnvironmentVariable("STUDIO_RASTER_EVIDENCE");
        _retain = !string.IsNullOrWhiteSpace(requested);
        var parent = _retain ? Path.GetFullPath(requested!) : Path.GetTempPath();
        EvidenceDirectory = Path.Combine(parent, "studio-raster-" + scenario + "-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(EvidenceDirectory);
        try { _connection = window.AttachHost(this); }
        catch { DeleteTemporaryEvidence(); throw; }
        UiSession? adopted = null;
        try
        {
            Session = adopted = UiSession.Create(window, new SkiaRasterRenderBackend(RasterFonts.Catalog), new RasterUiTarget(), viewport);
            Publish();
        }
        catch
        {
            try { _connection.Dispose(); }
            finally
            {
                try { if (adopted is not null) adopted.Dispose(); else window.Dispose(); }
                finally { DeleteTemporaryEvidence(); }
            }
            throw;
        }
    }

    private static void ValidateName(string name)
    {
        if (string.IsNullOrWhiteSpace(name) || name.Any(c => !char.IsAsciiLetterOrDigit(c) && c != '-'))
            throw new ArgumentException("Expected a nonempty ASCII evidence name.");
    }

    private static UiViewport CreateViewport(float width, float height, float scale)
    {
        if (!float.IsFinite(width) || !float.IsFinite(height) || width <= 0 || height <= 0 ||
            !float.IsFinite(scale) || scale <= 0)
            throw new ArgumentOutOfRangeException(nameof(width));
        return new(checked((int)Math.Round(width * scale)), checked((int)Math.Round(height * scale)), scale);
    }

    private UiViewport Viewport() => _state == WindowPresentationState.Minimized
        ? new(0, 0, _scale) : CreateViewport(_size.Width, _size.Height, _scale);

    private void Publish() => _connection.Synchronize(_state, _active,
        _state == WindowPresentationState.Minimized ? new(0, 0) : _size);

    internal void Update()
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        AdvanceClose();
        if (!_disposed) Session.Update();
    }

    internal void PumpUntil(Func<bool> ready, TimeSpan? timeout = null)
    {
        var watch = Stopwatch.StartNew();
        while (!ready())
        {
            if (watch.Elapsed > (timeout ?? TimeSpan.FromSeconds(5))) throw new TimeoutException("Raster UI condition did not become ready.");
            Update(); Thread.Yield();
        }
        Update();
    }

    internal UiInputResult Input(UiInput input)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        _inputs.Add(input);
        var result = Session.HandleInput(input);
        Update();
        return result;
    }

    internal void Click(Element element)
    {
        var bounds = element.Bounds;
        if (bounds.Width <= 0 || bounds.Height <= 0) throw new InvalidOperationException("Click target has no geometry.");
        var x = bounds.X + bounds.Width / 2; var y = bounds.Y + bounds.Height / 2;
        Input(new(InputKind.PointerDown, x, y)); Input(new(InputKind.PointerUp, x, y));
    }

    internal void Resize(float width, float height, float scale)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        var viewport = CreateViewport(width, height, scale);
        Session.Resize(_state == WindowPresentationState.Minimized ? new(0, 0, scale) : viewport);
        _size = new(width, height); _scale = scale;
        Publish(); Update();
    }

    internal void SetActive(bool active)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        _active = active; Publish();
        if (!active) Input(new(InputKind.FocusLost));
    }

    public void Minimize()
    {
        SetActive(false); _state = WindowPresentationState.Minimized;
        Session.Resize(Viewport()); Publish(); Update();
    }
    public void Maximize() { ObjectDisposedException.ThrowIf(_disposed, this); _state = WindowPresentationState.Maximized; Session.Resize(Viewport()); Publish(); Update(); }
    public void Restore() { ObjectDisposedException.ThrowIf(_disposed, this); _state = WindowPresentationState.Normal; Session.Resize(Viewport()); _active = true; Publish(); Update(); }

    // Poll asynchronous callbacks on the owner; cancellation cancels the wait, not an accepted attempt.
    public Task<bool> RequestCloseAsync(CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        cancellationToken.ThrowIfCancellationRequested();
        if (_close is null)
        {
            _handlers = _connection.SnapshotClosingHandlers(); _handlerIndex = 0; _handler = null;
            _close = new(TaskCreationOptions.RunContinuationsAsynchronously);
        }
        var result = _close.Task;
        AdvanceClose();
        return result.WaitAsync(cancellationToken);
    }

    private void AdvanceClose()
    {
        if (_close is not { } completion) return;
        try
        {
            while (_handlerIndex < _handlers.Length)
            {
                _handler ??= _handlers[_handlerIndex](WindowCloseReason.Application);
                if (!_handler.IsCompleted) return;
                if (!_handler.GetAwaiter().GetResult()) { _close = null; _handler = null; completion.SetResult(false); return; }
                _handler = null; _handlerIndex++;
            }
            _close = null; Closed = true;
            Dispose(); completion.SetResult(true);
        }
        catch (Exception error) { _close = null; _handler = null; completion.TrySetException(error); }
    }

    internal byte[] Capture(string name, object state)
    {
        ValidateName(name);
        Update();
        using var image = Session.SnapshotRasterImage();
        using var png = image.Encode(SKEncodedImageFormat.Png, 100);
        var bytes = png.ToArray();
        using (var file = new FileStream(Path.Combine(EvidenceDirectory, name + ".png"), FileMode.CreateNew)) file.Write(bytes);
        _frames.Add(new { name, width = image.Width, height = image.Height, scale = _scale,
            sha256 = Convert.ToHexString(SHA256.HashData(bytes)), state });
        return bytes;
    }

    internal static IEnumerable<Element> Walk(Element element) => new[] { element }.Concat(element.Children.SelectMany(Walk));

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _close?.TrySetCanceled(); _close = null; _handler = null; _handlers = [];
        try { _connection.Dispose(); }
        finally
        {
            try { Session.Dispose(); }
            finally
            {
                try
                {
                    var manifest = new { schema = 1, renderer = "Skia CPU raster", product_version = typeof(TWindow).Assembly
                        .GetCustomAttribute<AssemblyInformationalVersionAttribute>()!.InformationalVersion, framework_version = typeof(UiSession).Assembly
                        .GetCustomAttribute<AssemblyInformationalVersionAttribute>()!.InformationalVersion,
                        runtime = System.Runtime.InteropServices.RuntimeInformation.FrameworkDescription,
                        fonts = RasterFonts.Hashes, native_sha256 = RasterFonts.NativeHash, frames = _frames, input = _inputs,
                        root_disposed = Window.IsDisposed, session_disposed = Session.IsDisposed, closed = Closed };
                    File.WriteAllText(Path.Combine(EvidenceDirectory, "manifest.json"), JsonSerializer.Serialize(manifest, new JsonSerializerOptions { WriteIndented = true }));
                }
                finally { DeleteTemporaryEvidence(); }
            }
        }
    }

    private void DeleteTemporaryEvidence()
    {
        if (_retain) return;
        var path = Path.GetFullPath(EvidenceDirectory);
        var root = Path.TrimEndingDirectorySeparator(Path.GetFullPath(Path.GetTempPath()));
        if (!string.Equals(Path.GetDirectoryName(path), root, StringComparison.OrdinalIgnoreCase) ||
            !Path.GetFileName(path).StartsWith("studio-raster-", StringComparison.Ordinal))
            throw new InvalidOperationException("Refusing to remove evidence outside the allocated temp root.");
        if (Directory.Exists(path)) Directory.Delete(path, true);
    }
}

internal static class RasterFonts
{
    internal static readonly IReadOnlyDictionary<string, string> Hashes = new Dictionary<string, string>
    {
        ["NotoSans-Regular.ttf"] = "B85C38ECEA8A7CFB39C24E395A4007474FA5A4FC864F6EE33309EB4948D232D5",
        ["NotoSansSymbols-Regular.ttf"] = "8F02F31959BBDF6061547A188248E13F84DC5FDD940326EC494675F453F072BB"
    };
    private static readonly Lazy<SkiaFontCatalog> Fonts = new(Load);
    internal static SkiaFontCatalog Catalog => Fonts.Value;
    internal static string NativeHash
    {
        get
        {
            using var process = Process.GetCurrentProcess();
            var path = process.Modules.Cast<ProcessModule>().Single(module =>
                string.Equals(module.ModuleName, "libSkiaSharp.dll", StringComparison.OrdinalIgnoreCase)).FileName;
            return Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(path)));
        }
    }
    private static SkiaFontCatalog Load()
    {
        var root = Path.Combine(AppContext.BaseDirectory, "Assets", "Fonts");
        foreach (var (name, hash) in Hashes)
            if (Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(Path.Combine(root, name)))) != hash)
                throw new InvalidDataException("Fixed test font differs: " + name);
        return SkiaFontCatalog.Load("Studio Test", new Dictionary<string, string>
        {
            ["Studio Test"] = Path.Combine(root, "NotoSans-Regular.ttf"),
            ["Segoe UI Symbol"] = Path.Combine(root, "NotoSansSymbols-Regular.ttf")
        });
    }
}
