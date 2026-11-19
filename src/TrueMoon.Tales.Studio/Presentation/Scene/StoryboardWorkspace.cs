using TrueMoon.Alloy.Rendering.Skia;
using TrueMoon.Alloy.Runtime;
using TrueMoon.Argentis;
using TrueMoon.Tales.Studio.Backend;
using TrueMoon.Tales.Studio.Presentation.Shell;

namespace TrueMoon.Tales.Studio.Presentation.Scene;

internal sealed partial class StoryboardWorkspace : Border, IUiFrameParticipant
{
    private readonly BackendConnection _backend;
    private readonly Action<StoryboardShot, OpenedBackendProject, bool> _navigate;
    private readonly CancellationTokenSource _closing = new();
    private readonly VStack _rows = VStack.Create().Configure(stack => stack.Spacing = 6);
    private readonly Text _count = new(), _title = new(), _timing = new(), _text = new(), _status = new();
    private readonly Button _reload, _action, _frame, _back, _joinToggle;
    private readonly VStack _planDetails, _joinDetails;
    private readonly SplitView _split;
    private readonly Image _preview = new();
    private readonly PreviewSurface _surface;
    private readonly RichTextEditor _description = new(new() { IsReadOnly = true }) { Height = 90 };
    private readonly List<(StoryboardShot Shot, Button Button)> _choices = [];
    private SkiaImageSource? _pixels;
    private OpenedBackendProject? _snapshot;
    private StoryboardShot? _selected;
    private Task<Action>? _pending;

    internal StoryboardWorkspace(BackendConnection backend, Action<StoryboardShot, OpenedBackendProject, bool> navigate, Action close)
    {
        _backend = backend;
        _navigate = navigate;
        Padding = new Thickness(16, 40, 16, 16);
        var heading = Text.Plain("Раскадровка");
        heading.FontSize = 22;
        _title.FontSize = 19;
        _reload = Command("Обновить", () => Reload());
        _back = Command("К монтажу", close);
        _joinToggle = Command("Стыки сцен", ToggleJoins);
        _action = Command("К действию", () => Jump(toAction: true));
        _frame = Command("К кадру", () => Jump(toAction: false));
        _surface = new(_preview) { Height = 224, IsVisible = false };
        _planDetails = Stack(_title, _timing, Row(_action, _frame), _surface, _description, _text);
        _joinDetails = CreateJoinDetails();
        _split = new SplitView { Orientation = SplitOrientation.Horizontal, FirstPaneSize = 380 };
        _split.FirstPane.MinWidth = 215;
        _split.SecondPane.MinWidth = 250;
        _split.Divider.AccessibleName = "Ширина списка планов";
        _split.FirstPane.SetContent(ScrollViewer.Create().Content(Stack(_count, _rows, _joinRows)));
        _split.SecondPane.SetContent(ScrollViewer.Create().Content(Border.Create().Configure(border => border.Padding = new Thickness(16))
            .Content(Stack(_status, _planDetails, _joinDetails))));
        SetContent(new PreviewLayout(new StudioHeading(heading, Row(_joinToggle, _reload, _back)), _split, new Text()));
        Reload();
    }

    public bool NeedsFrame => _pending is not null;
    internal string? ProjectFingerprint => _snapshot?.Fingerprint;

    internal bool CanLeave()
    {
        if (_joinDraft) Show("Сохраните или отмените черновик стыка.", error: true);
        else if (_joinMode && NeedsFrame) Show("Дождитесь результата операции.");
        else return true;
        return false;
    }

    public void AdvanceFrame(TimeSpan elapsed, IInputContext context)
    {
        if (_pending is not { IsCompleted: true } ready) return;
        _pending = null;
        try { ready.GetAwaiter().GetResult()(); }
        catch (Exception error) when (error is IOException or InvalidDataException or OperationCanceledException or System.Text.Json.JsonException)
        {
            if (_joinMode) ShowJoinError(error);
            else Show(error is BackendCommandException { Code: "revision_conflict" }
                ? "Проект изменился. Обновите раскадровку."
                : _selected is null ? "Не удалось прочитать проект." : "Предпросмотр или переход недоступен.", error: true);
        }
        if (!IsDisposed) SyncCommands();
    }

    private void Reload() => Start("Читаю раскадровку…", async token =>
    {
        var snapshot = await _backend.OpenAsync(token).ConfigureAwait(false);
        var shots = StoryboardShot.Read(snapshot.Project);
        return () =>
        {
            if (_joinMode) RefreshJoins(snapshot, shots);
            else Apply(snapshot, shots);
        };
    });

    private void Apply(OpenedBackendProject snapshot, StoryboardShot[] shots)
    {
        _snapshot = snapshot;
        _selected = null;
        ClearPreview();
        foreach (var element in _rows.Items.ToArray()) { _rows.Items.Remove(element); element.Dispose(); }
        _choices.Clear();
        (string? Id, string Name)? segment = null;
        foreach (var shot in shots)
        {
            var group = (shot.SegmentId, shot.SegmentName);
            if (segment != group)
            {
                segment = group;
                _rows.Add(Text.Plain(group.SegmentName).WithRole(SceneView.CaptionRole));
            }
            var label = shot.Position is { } position ? $"{position.Frame:0000} · {shot.Title}" : $"— · {shot.Title}";
            var choice = Command(label, () => Select(shot));
            choice.AccessibleName = label;
            _choices.Add((shot, choice));
            _rows.Add(choice);
        }
        _count.Value = $"Планы: {shots.Length} · ревизия {snapshot.Project.Revision}";
        _timing.Value = _title.Value = _text.Value = "";
        _text.IsVisible = false;
        _description.IsVisible = false;
        Show(shots.Length == 0 ? "Добавьте действие в сценарии или разместите клип." : "Выберите план.");
    }

