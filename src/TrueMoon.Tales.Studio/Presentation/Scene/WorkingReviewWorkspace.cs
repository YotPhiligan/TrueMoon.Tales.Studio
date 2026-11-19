using TrueMoon.Alloy.Runtime;
using TrueMoon.Argentis;
using TrueMoon.Tales.Studio.Backend;
using TrueMoon.Tales.Studio.Backend.Export;
using TrueMoon.Tales.Studio.Models;

namespace TrueMoon.Tales.Studio.Presentation.Scene;

internal sealed class WorkingReviewWorkspace : Border, IUiFrameParticipant
{
    private readonly BackendConnection _backend;
    private readonly SceneSnapshot _scene;
    private readonly Action<BackendRenderedMontage, bool> _open;
    private readonly Action _close;
    private readonly CancellationTokenSource _closing = new();
    private readonly string _key = "preview-" + Guid.NewGuid().ToString("N");
    private readonly Text _status = new();
    private readonly ProgressBar _progress = new() { Height = 6, AccessibleName = "Подготовка CPU" };
    private readonly Button _segment, _project, _cancel, _back, _inspect;
    private BackendExportStatus? _job;
    private Task<BackendExportStatus>? _pending;
    private TimeSpan _elapsed;
    private bool _unresolved;
    private bool Active => _job?.State is "running" or "stopping";
    public bool NeedsFrame => _pending is not null || Active && !_unresolved;

    internal WorkingReviewWorkspace(BackendConnection backend, SceneSnapshot scene, Action<BackendRenderedMontage, bool> open, Action close)
    {
        _backend = backend; _scene = scene; _open = open; _close = close;
        Padding = new Thickness(16, 44, 16, 16);
        _segment = Button("Смотреть сегмент", () => View(true));
        _project = Button("Смотреть проект", () => View(false));
        _cancel = Button("Отменить подготовку", Cancel);
        _back = Button("К монтажу", Back);
        _inspect = Button("Проверить статус", Inspect);
        SetContent(VStack.Create().Configure(stack => stack.Spacing = 12).WithChildren(
            Text.Plain("Рабочий предпросмотр"), _status, _progress,
            HStack.Create().Configure(row => row.Spacing = 8).WithChildren(_segment, _project),
            HStack.Create().Configure(row => row.Spacing = 8).WithChildren(_cancel, _inspect, _back)));
        Start("Готовлю сохранённый монтаж…", () => backend.StartWorkingReviewAsync(_key, scene.Revision, scene.Fingerprint!, _closing.Token));
    }

    internal bool CanClose => _pending is null && !Active && !_unresolved;
    internal void Back()
    {
        if (CanClose) _close();
        else if (_job?.State == "running" && _pending is null) Cancel();
        else Show(_unresolved ? "Проверьте статус подготовки." : "Дождитесь завершения или отмены.", true);
    }

    private void View(bool segment)
    {
        if (_job is not { State: "completed" } job || !CanClose) return;
        var key = BackendRenderedMontage.Identifier(job.Id, segment ? _scene.Segment.StartFrame : null);
        _open(new(key, segment ? _scene.Segment.StartFrame : 0, segment ? _scene.Segment.DurationFrames : job.Frames,
            job.Fps, _scene.Revision, job.ProjectFingerprint), segment);
    }

    private void Inspect()
    {
        var id = _job?.Id ?? BackendExportJobs.JobId(_key);
        Start("Проверяю статус…", () => _backend.WorkingReviewStatusAsync(id, _closing.Token));
    }

    private void Cancel()
    {
        if (_job is { State: "running" } job)
            Start("Отменяю подготовку…", () => _backend.CancelWorkingReviewAsync(job.Id, job.Revision, _closing.Token));
    }

    private void Start(string status, Func<Task<BackendExportStatus>> run)
    {
        if (_pending is not null) return;
        Show(status);
        _pending = run();
        Refresh();
    }

    public void AdvanceFrame(TimeSpan elapsed, IInputContext context)
    {
        if (_pending is { IsCompleted: true } complete)
        {
            _pending = null;
            try
            {
                _job = complete.GetAwaiter().GetResult();
                _unresolved = false;
                Show(_job.State switch
                {
                    "completed" => $"Предпросмотр готов · ревизия {_job.ProjectRevision}",
                    "running" => $"CPU · {_job.ProgressPercent ?? 0}%",
                    "stopping" => "Останавливаю CPU…",
                    "cancelled" => "Подготовка отменена.",
                    _ => "Подготовка не удалась · " + _job.ErrorCode
                }, _job.State == "failed");
            }
            catch (BackendCommandException error)
            {
                _unresolved = error.Code != "export_not_found" && _job is { State: "running" or "stopping" };
                if (error.Code == "export_not_found") _job = null;
                Show(error.Code switch
                {
                    "invalid_export" => "Проверьте диапазоны, назначенные дубли, звуковые потоки и шрифты.",
                    "export_busy" => "CPU уже занят экспортом или предпросмотром.",
                    "revision_conflict" => "Сохранённый монтаж изменился. Вернитесь к монтажу.",
                    "export_conflict" => "Статус подготовки изменился. Проверьте его.",
                    "export_not_found" => "Подготовка не была принята.",
                    _ => error.Message
                }, true);
            }
            catch (Exception error) when (error is IOException or OperationCanceledException or System.Text.Json.JsonException)
            {
                _unresolved = true;
                Show(error.Message, true);
            }
            _elapsed = TimeSpan.Zero;
            Refresh();
        }
        if (_pending is null && Active && !_unresolved && (_elapsed += elapsed) >= TimeSpan.FromMilliseconds(250)) Inspect();
    }

    private void Refresh()
    {
        var ready = _pending is null && _job?.State == "completed";
        _segment.IsEnabled = _project.IsEnabled = ready;
        _cancel.IsEnabled = _pending is null && _job?.State == "running";
        _inspect.IsEnabled = _pending is null && _unresolved;
        _back.IsEnabled = CanClose;
        _progress.IsVisible = Active;
        _progress.Value = (_job?.ProgressPercent ?? 0) / 100f;
    }

    private void Show(string status, bool error = false) { _status.Value = status; _status.HasError = error; }
    private static Button Button(string label, Action action) => TrueMoon.Argentis.Button.Simple(label).WithRole(SceneView.CommandRole).OnClick(action);

    public override void Dispose()
    {
        _closing.Cancel();
        if (_pending is { } pending)
            _ = pending.ContinueWith(task => _ = task.Exception, CancellationToken.None, TaskContinuationOptions.OnlyOnFaulted, TaskScheduler.Default);
        _closing.Dispose();
        base.Dispose();
    }
}
