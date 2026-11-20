using TrueMoon.Argentis;
using TrueMoon.Tales.Studio.Backend;

namespace TrueMoon.Tales.Studio.Presentation.Scene;

internal sealed class TimelineReadiness : Border
{
    private readonly SceneViewModel _model;
    private readonly BackendConnection _backend;
    private readonly Action<BackendReadinessIssue, bool> _navigate;
    private readonly CancellationTokenSource _closing = new();
    private readonly Text _status = new();
    private readonly Text _overview = new() { IsVisible = false }, _counts = new() { IsVisible = false };
    private readonly VStack _rows = VStack.Create().Configure(stack => stack.Spacing = 8);
    private readonly Button _refresh, _mode;
    private readonly List<Button> _links = [];
    private (long Revision, string Fingerprint)? _requested;
    private Task<BackendReadinessReport>? _pending;
    private BackendReadinessReport? _report;
    private bool _episode;
    private bool _stopped;

    internal TimelineReadiness(SceneViewModel model, BackendConnection backend, Action<BackendReadinessIssue, bool> navigate,
        Action refreshWindow)
    {
        _model = model; _backend = backend; _navigate = navigate;
        AccessibleName = "Проблемы монтажа";
        IsVisible = false;
        Padding = new Thickness(8);
        this.WithRole(SceneView.PaneRole);
        _refresh = Button.Simple("Обновить").WithRole(SceneView.CommandRole).OnClick(() =>
        {
            if (!CanUseSnapshot || NeedsFrame) return;
            InvalidateReport();
            _model.Open();
            refreshWindow();
        });
        _mode = Button.Simple("Эпизод").WithRole(SceneView.CommandRole);
        _mode.OnClick(() =>
        {
            if (!CanUseSnapshot || NeedsFrame) return;
            _episode = !_episode;
            _mode.Value = _episode ? "Монтаж" : "Эпизод";
            InvalidateReport();
            refreshWindow();
        });
        var top = HStack.Create().Configure(row => row.Spacing = 8).WithChildren(_mode, _refresh);
        var body = ScrollViewer.Create().Content(VStack.Create().Configure(stack => stack.Spacing = 6)
            .WithChildren(_status, _overview, _counts, _rows));
        SetContent(new ReadinessLayout(top, body));
    }

    internal bool NeedsFrame => _pending is not null;
    internal string Caption => _report is { } report ? $"Проблемы · {report.Total}" : "Проблемы";
    private (long Revision, string Fingerprint)? Snapshot => _model.SavedFingerprint is { } sha ? (_model.SavedRevision, sha) : null;
    private bool CanUseSnapshot => Snapshot is not null && _model.CanReload && !_model.Stale;

    internal void Toggle()
    {
        IsVisible = !IsVisible;
        if (IsVisible) InvalidateReport();
    }

    internal void InvalidateReport()
    {
        _requested = null;
        ClearReport();
        Show("Ожидаю сохранённый монтаж…");
    }

    internal void Sync(bool active)
    {
        var current = Snapshot;
        if (_requested is not null && _requested != current) InvalidateReport();
        _refresh.IsEnabled = _mode.IsEnabled = active && CanUseSnapshot && !NeedsFrame;
        foreach (var link in _links) link.IsEnabled = active && CanUseSnapshot;
        if (!active || !IsVisible || !CanUseSnapshot || NeedsFrame || current is not { } baseline || _requested == baseline) return;
        _requested = baseline;
        Show(_episode ? "Проверяю эпизод…" : "Проверяю монтаж…");
        _pending = _backend.CheckReadinessAsync(baseline.Revision, baseline.Fingerprint, _closing.Token, _episode);
        _refresh.IsEnabled = _mode.IsEnabled = false;
    }

    internal bool Poll()
    {
        if (_pending is not { IsCompleted: true } pending) return false;
        _pending = null;
        try
        {
            var report = pending.GetAwaiter().GetResult();
            if (_requested is { } baseline && _requested == Snapshot && baseline.Revision == report.Revision &&
                string.Equals(baseline.Fingerprint, report.Fingerprint, StringComparison.OrdinalIgnoreCase)) Apply(report);
        }
        catch (Exception error) when (error is IOException or InvalidDataException or
            OperationCanceledException or System.Text.Json.JsonException)
        {
            if (_requested is not null && _requested == Snapshot)
                Show(error is BackendCommandException { Code: "revision_conflict" }
                    ? "Монтаж изменился. Нажмите «Обновить»."
                    : "Проверка недоступна. Нажмите «Обновить».", error: true);
        }
        return true;
    }

