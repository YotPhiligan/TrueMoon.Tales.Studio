using System.Globalization;
using TrueMoon.Alloy.Runtime;
using TrueMoon.Argentis;
using TrueMoon.Tales.Studio.Backend;
using TrueMoon.Tales.Studio.Backend.Export;
using TrueMoon.Tales.Studio.Models;

namespace TrueMoon.Tales.Studio.Presentation.Scene;

internal sealed class WorkingReviewWorkspace : Border, IUiFrameParticipant
{
    private readonly BackendConnection _backend;
    private readonly Segment? _window;
    private readonly int _revision;
    private readonly string _fingerprint;
    private bool IsAnimatic => _window is null;
    private readonly Action<BackendRenderedMontage, bool> _open;
    private readonly Action _close;
    private readonly CancellationTokenSource _closing = new();
    private string? _key;
    private readonly Text _status = new();
    private readonly ProgressBar _progress = new() { Height = 6, AccessibleName = "Подготовка CPU" };
    private readonly TextBox _start = new() { Width = 88, AccessibleName = "Начало диапазона" };
    private readonly TextBox _end = new() { Width = 88, AccessibleName = "Конец диапазона (не входит)" };
    private readonly Button _prepareRange, _prepareProject, _reset, _range, _segment, _project, _cancel, _back, _inspect;
    private readonly Button _issueClip, _issueAsset;
    private readonly HStack _issueActions;
    private BackendExportStatus? _job;
    private BackendReadinessIssue? _issue;
    private Task<BackendExportStatus>? _pending;
    private TimeSpan _elapsed;
    private bool _unresolved;
    private bool Active => _job?.State is "running" or "stopping";
    public bool NeedsFrame => _pending is not null || Active && !_unresolved;

    internal WorkingReviewWorkspace(BackendConnection backend, SceneSnapshot scene, Action<BackendRenderedMontage, bool> open, Action close,
        Action<BackendReadinessIssue, bool> navigate)
        : this(backend, scene.Revision, scene.Fingerprint!, scene.Segment, open, close, navigate) { }

    internal WorkingReviewWorkspace(BackendConnection backend, int revision, string fingerprint, Segment? window,
        Action<BackendRenderedMontage, bool> open, Action close, Action<BackendReadinessIssue, bool> navigate)
    {
        _backend = backend; _window = window; _revision = revision; _fingerprint = fingerprint; _open = open; _close = close;
        Padding = new Thickness(16, 44, 16, 16);
        SetSegmentRange();
        _reset = Button("По сегменту", SetSegmentRange);
        _prepareRange = Button("Подготовить диапазон", () => Prepare(wholeProject: false));
        _prepareProject = Button(IsAnimatic ? "Подготовить аниматик" : "Подготовить проект", () => Prepare(wholeProject: true));
        _range = Button("Смотреть диапазон", () => View(false));
        _segment = Button("Смотреть сегмент", () => View(true));
        _project = Button(IsAnimatic ? "Смотреть аниматик" : "Смотреть проект", () => View(false));
        _cancel = Button("Отменить подготовку", Cancel);
        _back = Button("К монтажу", Back);
        _inspect = Button("Проверить статус", Inspect);
        _issueClip = Button("К клипу", () => OpenIssue(asset: false));
        _issueAsset = Button("К ассету", () => OpenIssue(asset: true));
        _issueActions = Row(_issueClip, _issueAsset);
        var bounds = Row(Text.Plain("Кадр начала"), _start, Text.Plain("Конец (не входит)"), _end, _reset);
        bounds.IsVisible = _prepareRange.IsVisible = !IsAnimatic;
        SetContent(VStack.Create().Configure(stack => stack.Spacing = 12).WithChildren(
            Text.Plain(IsAnimatic ? "Аниматик всей шкалы" : "Рабочий предпросмотр"), bounds,
            Row(_prepareRange, _prepareProject), _status, _issueActions, _progress,
            Row(_range, _segment, _project), Row(_cancel, _inspect, _back)));
        Refresh();

        void OpenIssue(bool asset)
        {
            if (CanClose && _issue is { } issue && issue.Matches(_revision, _fingerprint)) navigate(issue, asset);
        }
    }

