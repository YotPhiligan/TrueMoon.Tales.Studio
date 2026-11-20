using System.Globalization;
using TrueMoon.Argentis;
using TrueMoon.Tales.Studio.Backend;

namespace TrueMoon.Tales.Studio.Presentation.Scenario;

internal sealed partial class ScenarioWorkspace
{
    private Button _dependenciesTab = null!, _compareDependencies = null!;
    private TextBox _dependencyRevision = null!;
    private VStack _dependencies = null!;
    private readonly VStack _dependencyRows = Stack();
    private readonly Text _dependencySummary = new();
    private string? _dependencyFingerprint;

    private VStack CreateDependenciesPane()
    {
        _dependenciesTab = ButtonFor("Зависимости", () => PickPane(Pane.Dependencies));
        _dependencyRevision = Input("Ревизия для сравнения");
        _compareDependencies = ButtonFor("Сравнить ревизии", CompareDependencies);
        _dependencies = Stack(Field(_dependencyRevision), _compareDependencies, _dependencySummary, _dependencyRows, CreateJobComparison());
        return _dependencies;
    }

    private void ShowDependencies(bool visible)
    {
        _dependencies.IsVisible = visible;
        _dependenciesTab.Tone = visible ? ControlTone.Accent : ControlTone.Neutral;
        if (_dependencyFingerprint == _baseline?.Fingerprint) return;
        _dependencyFingerprint = _baseline?.Fingerprint;
        ClearJobComparison();
        _dependencyRevision.Value = _baseline?.Project.Revision is > 1 and var revision
            ? (revision - 1).ToString(CultureInfo.InvariantCulture) : "";
        ClearDependencyRows();
        _dependencySummary.Value = _dependencyRevision.Value.Length > 0 ? "" : "Для сравнения нужна запись в истории.";
    }

    private void CompareDependencies()
    {
        if (Busy || HasDraft || _stale || _baseline is not { } baseline) return;
        if (!long.TryParse(_dependencyRevision.Value, NumberStyles.None, CultureInfo.InvariantCulture, out var revision) ||
            revision < 0 || revision >= baseline.Project.Revision)
        {
            Show("Укажите более раннюю ревизию проекта.", error: true);
            return;
        }
        ClearDependencyRows();
        _dependencySummary.Value = "";
        Start("Сравниваю сохранённые ревизии…", async token =>
        {
            var report = await _backend.CheckDependenciesAsync(revision, baseline, token).ConfigureAwait(false);
            return () =>
            {
                _dependencySummary.Value = report.Total == 0
                    ? $"Ревизии {revision} и {report.Revision} · изменения входов не найдены"
                    : $"Ревизии {revision} и {report.Revision} · показано {report.Rows.Length} из {report.Total}";
                foreach (var row in report.Rows)
                {
                    var lines = Stack(Caption($"{Label(row.Kind)} · {row.Label}"));
                    var (before, after) = row.Kind switch
                    {
                        "export" => (row.Before, $"SHA {row.After}"),
                        "take_reference" => ($"Опора дубля · {row.Before}", $"Назначено сейчас · {row.After}"),
                        "take_draft" => ($"Черновик при сборке · {row.Before}", $"Текущая ссылка · {row.After}"),
                        _ => ($"Было · {row.Before}", $"Сейчас · {row.After}")
                    };
                    lines.Add(Text.Plain(before));
                    lines.Add(Text.Plain(after));
                    if (CanOpen(row))
                        lines.Add(ButtonFor(row.TakeId is not null ? "К дублю" : row.ActionId is not null ? "К действию" : "К сцене",
                            () => OpenDependency(row)));
                    _dependencyRows.Add(lines);
                }
                if (!report.ExportSearchComplete) _dependencyRows.Add(Text.Plain("Список прежних MP4 неполный."));
                Show("");
            };
        });
    }

    private bool CanOpen(DependencyRow row) => row.TakeId is { } take
        ? _baseline!.Project.Sources.Any(source => source.Id == row.SourceId && source.Takes.Any(item => item.Id == take))
        : _baseline!.Project.Segments.Any(segment => segment.Id == row.SegmentId);

    private void OpenDependency(DependencyRow row)
    {
        if (Busy || HasDraft || _stale || !CanOpen(row)) return;
        if (row.SegmentId is not null && _baseline!.Project.Segments.Any(segment => segment.Id == row.SegmentId))
        {
            _segmentId = row.SegmentId;
            _actionId = row.ActionId;
            _clipId = row.ClipId;
        }
        if (row.TakeId is not null)
        {
            _sourceId = row.SourceId;
            _takeId = row.TakeId;
            _query = _search.Value = "";
        }
        _pane = row.TakeId is not null ? Pane.Library : Pane.Chapter;
        RefreshLists();
    }

    private void ClearDependencyRows()
    {
        foreach (var row in _dependencyRows.Items.ToArray())
        {
            _dependencyRows.Items.Remove(row);
            row.Dispose();
        }
    }

    private static string Label(string kind) => kind switch
    {
        "chapter" => "Текст главы",
        "reference" => "Назначение эталона",
        "take_reference" => "Дубль с прежней опорой",
        "take_draft" => "Финал с прежним черновиком",
        "sound" => "Звук",
        "clip" => "Клип",
        "montage" or "animatic" => "Сборка для сверки",
        "export" => "Сохранённый MP4",
        _ => "Изменение"
    };
}
