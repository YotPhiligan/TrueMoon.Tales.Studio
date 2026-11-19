using System.Globalization;
using TrueMoon.Argentis;
using TrueMoon.Tales.Studio.Backend;
using TrueMoon.Tales.Studio.Contracts.Projects;
using TrueMoon.Tales.Studio.Presentation.Shell;

namespace TrueMoon.Tales.Studio.Presentation.Scenario;

internal sealed partial class ScenarioWorkspace
{
    private void SegmentForm(ScenarioSegmentContract? segment)
    {
        var name = Input("Название сегмента", segment?.Name);
        var description = Description(segment?.Description);
        var fields = new List<Element> { Field(name), Field(description) };
        if (segment is { } previous)
        {
            Form("Правка сегмента", fields, () => new SaveSegment(previous.Id, name.Value, description.Text, 0, 0));
            return;
        }
        var end = _baseline!.Project.Montage.Windows.Select(row => (long)row.StartFrame + row.DurationFrames).DefaultIfEmpty().Max();
        var duration = (int)Math.Clamp(Math.Round(_baseline.Project.Fps * 10), 1, int.MaxValue);
        var start = Input("Начало окна, кадр", end.ToString(CultureInfo.InvariantCulture));
        var length = Input("Длительность окна, кадры", duration.ToString(CultureInfo.InvariantCulture));
        fields.Add(Field(start)); fields.Add(Field(length));
        Form("Новый сегмент", fields, () => new SaveSegment(null, name.Value, description.Text,
            Number(start, "начало окна"), Number(length, "длительность окна")));
    }

    private void ActionForm(StoryActionContract? story)
    {
        var segment = Segment!;
        var title = Input("Название действия", story?.Title);
        var description = Description(story?.Description);
        var plan = Input("Плановая длительность, кадры", story?.PlannedDurationFrames?.ToString(CultureInfo.InvariantCulture));
        Form(story is null ? "Новое действие" : "Правка действия", [Field(title), Field(description), Field(plan)],
            () => new SaveAction(segment.Id, story?.Id, title.Value, description.Text,
                string.IsNullOrWhiteSpace(plan.Value) ? null : Number(plan, "плановую длительность")));
    }

    private void LinkForm()
    {
        var segment = Segment!;
        var action = Story!;
        var source = Source!;
        var purpose = Input("Назначение источника", action.Sources.FirstOrDefault(row => row.SourceId == source.Id)?.Purpose);
        Form("Связать источник", [Text.Plain($"{action.Title} · {source.Title}"), Field(purpose)],
            () => new LinkSource(segment.Id, action.Id, source.Id, purpose.Value));
    }

    private void TextForm(ScenarioSegmentContract segment, StoryActionContract? action)
    {
        var previous = BackendAdaptation.Read(_baseline!.Project).Texts.SingleOrDefault(row =>
            row.SegmentId == segment.Id && row.ActionId == action?.Id)?.Fragment;
        var path = Input("Глава (@texts/… .md)", previous?.Reference);
        var first = Input("Первая строка", (previous?.FirstLine ?? 1).ToString(CultureInfo.InvariantCulture));
        var last = Input("Последняя строка", (previous?.LastLine ?? 1).ToString(CultureInfo.InvariantCulture));
        var version = Text.Plain(previous is null ? "" : $"Сохранённая версия · {previous.Sha256}");
        var fragment = new RichTextEditor(new() { IsReadOnly = true })
        { AccessibleName = "Фрагмент главы", Text = previous?.Excerpt ?? "", Height = 152 };
        ChapterFragment? observed = null;
        var read = ButtonFor("Прочитать фрагмент", () =>
        {
            string reference = path.Value.Trim();
            int start, end;
            try { start = Number(first, "первую строку"); end = Number(last, "последнюю строку"); }
            catch (ArgumentException error) { Show(error.Message, error: true); return; }
            observed = null;
            Start("Читаю выбранные строки…", async token =>
            {
                var result = await _backend.ReadAdaptationTextAsync(reference, start, end, token).ConfigureAwait(false);
                return () =>
                {
                    observed = result;
                    fragment.Text = result.Excerpt;
                    version.Value = $"Прочитанная версия · {result.Sha256}";
                    Show("После сохранения фрагмент будет связан со сценарием.");
                };
            });
        });
        Form(action is null ? $"Текст сегмента · {segment.Name}" : $"Текст действия · {action.Title}",
            [Field(path), Row(Field(first), Field(last)), read, version, Field(fragment),
                Text.Plain("Пустая ссылка снимает привязку.")], () =>
        {
            var reference = path.Value.Trim();
            if (reference.Length == 0) return new BindAdaptationText(segment.Id, action?.Id, null, 0, 0, null);
            var start = Number(first, "первую строку");
            var end = Number(last, "последнюю строку");
            if (observed is not { } value || value.Reference != reference || value.FirstLine != start || value.LastLine != end)
                throw new ArgumentException("Прочитайте фрагмент с указанными строками перед сохранением.");
            return new BindAdaptationText(segment.Id, action?.Id, reference, start, end, value.Sha256);
        });
    }

