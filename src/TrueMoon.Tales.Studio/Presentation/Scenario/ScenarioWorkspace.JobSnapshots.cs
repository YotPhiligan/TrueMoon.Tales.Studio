using TrueMoon.Argentis;
using TrueMoon.Tales.Studio.Backend;

namespace TrueMoon.Tales.Studio.Presentation.Scenario;

internal sealed partial class ScenarioWorkspace
{
    private TextBox _beforeJob = null!, _afterJob = null!;
    private readonly VStack _comparedJobs = Stack();

    private VStack CreateJobComparison()
    {
        _beforeJob = Input("ID задания для сравнения · А");
        _afterJob = Input("ID задания для сравнения · Б");
        return Stack(Field(_beforeJob), Field(_afterJob), ButtonFor("Сравнить задания", CompareJobs), _comparedJobs);
    }

    private void CompareJobs()
    {
        if (Busy || HasDraft || _stale || _baseline is not { } baseline) return;
        var beforeId = _beforeJob.Value.Trim();
        var afterId = _afterJob.Value.Trim();
        ClearJobComparison();
        Start("Сверяю снимки заданий…", async token =>
        {
            var comparison = await _backend.CompareJobSnapshotsAsync(beforeId, afterId, baseline, token).ConfigureAwait(false);
            return () =>
            {
                var (before, after) = comparison;
                _comparedJobs.Add(Text.Plain(before.PromptSha256 == after.PromptSha256 ? "Промпты совпадают по SHA" : "Промпты различаются"));
                _comparedJobs.Add(Text.Plain(DescribeMotion(before.Motion, after.Motion)));
                var prompts = new SplitView { Orientation = SplitOrientation.Horizontal };
                prompts.Divider.AccessibleName = "Ширина промптов в сравнении";
                prompts.FirstPane.SetContent(PromptField(before, "А"));
                prompts.SecondPane.SetContent(PromptField(after, "Б"));
                _comparedJobs.Add(prompts);
                ShowJob(before, "А");
                ShowJob(after, "Б");
                Show("");
            };
        });
    }

    private static string DescribeMotion(SavedJobMotion? before, SavedJobMotion? after)
    {
        if (before is null && after is null) return "Оба задания без опоры движения";
        if (before is null || after is null || (before.Kind, before.TakeId, before.AssetId) != (after.Kind, after.TakeId, after.AssetId))
            return "Опоры движения различаются";
        if (before.Sha256 is null || after.Sha256 is null) return "Дубль опоры совпадает · сравнить SHA входа нельзя";
        return before.Sha256 == after.Sha256 ? "Опоры движения совпадают" : "SHA опор движения различаются";
    }

    private static Element PromptField(SavedJobSnapshot snapshot, string side)
    {
        var prompt = ReadOnlyText($"Сохранённый промпт · {side}", 185);
        prompt.Text = snapshot.Prompt;
        return Field(prompt);
    }

    private void ShowJob(SavedJobSnapshot snapshot, string side)
    {
        var rows = Stack(Caption($"Задание {side} · ревизия {snapshot.Revision}"), Text.Plain(snapshot.Id),
            Text.Plain(snapshot.Motion switch
            {
                { Kind: "video_1" } motion => $"Video 1 · дубль {motion.TakeId}",
                { Kind: "motion_context" } motion => $"Motion Context · продолжение дубля {motion.TakeId}",
                _ => "Без опоры движения"
            }));
        if (snapshot.Motion is { } input)
            rows.Add(Text.Plain(input.Sha256 is null ? "SHA опоры не записан" : $"SHA опоры · {input.Sha256}"));
        if (snapshot.TransferForm?.Predecessor is { } predecessor)
            rows.Add(Text.Plain($"Подготовлено по заданию {predecessor.Id}"));
        rows.Add(Text.Plain($"SHA промпта · {snapshot.PromptSha256}"));
        if (snapshot.TransferForm is { } template && snapshot.TransferRequestSha256 is { } sha &&
            _baseline!.Project.Sources.FirstOrDefault(row => row.Id == template.SourceId) is { } source &&
            source.Takes.Any(IsTransferDraft))
        {
            rows.Add(ButtonFor($"Подготовить новую версию · {side}", () =>
            {
                if (Busy || HasDraft || _stale) return;
                OpenTransferForm(source, source.Takes.FirstOrDefault(take => take.Id == template.DraftTakeId && IsTransferDraft(take)),
                    template, new(snapshot.Id, sha));
            }));
        }
        _comparedJobs.Add(rows);
    }

    private void ClearJobComparison()
    {
        foreach (var row in _comparedJobs.Items.ToArray())
        {
            _comparedJobs.Items.Remove(row);
            row.Dispose();
        }
    }
}
