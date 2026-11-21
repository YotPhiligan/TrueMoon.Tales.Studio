using System.Globalization;
using TrueMoon.Argentis;
using TrueMoon.Tales.Studio.Backend;
using TrueMoon.Tales.Studio.Contracts.Projects;

namespace TrueMoon.Tales.Studio.Presentation.Scenario;

internal sealed partial class ScenarioWorkspace
{
    private readonly Text _transferReceipt = new(), _transferPath = new();
    private Button _openTransfer = null!;
    private Button _inspectTransfer = null!, _stageTransfer = null!;
    private Button _launchTransfer = null!, _refreshTransferRun = null!, _stopTransfer = null!, _collectTransfer = null!;
    private TextBox _transferId = null!;
    private VStack _transferPanel = null!;
    private Func<FullTransferForm>? _transferDraft;
    private FullTransferReceipt? _preparedTransfer;
    private FullTransferRunState? _transferRun;
    private TimeSpan _nextTransferPoll;

    private bool TrackingTransfer => !HasDraft && !_stale && _preparedTransfer?.DraftTakeId == Take?.Id &&
        _transferRun?.State is "running" or "stopping";

    private void PollTransfer(TimeSpan elapsed)
    {
        if (!TrackingTransfer) return;
        _nextTransferPoll += elapsed;
        if (_nextTransferPoll < TimeSpan.FromSeconds(1)) return;
        _nextTransferPoll = TimeSpan.Zero;
        ChangeTransferRun("status");
    }

    private VStack CreateTransferPanel()
    {
        _openTransfer = ButtonFor(BackendTakePairs.GenerationFromDraftLabel, OpenTransferForm);
        _inspectTransfer = ButtonFor("Открыть задание", () => AccessTransfer(stage: false));
        _stageTransfer = ButtonFor("Проверить входы", () => AccessTransfer(stage: true));
        _launchTransfer = ButtonFor("Запустить генерацию на GPU", () => ChangeTransferRun("launch"));
        _refreshTransferRun = ButtonFor("Обновить статус генерации", () => ChangeTransferRun("status"));
        _stopTransfer = ButtonFor("Остановить генерацию", () => ChangeTransferRun("stop"));
        _collectTransfer = ButtonFor("Повторить запись дубля", CollectTransfer);
        _transferId = Input("ID задания генерации");
        _transferReceipt.IsVisible = _transferPath.IsVisible = _stageTransfer.IsVisible = _launchTransfer.IsVisible =
            _refreshTransferRun.IsVisible = _stopTransfer.IsVisible = _collectTransfer.IsVisible = false;
        _transferPanel = Stack(_openTransfer, Field(_transferId), _inspectTransfer, _transferReceipt, _transferPath,
            _stageTransfer, _launchTransfer, _refreshTransferRun, _stopTransfer, _collectTransfer);
        _transferPanel.IsVisible = false;
        return _transferPanel;
    }

    private void RefreshTransfer()
    {
        _transferPanel.IsVisible = Take is { VideoAssetId: not null } && _takePair is null;
        var ready = _preparedTransfer;
        var matches = ready is not null && ready.DraftTakeId == Take?.Id;
        var state = _transferRun?.Id == ready?.Id ? _transferRun?.State : null;
        var current = matches && ready!.Fingerprint == _baseline?.Fingerprint;
        _transferReceipt.IsVisible = _transferPath.IsVisible = matches;
        _stageTransfer.IsVisible = current && state is null or "prepared" or "staged";
        _launchTransfer.IsVisible = current && ready!.Staged && state is null or "staged";
        _refreshTransferRun.IsVisible = matches && ready!.Staged;
        _stopTransfer.IsVisible = matches && state is "running" or "stopping";
        var saved = matches && Source!.Takes.Any(take => take.Id == BackendFullTransfer.FinalTakeId(ready!.Id));
        _collectTransfer.IsVisible = matches && state == "completed" && !saved;
        if (ready is null) return;
        var status = saved ? "Финальный дубль сохранён" : state switch
        {
            "running" => "Запуск выполняется",
            "stopping" => "Останавливаю запуск",
            "completed" => "MP4 готов · требуется запись в библиотеку",
            "cancelled" => "Запуск остановлен",
            "interrupted" => "Запуск прерван",
            "timed_out" => "Время запуска истекло",
            "failed" => "Запуск завершился ошибкой",
            _ => ready.Staged ? "Входы проверены" : "Подготовлено"
        };
        _transferReceipt.Value = $"{status} · {ready.Width}×{ready.Height} · {ready.Frames} к. · {ready.Fps} кадр/с";
        _transferPath.Value = BackendFullTransfer.JobReference(ready.Id);
        _transferPath.AccessibleName = $"{_transferPath.Value} · SHA сборки {ready.BuildSha256}";
    }

