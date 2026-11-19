using TrueMoon.Argentis;
using TrueMoon.Tales.Studio.Backend;
using TrueMoon.Tales.Studio.Contracts.Projects;

namespace TrueMoon.Tales.Studio.Presentation.Scenario;

internal sealed partial class ScenarioWorkspace
{
    private static readonly (string Id, string Name)[] TakePairMethods =
        [(BackendTakePairs.Direct, "Прямая генерация"), (BackendTakePairs.FullTransfer, "Full Transfer")];
    private readonly Text _takePairMethod = new(), _draftInfo = new(), _finalInfo = new();
    private readonly VStack _relatedFinals = Stack();
    private VStack _takePairPanel = null!;
    private Button _linkTakePair = null!, _compareTakePair = null!;
    private DraftFinalPair? _takePair;
    private (string Source, string Final)? _shownPair;

    private VStack CreateTakePairPanel()
    {
        _linkTakePair = ButtonFor("Связать с черновиком", TakePairForm);
        _compareTakePair = ButtonFor("Сравнить с черновиком", CompareTakePair);
        return _takePairPanel = Stack(_linkTakePair, _relatedFinals, _takePairMethod, _draftInfo, _finalInfo, _compareTakePair);
    }

    private void RefreshTakePairs()
    {
        var source = Source;
        var take = Take;
        if (_shownPair is { } shown && (shown.Source != source?.Id || shown.Final != take?.Id)) ClearPreview();
        _takePair = take is null ? null : BackendTakePairs.Read(take);
        var related = source is not null && take is not null
            ? source.Takes.Where(row => BackendTakePairs.Read(row)?.Draft.TakeId == take.Id).ToArray() : [];
        Rows(_relatedFinals, related.Select(row => (row.Id, $"Финал · {BackendScenarioEdit.TakeName(row)}")), null,
            id => { _takeId = id; RefreshLists(); });
        _relatedFinals.IsVisible = related.Length > 0;
        _linkTakePair.IsVisible = take?.VideoAssetId is not null && _takePair is null && related.Length == 0 &&
            source!.Takes.Any(row => DraftCandidate(row, take));
        _takePairMethod.IsVisible = _draftInfo.IsVisible = _finalInfo.IsVisible = _compareTakePair.IsVisible = _takePair is not null;
        if (_takePair is { } pair)
        {
            _takePairMethod.Value = $"Финал по черновику · {pair.MethodName}";
            TakeInfo(_draftInfo, "Черновик", pair.Draft);
            TakeInfo(_finalInfo, "Финал", pair.Final);
        }
        _takePairPanel.IsVisible = _linkTakePair.IsVisible || _relatedFinals.IsVisible || _takePair is not null;
    }

    private static bool DraftCandidate(TakeContract candidate, TakeContract final) =>
        candidate.VideoAssetId is not null && candidate.VideoAssetId != final.VideoAssetId && BackendTakePairs.Read(candidate) is null;

    private static void TakeInfo(Text text, string role, TakeVideoSnapshot sample)
    {
        text.Value = $"{role} · {sample.Name} · {sample.Width}×{sample.Height} · {sample.Frames} к. · {sample.Fps:0.###} кадр/с";
        text.AccessibleName = $"{text.Value} · {sample.Reference} · SHA-256 {sample.Sha256}";
    }

    private void TakePairForm()
    {
        var source = Source!;
        var final = Take!;
        var drafts = source.Takes.Where(row => DraftCandidate(row, final)).Select(row => (row.Id, BackendScenarioEdit.TakeName(row))).ToArray();
        var draftRows = Stack();
        var methodRows = Stack();
        string? draft = null, method = null;
        ShowDrafts();
        ShowMethods();
        Form("Связать с черновиком", [Text.Plain($"Финальный дубль · {BackendScenarioEdit.TakeName(final)}"),
            Text.Plain("Выберите черновой дубль"), draftRows, Text.Plain("Как получен финал"), methodRows], () =>
            new LinkDraftFinalTake(source.Id, draft ?? throw new ArgumentException("Выберите черновик."), final.Id,
                method ?? throw new ArgumentException("Выберите способ получения финала.")));

        void ShowDrafts() => Rows(draftRows, drafts, draft, id => { draft = id; ShowDrafts(); });
        void ShowMethods() => Rows(methodRows, TakePairMethods, method, id => { method = id; ShowMethods(); });
    }

    private void CompareTakePair()
    {
        if (_baseline is not { } baseline || Source is not { } source || _takePair is not { } pair || Busy) return;
        int frame;
        try { frame = Number(_previewFrame, "кадр финала"); }
        catch (ArgumentException error) { Show(error.Message, error: true); return; }
        if (frame >= pair.Final.Frames) { Show($"Укажите кадр финала от 0 до {pair.Final.Frames - 1}.", error: true); return; }
        ClearPreview();
        Start("Сверяю версии и читаю кадры…", async token =>
        {
            var draft = await _backend.TakePairFrameAsync(source.Id, pair, draft: true, frame, baseline, token).ConfigureAwait(false);
            var final = await _backend.TakePairFrameAsync(source.Id, pair, draft: false, frame, baseline, token).ConfigureAwait(false);
            var latest = await _backend.OpenAsync(token).ConfigureAwait(false);
            if (latest.Project.Revision != baseline.Project.Revision || latest.Fingerprint != baseline.Fingerprint)
                throw new BackendCommandException("revision_conflict");
            return () =>
            {
                _first.Show(source.Title, pair.Draft.Name, draft);
                _second.Show(source.Title, pair.Final.Name, final);
                _shownPair = (source.Id, pair.Final.TakeId);
                Show("");
            };
        });
    }
}
