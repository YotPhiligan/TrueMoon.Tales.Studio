using System.Globalization;
using TrueMoon.Argentis;
using TrueMoon.Tales.Studio.Backend;

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

    private VStack CreateTransferPanel()
    {
        _openTransfer = ButtonFor("Подготовить Full Transfer", OpenTransferForm);
        _inspectTransfer = ButtonFor("Открыть задание", () => AccessTransfer(stage: false));
        _stageTransfer = ButtonFor("Скопировать входы Full Transfer", () => AccessTransfer(stage: true));
        _launchTransfer = ButtonFor("Запустить Full Transfer на GPU", () => ChangeTransferRun("launch"));
        _refreshTransferRun = ButtonFor("Обновить статус Full Transfer", () => ChangeTransferRun("status"));
        _stopTransfer = ButtonFor("Остановить Full Transfer", () => ChangeTransferRun("stop"));
        _collectTransfer = ButtonFor("Сохранить финальный дубль", CollectTransfer);
        _transferId = Input("ID задания Full Transfer");
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
            "completed" => "MP4 проверен и готов к сохранению",
            "cancelled" => "Запуск остановлен",
            "interrupted" => "Запуск прерван",
            "timed_out" => "Время запуска истекло",
            "failed" => "Запуск завершился ошибкой · журнал в Run",
            _ => ready.Staged ? "Входы скопированы" : "Подготовлено"
        };
        _transferReceipt.Value = $"{status} · {ready.Width}×{ready.Height} · {ready.Frames} к. · {ready.Fps} кадр/с";
        _transferPath.Value = BackendFullTransfer.JobReference(ready.Id);
        _transferPath.AccessibleName = $"{_transferPath.Value} · SHA сборки {ready.BuildSha256}";
    }

    private void OpenTransferForm()
    {
        var source = Source!;
        var take = Take!;
        var key = Guid.NewGuid().ToString("N");
        var seed = Input("Seed", "0");
        var guide = Input("Picture для Guide 0", "1");
        var role = Input("Роль Video 1");
        var notes = EditText("Фазы движения Video 1", null);
        var prompt = EditText("Промпт Ref2VA", "subject_definitions:\n\nsummary:\n\nretention_analysis:\n\ndetailed_description:\n\noverall_soundscape: N/A\n\nnon_diegetic_music: N/A");
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
        foreach (var input in BackendReferenceLibrary.Inputs(take).Take(8))
            AddImage(input.Version.ImageReference, input.Name);
        if (images.Count == 0) AddImage("", "");
        _transferDraft = () => new FullTransferForm(key, source.Id, take.Id,
            ulong.TryParse(seed.Value.Trim(), NumberStyles.None, CultureInfo.InvariantCulture, out var number)
                ? number : throw new ArgumentException("Укажите целый Seed от 0 до 18446744073709551615."),
            prompt.Text, images.Select(row => new FullTransferImage(row.Reference.Value, row.Role.Value)).ToArray(),
            Number(guide, "номер Picture для Guide 0"), role.Value, notes.Text);
        _commit.Value = "Подготовить";
        ShowForm("Подготовка Full Transfer", [Text.Plain($"Video 1 · {BackendScenarioEdit.TakeName(take)}"),
            Text.Plain("1280×736 · 24 кадр/с · длина черновика · без очереди"), Field(seed), Field(guide),
            Field(role), Field(notes), Field(prompt), rows, Row(add, remove)]);

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
        Start("Готовлю граф Full Transfer…", async token =>
        {
            var receipt = await _backend.PrepareFullTransferAsync(form, baseline, token).ConfigureAwait(false);
            return () =>
            {
                _preparedTransfer = receipt;
                _transferRun = null;
                _transferId.Value = receipt.Id;
                Cancel();
                Show("Full Transfer подготовлен без постановки в очередь.");
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
        Start(stage ? "Сверяю и копирую входы Full Transfer…" : "Открываю задание Full Transfer…", async token =>
        {
            var receipt = await _backend.FullTransferJobAsync(id, requestSha, baseline, token).ConfigureAwait(false);
            var run = await _backend.FullTransferRunAsync(id, receipt.RequestSha256, "status", baseline, token).ConfigureAwait(false);
            return () =>
            {
                if (receipt.DraftTakeId != takeId) { Show("Задание относится к другому черновику.", error: true); return; }
                _preparedTransfer = receipt;
                _transferRun = run;
                _transferId.Value = receipt.Id;
                RefreshTransfer();
                Show(stage ? "Входы сверены и скопированы без постановки в очередь." : "Задание Full Transfer открыто.");
            };
        });
    }

    private void ChangeTransferRun(string operation)
    {
        if (Busy || _stale || HasDraft || _baseline is not { } baseline || _preparedTransfer is not { Staged: true } job ||
            job.DraftTakeId != Take?.Id) return;
        var action = operation switch
        {
            "launch" => "Проверяю входы перед запуском Full Transfer…",
            "stop" => "Останавливаю Full Transfer…",
            _ => "Читаю статус Full Transfer…"
        };
        Start(action, async token =>
        {
            var state = await _backend.FullTransferRunAsync(job.Id, job.RequestSha256, operation, baseline, token).ConfigureAwait(false);
            return () =>
            {
                _transferRun = state;
                RefreshTransfer();
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