    private void Apply(BackendReadinessReport report)
    {
        ClearReport();
        _report = report;
        if (report.Episode is { } episode)
        {
            var displayed = report.Total > report.Issues.Length ? $" · показано {report.Issues.Length} из {report.Total}" : "";
            Show($"Эпизод · ошибок {report.Total - episode.Notes} · замечаний {episode.Notes}{displayed} · рев. {report.Revision}");
            _overview.Value = $"{episode.Frames / episode.Fps:0.#} с · Окна с видео {episode.WindowsWithVideo}/{episode.Windows}";
            _counts.Value = $"Описанные стыки {episode.DocumentedJoins}/{episode.Joins} · Речь: {episode.VoiceEvents}";
            _overview.IsVisible = _counts.IsVisible = true;
        }
        else Show(report.Total switch
        {
            0 => $"Проблем нет · рев. {report.Revision}",
            _ when report.Total > report.Issues.Length => $"Показано {report.Issues.Length} из {report.Total} · рев. {report.Revision}",
            _ => $"Проблем: {report.Total} · рев. {report.Revision}"
        });
        foreach (var issue in report.Issues)
        {
            var choice = _model.Scene?.Choices.FirstOrDefault(row => row.Clip.Id == issue.ClipId);
            var name = issue.Track switch
            {
                "music" => "Музыка",
                "sound_events" => "Звуковая дорожка",
                _ => choice?.Title ?? (issue.EpisodeNote ? issue.WindowId is not null ? "Сцена" : "Эпизод" :
                    issue.ClipId is not null ? "Клип" : "Монтаж")
            };
            var position = issue.Frame is { } frame ? $"Кадр {frame} · {name}" : name;
            var description = issue.Reason switch
            {
                "clip_take_missing" => "Клип без видеодубля.",
                "asset_invalid" => "Некорректный формат или длительность.",
                "speech_source_changed" => "Запись речи изменилась.",
                "subtitle_review" => "Субтитр требует сверки.",
                "legacy_root_overlap" => "Корень чтения пересекает legacy_root.",
                "montage_invalid" => "Проверьте окна и тайминг монтажа.",
                _ => ReadinessMessage.Explain(issue)
            };
            var row = VStack.Create().Configure(stack => stack.Spacing = 3)
                .WithChildren(Text.Plain(position).WithRole(SceneView.CaptionRole), Text.Plain(description));
            row.AccessibleName = position;
            var actions = HStack.Create().Configure(stack => stack.Spacing = 6);
            if (issue.Reason is "episode_join_states_missing" or "episode_join_bridge_missing") actions.Add(Link("К стыку", issue, asset: false));
            if (issue.ClipId is not null) actions.Add(Link("К клипу", issue, asset: false));
            if (issue.AssetId is not null) actions.Add(Link("К ассету", issue, asset: true));
            if (actions.Items.Count > 0) row.Add(actions);
            _rows.Add(row);
        }
    }

    private Button Link(string label, BackendReadinessIssue issue, bool asset)
    {
        var button = Button.Simple(label).WithRole(SceneView.CommandRole).OnClick(() =>
        {
            if (CanUseSnapshot && issue.Matches(_model.SavedRevision, _model.SavedFingerprint)) _navigate(issue, asset);
        });
        _links.Add(button);
        return button;
    }

    private void ClearReport()
    {
        _report = null; _links.Clear();
        _overview.IsVisible = _counts.IsVisible = false;
        foreach (var row in _rows.Items.ToArray()) { _rows.Items.Remove(row); row.Dispose(); }
    }

    private void Show(string message, bool error = false) { _status.Value = message; _status.HasError = error; }

    internal void Stop()
    {
        if (_stopped) return;
        _stopped = true;
        _closing.Cancel();
        if (_pending is { } task)
            _ = task.ContinueWith(result => _ = result.Exception, CancellationToken.None, TaskContinuationOptions.OnlyOnFaulted, TaskScheduler.Default);
        _pending = null;
        _closing.Dispose();
    }

    public override void Dispose()
    {
        Stop();
        base.Dispose();
    }

    private sealed class ReadinessLayout : ElementList
    {
        private const float HeaderHeight = 36;
        private readonly Element _header, _body;

        internal ReadinessLayout(Element header, Element body)
        { _header = header; _body = body; Add(header); Add(body); }

        protected override Size MeasureCore(Size available, ITextLayoutService text)
        {
            var top = Math.Min(HeaderHeight, available.Height);
            _header.Measure(new(available.Width, top), text);
            _body.Measure(new(available.Width, available.Height - top), text);
            return available;
        }

        protected override void ArrangeCore(Rect area)
        {
            var top = Math.Min(HeaderHeight, area.Height);
            _header.Arrange(new(area.X, area.Y, area.Width, top));
            _body.Arrange(new(area.X, area.Y + top, area.Width, area.Height - top));
        }
    }
}