    private void Select(StoryboardShot shot)
    {
        if (NeedsFrame || _snapshot is not { } snapshot) return;
        _selected = shot;
        ClearPreview();
        _title.Value = shot.Title;
        _timing.Value = shot.Position is { } position
            ? $"{shot.Medium} · кадр {position.Frame} · {position.Duration} к. · {position.Duration / snapshot.Project.Fps:0.###} с"
            : $"{shot.Medium} · без позиции на шкале{(shot.PlannedDuration is { } length ? $" · {length} к." : "")}";
        _description.Text = shot.Description;
        _description.IsVisible = shot.Description.Length > 0;
        _text.Value = shot.Text is { } text ? $"Строки {text.FirstLine}–{text.LastLine} · {text.Sha256[..12]}" : "";
        _text.IsVisible = _text.Value.Length > 0;
        Show(shot.Position is null ? "Проверьте окно и длительность действия." : "");
        if (shot.Video is null && !shot.Still) { SyncCommands(); return; }
        Start("Читаю кадр…", async token =>
        {
            var frame = (await ReadFrameAsync(shot, 0, snapshot, token).ConfigureAwait(false))!;
            return () =>
            {
                _pixels = SkiaImageSource.CopyBgraPixels(frame.Width, frame.Height, frame.Pixels);
                _preview.Source = _pixels;
                _preview.AccessibleName = $"{shot.Medium} · {shot.Title}";
                _surface.IsVisible = true;
                Show(shot.Position is null ? "Проверьте окно и длительность действия." : "");
            };
        });
    }

    private async Task<BackendMediaReply?> ReadFrameAsync(StoryboardShot? shot, int offset, OpenedBackendProject snapshot, CancellationToken token)
    {
        var revision = snapshot.Project.Revision;
        var fingerprint = snapshot.Fingerprint;
        if (shot?.Video is { } asset)
        {
            var media = await _backend.MediaInfoAsync(asset, revision, fingerprint, token).ConfigureAwait(false);
            var sourceFrame = (shot.SourceIn + offset * shot.Speed) * media.Fps / snapshot.Project.Fps;
            var index = (int)Math.Clamp(Math.Floor(sourceFrame), 0, media.FrameCount - 1);
            return await _backend.FrameAsync(asset, index, revision, fingerprint, token).ConfigureAwait(false);
        }
        return shot is { Still: true, SegmentId: { } segment, ActionId: { } action }
            ? await _backend.StoryboardStillAsync(segment, action, revision, fingerprint, token).ConfigureAwait(false)
            : null;
    }

    private void Jump(bool toAction)
    {
        if (_snapshot is not { } baseline || _selected is not { } shot || NeedsFrame) return;
        if (toAction ? shot.ActionId is null : shot.Position is null) return;
        Start("Сверяю сохранённый монтаж…", async token =>
        {
            var actual = await _backend.OpenAsync(token).ConfigureAwait(false);
            if (actual.Project.Revision == baseline.Project.Revision && string.Equals(actual.Fingerprint, baseline.Fingerprint, StringComparison.OrdinalIgnoreCase))
                return () => _navigate(shot, actual, toAction);
            var shots = StoryboardShot.Read(actual.Project);
            return () => { Apply(actual, shots); Show("Проект изменился. Выберите план заново.", error: true); };
        });
    }

    private void Start(string message, Func<CancellationToken, Task<Action>> operation)
    {
        if (NeedsFrame) return;
        Show(message);
        _pending = operation(_closing.Token);
        SyncCommands();
    }

    private void SyncCommands()
    {
        _reload.IsEnabled = !NeedsFrame;
        _joinToggle.IsEnabled = _back.IsEnabled = !NeedsFrame && !_joinDraft;
        _action.IsVisible = _frame.IsVisible = _selected is not null;
        _action.IsEnabled = !NeedsFrame && _selected?.ActionId is not null;
        _frame.IsEnabled = !NeedsFrame && _selected?.Position is not null;
        foreach (var (shot, button) in _choices)
        {
            button.IsEnabled = !NeedsFrame;
            button.Tone = ReferenceEquals(shot, _selected) ? ControlTone.Accent : ControlTone.Neutral;
        }
        SyncJoinCommands();
    }

    private void Show(string status, bool error = false)
    { _status.Value = status; _status.IsVisible = status.Length > 0; _status.HasError = error; }

    private void ClearPreview()
    {
        _preview.Source = null;
        _pixels?.Dispose();
        _pixels = null;
        _surface.IsVisible = false;
    }

    public override void Dispose()
    {
        _closing.Cancel();
        _closing.Dispose();
        if (_pending is { } pending) _ = pending.ContinueWith(task => _ = task.Exception, TaskContinuationOptions.OnlyOnFaulted);
        ClearPreview();
        _exitSide.ClearPreview();
        _entrySide.ClearPreview();
        base.Dispose();
    }

    private static Button Command(string label, Action click) => Button.Simple(label).WithRole(SceneView.CommandRole).OnClick(click);
    private static VStack Stack(params Element[] children) => VStack.Create().Configure(stack => stack.Spacing = 8).WithChildren(children);
    private static HStack Row(params Element[] children) => HStack.Create().Configure(row => row.Spacing = 8).WithChildren(children);
}