    private void SetSegmentRange()
    {
        if (_window is not { } window) return;
        _start.Value = window.StartFrame.ToString(CultureInfo.InvariantCulture);
        _end.Value = ((long)window.StartFrame + window.DurationFrames).ToString(CultureInfo.InvariantCulture);
    }

    private void Prepare(bool wholeProject)
    {
        if (!CanClose) return;
        BackendReviewRange? range = null;
        if (!wholeProject)
        {
            if (!int.TryParse(_start.Value.Trim(), NumberStyles.None, CultureInfo.InvariantCulture, out var start) ||
                !int.TryParse(_end.Value.Trim(), NumberStyles.None, CultureInfo.InvariantCulture, out var end) || start >= end)
            {
                Show("Укажите два кадра проекта. Конец должен быть больше начала.", true);
                return;
            }
            range = new(start, end);
        }
        var key = "preview-" + Guid.NewGuid().ToString("N");
        _key = key;
        _job = null;
        Start("Готовлю сохранённый монтаж…", () =>
            _backend.StartWorkingReviewAsync(key, _revision, _fingerprint, _closing.Token, range, IsAnimatic));
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
        if (job.RangeStart is { } start)
        {
            _open(new(BackendRenderedMontage.Identifier(job.Id, null), start, job.Frames,
                job.Fps, _revision, job.ProjectFingerprint, Bounded: true), false);
            return;
        }
        var window = segment ? _window : null;
        var key = BackendRenderedMontage.Identifier(job.Id, window?.StartFrame);
        _open(new(key, window?.StartFrame ?? 0, window?.DurationFrames ?? job.Frames,
            job.Fps, _revision, job.ProjectFingerprint, Animatic: job.Animatic), segment);
    }

    private void Inspect()
    {
        if (_key is not { } key) return;
        var id = _job?.Id ?? BackendExportJobs.JobId(key);
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
        _issue = null;
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
                _issue = error.Issue;
                Show(_issue is { } issue ? ReadinessMessage.Explain(issue) : error.Code switch
                {
                    "invalid_export" => "Проверьте диапазоны, назначенные дубли, звуковые потоки и шрифты.",
                    "animatic_timing" => "Задайте длительность и проверьте, что планы помещаются в сохранённые окна.",
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
        _range.IsVisible = _range.IsEnabled = ready && _job!.RangeStart is not null;
        _project.IsVisible = _project.IsEnabled = ready && _job!.RangeStart is null;
        _segment.IsVisible = _segment.IsEnabled = _project.IsVisible && !IsAnimatic;
        _prepareRange.IsEnabled = _prepareProject.IsEnabled = _reset.IsEnabled = _start.IsEnabled = _end.IsEnabled = CanClose;
        _cancel.IsEnabled = _pending is null && _job?.State == "running";
        _inspect.IsEnabled = _pending is null && _unresolved;
        _back.IsEnabled = CanClose;
        var canNavigate = CanClose && _issue?.Matches(_revision, _fingerprint) == true;
        _issueClip.IsVisible = _issueClip.IsEnabled = canNavigate && _issue?.ClipId is not null;
        _issueAsset.IsVisible = _issueAsset.IsEnabled = canNavigate && _issue?.AssetId is not null;
        _issueActions.IsVisible = _issueClip.IsVisible || _issueAsset.IsVisible;
        _progress.IsVisible = Active;
        _progress.Value = (_job?.ProgressPercent ?? 0) / 100f;
    }

    private void Show(string status, bool error = false) { _status.Value = status; _status.HasError = error; }
    private static HStack Row(params Element[] children) => HStack.Create().Configure(row => row.Spacing = 8).WithChildren(children);
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