    private void AnimaticStillForm()
    {
        var segment = Segment!;
        var action = Story!;
        var previous = BackendAdaptation.Read(_baseline!.Project).Stills.SingleOrDefault(row =>
            row.SegmentId == segment.Id && row.ActionId == action.Id);
        var path = Input("Кадр (PNG или JPEG из @mount)", previous?.Reference);
        Form($"Кадр аниматика · {action.Title}", [Field(path),
            Text.Plain("При пустой ссылке используется текст действия.")], () =>
            new SaveAnimaticStill(segment.Id, action.Id, string.IsNullOrWhiteSpace(path.Value) ? null : path.Value.Trim()));
    }

    private void ImportForm(SourceContract? source)
    {
        var title = source is null ? Input("Название источника") : null;
        var take = Input("Название дубля");
        var path = Input("Путь или ссылка на видео");
        Form(source is null ? "Новый источник" : $"Новый дубль · {source.Title}",
            title is not null ? [Field(title), Field(take), Field(path)] : [Field(take), Field(path)],
            () => new ImportVideo(source?.Id, title?.Value ?? "", take.Value, path.Value));
    }

    private void RelinkForm()
    {
        var source = Source!;
        var take = Take!;
        var path = Input("Путь или ссылка на видео", BackendScenarioEdit.TryReference(_baseline!.Project, take.VideoAssetId!));
        Form($"Перепривязка · {BackendScenarioEdit.TakeName(take)}",
            [Field(path), Text.Plain("Все клипы этого видео сохранят свои участки.")],
            () => new RelinkVideo(source.Id, take.Id, path.Value));
    }

    private void PlaceForm()
    {
        var segment = Segment!;
        var action = Story!;
        var source = Source!;
        var take = Take!;
        var montage = _baseline!.Project.Montage;
        var window = montage.Windows.FirstOrDefault(row => row.SegmentId == segment.Id);
        var next = montage.Clips.Where(row => row.WindowId == window?.Id)
            .Select(row => (long)row.StartFrame + row.DurationFrames).DefaultIfEmpty(window?.StartFrame ?? 0).Max();
        var start = Input("Начало клипа, кадр", next.ToString(CultureInfo.InvariantCulture));
        var length = Input("Длительность клипа, кадры", "24");
        var sourceIn = Input("Начало в дубле, кадр", "0");
        var target = window is null ? "Сегменту требуется монтажное окно." :
            $"Окно {window.StartFrame}–{(long)window.StartFrame + window.DurationFrames} · {source.Title} / {BackendScenarioEdit.TakeName(take)}";
        Form("Разместить клип", [Text.Plain(target), Field(start), Field(length), Field(sourceIn)],
            () => new PlaceClip(segment.Id, action.Id, source.Id, take.Id, Number(start, "начало клипа"),
                Number(length, "длительность"), Number(sourceIn, "начало в дубле")));
    }

    private void Form(string title, IEnumerable<Element> fields, Func<ScenarioCommand> draft)
    {
        _draft = draft;
        _body.SetContent(ScrollViewer.Create().Content(Inset(Stack([Caption(title), .. fields]))));
        Show("");
        RefreshCommands();
    }

    private void Cancel()
    {
        if (!HasDraft) return;
        var form = _body.Child!;
        _body.SetContent(_browser);
        form.Dispose();
        _draft = null;
        Show("");
        RefreshLists();
    }

    private static int Number(TextBox field, string label) => int.TryParse(field.Value.Trim(), NumberStyles.None,
        CultureInfo.InvariantCulture, out var value) ? value : throw new ArgumentException($"Укажите {label} целым числом.");

    private static TextBox Input(string label, string? value = null) =>
        new() { AccessibleName = label, Value = (value ?? "").ReplaceLineEndings(" ") };
    private static RichTextEditor Description(string? description) =>
        new() { AccessibleName = "Описание", Text = description ?? "", Height = 112 };
    private static Element Field(Element input) => Stack(Text.Plain(input.AccessibleName), input);
}
