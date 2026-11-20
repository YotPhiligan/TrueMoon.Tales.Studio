using TrueMoon.Argentis;
using TrueMoon.Tales.Studio.Backend;

namespace TrueMoon.Tales.Studio.Presentation.Scenario;

internal sealed partial class ScenarioWorkspace
{
    private VStack _proxyPanel = null!;
    private Button _proxyAuto = null!, _proxyManual = null!, _proxyRead = null!, _proxyCancel = null!, _proxyOriginal = null!;
    private readonly Text _proxyInfo = new();
    private ProxyStatus? _proxyStatus;
    private string? _proxyBaseline;
    private TimeSpan _proxyElapsed;
    private bool _proxyExpanded;
    private bool _proxyPolling;

    private bool MatchesProxy => _proxyStatus is { } status && Take?.VideoAssetId == status.AssetId &&
        _proxyBaseline == _baseline?.Fingerprint;
    private bool TrackingProxy => _proxyExpanded && _proxyPolling && !_stale && !HasDraft && _pane is null or Pane.Library &&
        MatchesProxy && _proxyStatus?.State == "running";

    private Button CreateProxyToggle() => ButtonFor("Прокси", () =>
    {
        _proxyExpanded = !_proxyExpanded;
        RefreshCommands();
    });

    private VStack CreateProxyPanel()
    {
        _proxyAuto = ButtonFor("Подготовить прокси 4K", () => UseProxy("prepare_4k"));
        _proxyManual = ButtonFor("Подготовить прокси вручную", () => UseProxy("prepare_manual"));
        _proxyRead = ButtonFor("Проверить прокси", () => UseProxy("status"));
        _proxyCancel = ButtonFor("Отменить подготовку прокси", () => UseProxy("cancel"));
        _proxyOriginal = ButtonFor("Использовать оригинал", () => UseProxy("original"));
        return _proxyPanel = Stack(Caption("Прокси предпросмотра"), _proxyAuto, _proxyManual, _proxyRead,
            _proxyCancel, _proxyOriginal, _proxyInfo);
    }

    private void RefreshProxy(bool ready)
    {
        _proxyPanel.IsVisible = _proxyExpanded && Take?.VideoAssetId is not null;
        var status = MatchesProxy ? _proxyStatus : null;
        var running = status?.State == "running";
        _proxyAuto.IsEnabled = _proxyManual.IsEnabled = ready && !running;
        _proxyRead.IsEnabled = ready;
        _proxyCancel.IsVisible = running;
        _proxyCancel.IsEnabled = ready && running;
        _proxyOriginal.IsVisible = status?.State == "ready";
        _proxyOriginal.IsEnabled = ready && _proxyOriginal.IsVisible;
        _proxyInfo.Value = status is null ? "" : ProxyMessage(status);
        _proxyInfo.IsVisible = status is not null;
    }

    private void UseProxy(string operation)
    {
        if (Busy || HasDraft || _stale || _baseline is not { SchemaVersion: 2 } baseline ||
            Source is not { } source || Take is not { VideoAssetId: { } asset } take) return;
        _proxyPolling = false;
        Start("Проверяю прокси…", async token =>
        {
            var result = await _backend.TakeProxyAsync(operation, source.Id, take.Id, asset, baseline, token).ConfigureAwait(false);
            return () =>
            {
                _proxyStatus = result;
                _proxyBaseline = baseline.Fingerprint;
                _proxyPolling = result.State == "running";
                _proxyElapsed = TimeSpan.Zero;
                if (result.Error == "revision_conflict") _stale = true;
                Show("", error: false);
            };
        });
    }

    private void PollProxy(TimeSpan elapsed)
    {
        if (!TrackingProxy || (_proxyElapsed += elapsed) < TimeSpan.FromMilliseconds(400)) return;
        UseProxy("status");
    }

    private static string ProxyMessage(ProxyStatus status) => status.State switch
    {
        "running" => "Готовлю прокси на CPU…",
        "ready" when status.Receipt is { } proof => $"Прокси выбран · {proof.Proxy.Width} × {proof.Proxy.Height} · {proof.Proxy.FrameCount} к.",
        "original" => "Предпросмотр использует оригинал.",
        "cancelled" => "Подготовка прокси отменена.",
        _ => status.Error switch
        {
            "proxy_4k_required" => "Для выбора 4K нужны 3840 × 2160 или больше и GOP от двух секунд.",
            "proxy_wide_required" => "Ручной режим доступен для H.264 шириной от 1920 пикселей.",
            "proxy_unsupported" => "Проверьте кадровую сетку и формат видео.",
            "proxy_timing" => "Прокси отклонён при сверке кадров или звука.",
            "revision_conflict" => "Проект изменился. Перечитайте его перед подготовкой.",
            _ => "Подготовка прокси завершилась ошибкой."
        }
    };
}
