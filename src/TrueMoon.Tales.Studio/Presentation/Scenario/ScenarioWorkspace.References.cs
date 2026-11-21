using TrueMoon.Argentis;
using TrueMoon.Tales.Studio.Backend;
using TrueMoon.Tales.Studio.Contracts.Projects;

namespace TrueMoon.Tales.Studio.Presentation.Scenario;

internal sealed partial class ScenarioWorkspace
{
    private static readonly (string Id, string Name)[] ReferenceKinds =
        [("character", "Персонаж"), ("costume", "Костюм"), ("location", "Локация"), ("prop", "Реквизит"), ("style", "Стиль")];
    private readonly VStack _referenceEntries = Stack(), _referenceVersions = Stack(), _referenceUses = Stack();
    private readonly Text _referenceFile = new(), _referenceHash = new(), _sceneReference = new(), _actionReference = new(), _takeReference = new();
    private VStack _referencePane = null!;
    private Button _referenceTab = null!, _createReference = null!, _addVersion = null!, _assignScene = null!, _assignAction = null!,
        _clearScene = null!, _clearAction = null!, _recordTake = null!;
    private string? _referenceId;
    private int _referenceVersion;
    private ReferenceEntry? _referenceEntry;
    private ReferenceVersion? _selectedVersion;
    private ReferenceAssignment? _sceneSelection, _actionSelection;
    private TakeReferenceInput? _takeSelection;

    private VStack CreateReferencePane()
    {
        _referenceTab = ButtonFor("Эталоны", () => PickPane(Pane.References));
        _createReference = ButtonFor("Новый эталон", () => ReferenceForm(null));
        _addVersion = ButtonFor("Добавить версию", () => ReferenceForm(_referenceEntry));
        _assignScene = ButtonFor("Назначить сцене", () => ReferenceAssignmentForm(action: false, clear: false));
        _assignAction = ButtonFor("Назначить действию", () => ReferenceAssignmentForm(action: true, clear: false));
        _clearScene = ButtonFor("Снять со сцены", () => ReferenceAssignmentForm(action: false, clear: true));
        _clearAction = ButtonFor("Снять с действия", () => ReferenceAssignmentForm(action: true, clear: true));
        _recordTake = ButtonFor("Записать опору дубля", TakeReferenceForm);
        return _referencePane = Stack(Row(_createReference, _addVersion), _referenceEntries, _referenceVersions,
            _referenceFile, _referenceHash, _sceneReference, Row(_assignScene, _clearScene), _actionReference,
            Row(_assignAction, _clearAction), _takeReference, _recordTake, _referenceUses);
    }

    private void RefreshReferences(ProjectContract project)
    {
        var library = BackendReferenceLibrary.Read(project);
        _referenceEntry = library.Entries.FirstOrDefault(row => row.Id == _referenceId) ?? library.Entries.FirstOrDefault();
        _referenceId = _referenceEntry?.Id;
        _selectedVersion = _referenceEntry?.Versions.FirstOrDefault(row => row.Number == _referenceVersion) ?? _referenceEntry?.Versions.Last();
        _referenceVersion = _selectedVersion?.Number ?? 0;
        Rows(_referenceEntries, library.Entries.Select(row => (row.Id, $"{row.Name} · {ReferenceKinds.Single(kind => kind.Id == row.Kind).Name}")),
            _referenceId, id =>
            {
                _referenceId = id;
                _referenceVersion = 0;
                PickReference(project);
            });
        Rows(_referenceVersions, (_referenceEntry?.Versions ?? []).Select(row => (row.Number.ToString(), row.Display)), _referenceVersion.ToString(), number =>
        {
            _referenceVersion = int.Parse(number, System.Globalization.CultureInfo.CurrentCulture);
            PickReference(project);
        });
        _referenceFile.Value = _selectedVersion is { } version ? Path.GetFileName(version.ImageReference) : "";
        _referenceFile.AccessibleName = _selectedVersion?.ImageReference ?? "";
        _referenceHash.Value = _selectedVersion is { } sample ? $"SHA {sample.Sha256[..12]}" : "";
        _referenceHash.AccessibleName = _selectedVersion?.Sha256 ?? "";
        _referenceFile.IsVisible = _referenceHash.IsVisible = _selectedVersion is not null;
        var here = library.Assignments.Where(row => row.ReferenceId == _referenceId && row.SegmentId == _segmentId).ToArray();
        _sceneSelection = here.FirstOrDefault(row => row.ActionId is null);
        _actionSelection = here.FirstOrDefault(row => row.ActionId is not null && row.ActionId == _actionId);
        _takeSelection = BackendReferenceLibrary.Inputs(Take).FirstOrDefault(row => row.ReferenceId == _referenceId);
        _sceneReference.Value = _sceneSelection is { } scene ? $"Сцена · v{scene.Version}" : "Сцена · версия не выбрана";
        _actionReference.Value = _actionSelection is { } action ? $"Действие · v{action.Version}" :
            _sceneSelection is { } inherited ? $"Действие · версия сцены v{inherited.Version}" : "Действие · версия не выбрана";
        _takeReference.Value = Take is { } take ? $"{BackendScenarioEdit.TakeDisplayName(take)} · " +
            (_takeSelection is { } input ? $"опора v{input.Version.Number}" : "опора не записана") : "";
        _sceneReference.IsVisible = _referenceEntry is not null && Segment is not null;
        _actionReference.IsVisible = _referenceEntry is not null && Story is not null;
        _takeReference.IsVisible = _referenceEntry is not null && Take is not null;
        ShowUses(project, library);
    }