    private void OpenTransferForm() => OpenTransferForm(Source!, Take!);

    private static bool IsTransferDraft(TakeContract take) => take.VideoAssetId is not null && BackendTakePairs.Read(take) is null;

    private void OpenTransferForm(SourceContract source, TakeContract? take, FullTransferForm? template = null,
        FullTransferPredecessor? predecessor = null)
    {
        var key = Guid.NewGuid().ToString("N");
        var draftId = predecessor is null ? null : Input("Дубль Video 1 · ID", take?.Id);
        var seed = Input("Seed", (template?.Seed ?? 0).ToString(CultureInfo.InvariantCulture));
        var guide = Input("Picture для Guide 0", (template?.GuidePicture ?? 1).ToString(CultureInfo.InvariantCulture));
        var role = Input("Роль Video 1", template?.MotionRole);
        var notes = EditText("Фазы движения Video 1", template?.MotionNotes);
        var prompt = EditText("Промпт Ref2VA", template?.Prompt ??
            "subject_definitions:\n\nsummary:\n\nretention_analysis:\n\ndetailed_description:\n\noverall_soundscape: N/A\n\nnon_diegetic_music: N/A");
        prompt.Height = 200;
        var rows = Stack();
        var images = new List<(Element Row, TextBox Reference, TextBox Role)>();
        var add = Button.Simple("Добавить Picture");
        var remove = Button.Simple("Убрать последний Picture");
        add.OnClick(() => AddImage("", ""));
        remove.OnClick(() =>
        {
            var row = images[^1].Row;
            rows.Items.Remove(row);
            row.Dispose();
            images.RemoveAt(images.Count - 1);
            SyncRows();
        });
        if (template is not null)
        {
            foreach (var image in template.Images) AddImage(image.Reference, image.Role);
        }
        else
        {
            foreach (var input in BackendReferenceLibrary.Inputs(take!).Take(8))
                AddImage(input.Version.ImageReference, input.Name);
        }
        if (images.Count == 0) AddImage("", "");
        _transferDraft = () => new FullTransferForm(key, source.Id, DraftId(),
            ulong.TryParse(seed.Value.Trim(), NumberStyles.None, CultureInfo.InvariantCulture, out var number)
                ? number : throw new ArgumentException("Укажите целый Seed от 0 до 18446744073709551615."),
            prompt.Text, images.Select(row => new FullTransferImage(row.Reference.Value, row.Role.Value)).ToArray(),
            Number(guide, "номер Picture для Guide 0"), role.Value, notes.Text, predecessor);
        _commit.Value = "Подготовить";
        Element motion = draftId is null
            ? Text.Plain($"Черновик (Video 1) · {BackendScenarioEdit.TakeDisplayName(take!)}")
            : Stack(Text.Plain($"На основе {predecessor!.Id}"), Field(draftId), Stack(source.Takes.Where(IsTransferDraft)
                .Select(candidate => ButtonFor($"Video 1 · {BackendScenarioEdit.TakeDisplayName(candidate)}", () => draftId.Value = candidate.Id))
                .Cast<Element>().ToArray()));
        ShowForm(BackendTakePairs.GenerationFromDraftLabel, [motion, Text.Plain("1280×736 · 24 кадр/с · длина черновика · без очереди"),
            Field(seed), Field(guide), Field(role), Field(notes), Field(prompt), rows, Row(add, remove)]);

        string DraftId()
        {
            var id = draftId?.Value.Trim() ?? take!.Id;
            if (!source.Takes.Any(candidate => candidate.Id == id && IsTransferDraft(candidate)))
                throw new ArgumentException("Выберите видео черновика из этого источника.");
            return id;
        }

        void AddImage(string reference, string imageRole)
        {
            var index = images.Count + 1;
            var path = Input($"Picture {index} · файл из @mount", reference);
            var purpose = Input($"Picture {index} · роль", imageRole);
            var row = Stack(Field(path), Field(purpose));
            rows.Add(row);
            images.Add((row, path, purpose));
            SyncRows();
        }

        void SyncRows() { add.IsEnabled = images.Count < 8; remove.IsEnabled = images.Count > 1; }
    }

    private void PrepareTransfer(OpenedBackendProject baseline)
    {
        FullTransferForm form;
        try { form = _transferDraft!(); }
        catch (ArgumentException error) { Show(error.Message, error: true); return; }
        Start("Готовлю задание генерации…", async token =>
        {
            var receipt = await _backend.PrepareFullTransferAsync(form, baseline, token).ConfigureAwait(false);
            return () =>
            {
                _preparedTransfer = receipt;
                _transferRun = null;
                _transferId.Value = receipt.Id;
                if (form.Predecessor is { } previous)
                {
                    _beforeJob.Value = previous.Id;
                    _afterJob.Value = receipt.Id;
                    _sourceId = form.SourceId;
                    _takeId = form.DraftTakeId;
                    _query = _search.Value = "";
                    ClearJobComparison();
                }
                Cancel();
                Show("Задание подготовлено без постановки в очередь.");
            };
        });
    }

    private void AccessTransfer(bool stage)
    {
        if (Busy || _stale || HasDraft || _baseline is not { } baseline || Take?.Id is not { } takeId) return;
        var prepared = _preparedTransfer;
        if (stage && (prepared?.DraftTakeId != takeId || prepared.Fingerprint != baseline.Fingerprint)) return;
        var id = stage ? prepared!.Id : _transferId.Value.Trim();
        if (!BackendFullTransfer.ValidJobId(id)) { Show("Укажите ID задания вида ft-…", error: true); return; }
        var requestSha = stage ? prepared!.RequestSha256 : null;
        Start(stage ? "Сверяю входы генерации…" : "Открываю задание генерации…", async token =>
        {
            var current = stage ? baseline : await _backend.OpenAsync(token).ConfigureAwait(false);
            var receipt = await _backend.FullTransferJobAsync(id, requestSha, current, token).ConfigureAwait(false);
            var run = await _backend.FullTransferRunAsync(id, receipt.RequestSha256, "status", current, token).ConfigureAwait(false);
            return () =>
            {
                if (receipt.DraftTakeId != takeId) { Show("Задание относится к другому черновику.", error: true); return; }
                _baseline = current;
                _preparedTransfer = receipt;
                _transferRun = run;
                _transferId.Value = receipt.Id;
                if (current.Fingerprint != baseline.Fingerprint) RefreshLists();
                else RefreshTransfer();
                Show(stage ? "Входы сверены. Studio скопирует их в Temp при запуске." : "Задание генерации открыто.");
            };
        });
    }

    private void ChangeTransferRun(string operation)
    {
        if (Busy || _stale || HasDraft || _baseline is not { } baseline || _preparedTransfer is not { Staged: true } job ||
            job.DraftTakeId != Take?.Id) return;
        var action = operation switch
        {
            "launch" => "Проверяю входы перед запуском генерации…",
            "stop" => "Останавливаю генерацию…",
            _ => "Читаю статус генерации…"
        };
        Start(action, async token =>
        {
            var state = await _backend.FullTransferRunAsync(job.Id, job.RequestSha256, operation, baseline, token).ConfigureAwait(false);
            var updated = state.State == "completed" ? await _backend.OpenAsync(token).ConfigureAwait(false) : baseline;
            return () =>
            {
                _baseline = updated;
                _transferRun = state;
                if (updated.Fingerprint != baseline.Fingerprint) RefreshLists();
                else RefreshTransfer();
                Show(_transferReceipt.Value, error: state.State is "failed" or "timed_out");
            };
        });
    }

    private void CollectTransfer()
    {
        if (Busy || _stale || HasDraft || _baseline is not { } baseline || _preparedTransfer is not { } job ||
            job.DraftTakeId != Take?.Id || _transferRun is not { State: "completed" } run || run.Id != job.Id) return;
        Start("Проверяю MP4 и сохраняю финальный дубль…", async token =>
        {
            var saved = await _backend.CollectFullTransferAsync(job.Id, job.RequestSha256, baseline, token).ConfigureAwait(false);
            return () =>
            {
                _baseline = saved;
                _takeId = BackendFullTransfer.FinalTakeId(job.Id);
                RefreshLists();
                Show($"Финальный дубль сохранён · рев. {saved.Project.Revision}");
            };
        });
    }
}