    private void PickReference(ProjectContract project)
    {
        RefreshReferences(project);
        RefreshCommands();
    }

    private void ShowUses(ProjectContract project, ReferenceLibrary library)
    {
        foreach (var row in _referenceUses.Items.ToArray()) { _referenceUses.Items.Remove(row); row.Dispose(); }
        if (_selectedVersion is null) { _referenceUses.IsVisible = false; return; }
        _referenceUses.IsVisible = true;
        _referenceUses.Add(Caption($"Использования v{_referenceVersion}"));
        foreach (var segment in project.Segments)
            foreach (var assignment in library.Assignments.Where(row => row.ReferenceId == _referenceId &&
                row.SegmentId == segment.Id && row.Version == _referenceVersion))
            {
                var title = segment.Actions.FirstOrDefault(action => action.Id == assignment.ActionId)?.Title;
                _referenceUses.Add(Text.Plain(title is null ? $"Сцена · {segment.Name}" : $"Действие · {segment.Name} / {title}"));
            }
        foreach (var source in project.Sources)
            foreach (var take in source.Takes.Where(take => BackendReferenceLibrary.Inputs(take).Any(input =>
                input.ReferenceId == _referenceId && input.Version.Number == _referenceVersion)))
                _referenceUses.Add(Text.Plain($"Дубль · {BackendScenarioEdit.TakeDisplayName(take)}"));
        if (_referenceUses.Items.Count == 1) _referenceUses.Add(Text.Plain("Использований пока нет"));
    }

    private void RefreshReferenceCommands(bool ready)
    {
        _createReference.IsEnabled = ready;
        _addVersion.IsVisible = _referenceEntry is not null;
        _addVersion.IsEnabled = ready && _referenceEntry is not null;
        var selected = ready && _selectedVersion is not null;
        _assignScene.IsVisible = _sceneReference.IsVisible;
        _assignAction.IsVisible = _actionReference.IsVisible;
        _clearScene.IsVisible = _sceneSelection is not null;
        _clearAction.IsVisible = _actionSelection is not null;
        _recordTake.IsVisible = _takeReference.IsVisible;
        _assignScene.IsEnabled = selected && Segment is not null && _sceneSelection?.Version != _referenceVersion;
        _assignAction.IsEnabled = selected && Story is not null && _actionSelection?.Version != _referenceVersion;
        _clearScene.IsEnabled = ready && _sceneSelection is not null;
        _clearAction.IsEnabled = ready && _actionSelection is not null;
        _recordTake.IsEnabled = selected && Take is not null && _takeSelection is null;
    }

    private void ReferenceForm(ReferenceEntry? entry)
    {
        var name = Input("Название эталона");
        var label = Input("Метка версии");
        var path = Input("Файл опоры (PNG или JPEG из @mount)");
        if (entry is not null)
        {
            Form($"Новая версия · {entry.Name}", [Field(label), Field(path)],
                () => new AddReferenceVersion(entry.Id, null, null, label.Value, path.Value));
            return;
        }
        string? selectedKind = null;
        var kinds = Stack();
        var category = Text.Plain("Выберите категорию");
        ShowKinds();
        Form("Новый эталон", [Field(name), category, kinds, Field(label), Field(path)], () =>
            new AddReferenceVersion(null, name.Value, selectedKind ?? throw new ArgumentException("Выберите категорию эталона."),
                label.Value, path.Value));

        void ShowKinds() => Rows(kinds, ReferenceKinds, selectedKind, id => { selectedKind = id; ShowKinds(); });
    }

    private void ReferenceAssignmentForm(bool action, bool clear)
    {
        var segment = Segment!;
        var story = action ? Story : null;
        var entry = _referenceEntry!;
        var version = clear ? null : _selectedVersion;
        Form(clear ? "Снять назначение" : "Назначить версию", [Text.Plain(story?.Title ?? segment.Name),
            Text.Plain(version is null ? entry.Name : $"{entry.Name} · {version.Display}")],
            () => new AssignSceneReference(segment.Id, story?.Id, entry.Id, version?.Number));
    }

    private void TakeReferenceForm()
    {
        var source = Source!;
        var take = Take!;
        var entry = _referenceEntry!;
        var version = _selectedVersion!;
        Form("Фактическая опора дубля", [Text.Plain(BackendScenarioEdit.TakeDisplayName(take)), Text.Plain($"{entry.Name} · {version.Display}"),
            Text.Plain("Подтвердите версию по материалам генерации или импорта.")],
            () => new RecordTakeReference(source.Id, take.Id, entry.Id, version.Number));
    }
}
