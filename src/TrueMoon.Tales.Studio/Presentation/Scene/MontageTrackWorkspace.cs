using System.Globalization;
using System.Text.Json.Nodes;
using TrueMoon.Alloy.Runtime;
using TrueMoon.Argentis;
using TrueMoon.Tales.Studio.Backend;
using TrueMoon.Tales.Studio.Contracts.Projects;
using TrueMoon.Tales.Studio.Models;
using TrueMoon.Tales.Studio.Presentation.Shell;

namespace TrueMoon.Tales.Studio.Presentation.Scene;

internal sealed class MontageTrackWorkspace : Border, IUiFrameParticipant
{
    private readonly BackendConnection _backend;
    private readonly Action _close;
    private readonly CancellationTokenSource _closing = new();
    private readonly VStack _list = Stack(), _form = Stack();
    private readonly ScrollViewer _contents;
    private readonly Text _status = new();
    private readonly Button _save, _reload, _remove, _discard, _back, _native;
    private readonly Element _navigation;
    private readonly Dictionary<string, TextBox> _fields = [];
    private OpenedBackendProject? _snapshot;
    private RichTextEditor? _caption;
    private Task<Action>? _pending;
    private string? _clip, _window, _kind, _id, _clean;
    private int _cursor;
    private bool _clipSound, _speechForm, _stale, _delete;
    public bool NeedsFrame => _pending is not null;
    internal bool HasDraft => _kind is not null && Signature() != _clean;

    internal MontageTrackWorkspace(BackendConnection backend, SceneSnapshot? scene, int cursor, string? track, string? id, Action close,
        BackendReadinessIssue? issue = null)
    {
        _backend = backend; _close = close;
        (_clip, _window, _cursor) = (scene?.Clip.Id, scene?.Segment.Id, cursor);
        Padding = new Thickness(16, 44, 16, 12);
        _back = Button("К монтажу", Close);
        _reload = Button("Перечитать дорожки", () => Reload());
        _save = Button("Сохранить дорожку", Save);
        _remove = Button("Удалить…", Remove);
        _discard = Button("Сбросить ввод", () => Select(null, null));
        _native = Button("Звук видеоклипов", () => Run(() => new SetMontageClipAudio(!NativeAudio)));
        _navigation = Row(Button("Музыка", () => Select("music", "music")), Button("+ Звук сегмента", () => Select("sound_events", null)),
            Button("+ Звук клипа", () => Select("sound_events", null, clipSound: true)),
            Button("+ Реплика или рассказчик", () => Select("sound_events", null, speechForm: true)), Button("+ Титр", () => Select("titles", null)),
            Button("+ Маркер", () => Select("markers", null)));
        _contents = ScrollViewer.Create().Content(Stack(_navigation, _native, _list, _form));
        SetContent(new PreviewLayout(new StudioHeading(Text.Plain("Дорожки монтажа"), Row(_reload, _back)), _contents,
            Stack(_status, Row(_save, _remove, _discard))));
        Reload(track, id, issue);
    }

    private ProjectContract Project => _snapshot!.Project;
    private bool NativeAudio => Project.Montage.Properties.TryGetValue("clip_audio", out var value) && value.ValueKind == System.Text.Json.JsonValueKind.True;

    internal bool CancelNavigation()
    {
        if (!NeedsFrame && !HasDraft) return false;
        Show(NeedsFrame ? "Дождитесь ответа backend." : "Сохраните дорожку или сбросьте ввод.", true);
        return true;
    }

    private void Close() { if (!CancelNavigation()) _close(); }

    private void Reload(string? track = null, string? id = null, BackendReadinessIssue? issue = null)
    {
        var keepDraft = HasDraft;
        Begin("Читаю дорожки…", async token =>
        {
            var snapshot = await _backend.OpenAsync(token).ConfigureAwait(false);
            return () =>
            {
                _snapshot = snapshot;
                _stale = false;
                _window ??= Project.Montage.Windows.FirstOrDefault()?.Id;
                Show(keepDraft ? "Прочитана актуальная ревизия. Проверьте сохранённые значения перед применением ввода." : $"Ревизия {Project.Revision}");
                RebuildList();
                if (issue is not null && !issue.Matches(snapshot.Project.Revision, snapshot.Fingerprint))
                {
                    Show("Монтаж изменился. Проверьте готовность снова.", true);
                    return;
                }
                if (!keepDraft) Select(track ?? _kind, id ?? _id, _clipSound, _speechForm);
            };
        });
    }

    private void RebuildList()
    {
        Clear(_list);
        foreach (var span in BackendTrackProjection.Spans(Project))
        {
            if (span.Track == "music") continue;
            var label = $"{span.Lane} · {span.Name} · {span.StartFrame}–{span.StartFrame + (long)span.DurationFrames} к.";
            _list.Add(Button(label, () => Select(span.Track, span.Id)));
        }
        _native.Value = NativeAudio ? "Звук видеоклипов включён" : "Звук видеоклипов выключен";
    }

    private void Select(string? track, string? id, bool clipSound = false, bool speechForm = false)
    {
        if (_snapshot is null || NeedsFrame) return;
        if (HasDraft && track is not null)
        {
            if (_kind != track || _id != id || _clipSound != clipSound || _speechForm != speechForm)
                Show("Сохраните дорожку или сбросьте ввод.", true);
            return;
        }
        var montage = Project.Montage;
        var saved = track switch
        {
            "music" => BackendTrackEdits.Object(montage, track),
            not null when id is not null => BackendTrackEdits.Rows(montage, track).OfType<JsonObject>().FirstOrDefault(row => (string?)row["id"] == id),
            _ => null
        };
        var speech = track == "sound_events" ? BackendSpeech.Read(saved?["speech"]) : null;
        (_kind, _id, _clipSound, _speechForm, _delete) = (track, saved is not null ? id : null, clipSound, speechForm || speech is not null, false);
        Clear(_form); _fields.Clear(); _caption = null;
        if (track is null) { _clean = null; Refresh(); return; }
        void Field(string key, string label, string fallback) => AddField(key, label, saved?[key]?.ToString() ?? fallback);
        if (track != "music") Field("name", "Название", track == "titles" ? "Титр" : track == "markers" ? "Маркер" : _speechForm ? "Речь" : "Звук");
        if (track == "sound_events")
        {
            Field("segment_id", "Окно сегмента (ID)", _window ?? "");
            if (!_speechForm)
            {
                Field("clip_id", "Клип (ID, пусто для звука сегмента)", clipSound ? _clip ?? "" : "");
                Field("lane", "Дорожка (voice / sfx / ambience)", "sfx");
            }
            Field("stream", "Звуковой поток", "0");
        }
        if (track is "music" or "sound_events")
        {
            Field("asset_id", "Медиа (ID ассета или @mount/путь)", "");
            if (Project.Properties.TryGetValue("assets", out var assets) && assets.ValueKind == System.Text.Json.JsonValueKind.Object)
            {
                var media = assets.EnumerateObject().Where(row => row.Value.ValueKind == System.Text.Json.JsonValueKind.Object &&
                    row.Value.TryGetProperty("kind", out var value) && value.GetString() is "audio" or "video").Select(row => row.Name).Take(12);
                var hint = string.Join(", ", media);
                if (hint.Length > 0) _form.Add(Text.Plain("Ассеты · " + hint).WithRole(SceneView.MutedRole));
            }
            Field("source_in_frame", "Начало исходника, к.", "0");
            Field("gain", "Громкость, 0–1", "1");
            Field("muted", "Приглушить (да / нет)", "нет");
        }
        var startKey = track == "sound_events" ? "offset_frames" : track == "markers" ? "frame" : "start_frame";
        Field(startKey, track == "sound_events" ? "Смещение от клипа или окна, к." : "Кадр проекта",
            track == "sound_events" ? "0" : Number(_cursor));
        if (track != "markers") Field("duration_frames", "Длительность, к.", Number(Math.Max(1, Math.Round(Project.Fps))));
        if (_speechForm)
        {
            AddField("speech_role", "Роль (narrator / dialogue)", speech?.Role ?? "narrator");
            AddField("speaker", "Говорящий", speech?.Speaker ?? "");
            AddField("action_id", "Действие (ID, пусто для всей сцены)", speech?.ActionId ?? "");
            AddField("speech_version", "Версия записи (0 — добавить)", Number(speech?.SelectedVersion ?? 0));
            AddField("speech_origin", "Происхождение новой записи", speech?.Selected.Origin ?? "");
            if (speech is not null)
            {
                foreach (var version in speech.Versions)
                    _form.Add(Text.Plain($"Версия {version.Number} · {version.Origin} · SHA {version.Sha256[..12]}")
                        .WithRole(SceneView.MutedRole));
                if (BackendSpeech.NeedsReview(speech, (string)saved!["segment_id"]!, (int)saved["offset_frames"]!,
                        (int)saved["duration_frames"]!, (double)saved["source_in_frame"]!))
                    _form.Add(Text.Plain("Субтитр требует сверки"));
            }
            _caption = new() { AccessibleName = "Текст субтитра", Text = speech?.Subtitle?.Text ?? "", MaximumContentHeight = 120 };
            _form.Add(Text.Plain("Текст субтитра"));
            _form.Add(_caption);
            AddField("caption_offset", "Начало субтитра от записи, к.", Number(speech?.Subtitle?.OffsetFrames ?? 0));
            AddField("caption_duration", "Длительность субтитра, к.", Number(speech?.Subtitle?.DurationFrames ?? Math.Max(1, Math.Round(Project.Fps))));
            AddField("confirm_caption", "Субтитр сверён (да / нет)", "нет");
        }
        if (track == "titles")
        {
            _caption = new() { AccessibleName = "Текст титра", Text = (string?)saved?["text"] ?? "", MaximumContentHeight = 180 };
            _form.Add(Text.Plain("Текст титра"));
            _form.Add(_caption);
            Field("fade_in_frames", "Появление, к.", "0");
            Field("fade_out_frames", "Исчезновение, к.", "0");
            Field("font_id", "Шрифт (arial / arial-bold / georgia / times / consolas / segoe)", OperatingSystem.IsWindows() ? "arial" : "dejavu");
            Field("font_size", "Размер, пиксели", "24");
            Field("color", "Цвет (#RRGGBB)", "#FFFFFF");
            Field("align", "Выравнивание (left / center / right)", "center");
            Field("position", "Положение (upper / center / lower)", "center");
            Field("background", "Фон (transparent / black)", "transparent");
            Field("outline", "Обводка (да / нет)", "да");
        }
        _clean = Signature();
        Refresh();
    }

    private void Save() => Run(() => _kind switch
    {
        "music" => new SaveMontageMusic(Value("asset_id"), Int("start_frame"), Int("duration_frames"), Real("source_in_frame"), Real("gain"), Flag("muted")),
        "sound_events" => new SaveMontageSound(_id, Value("segment_id"), _speechForm ? null : Optional("clip_id"), Value("name"),
            _speechForm ? "voice" : Value("lane"), Value("asset_id"), Int("stream"), Int("offset_frames"), Int("duration_frames"),
            Real("source_in_frame"), Real("gain"), Flag("muted"), _speechForm
                ? new(Value("speech_role"), Value("speaker"), Optional("action_id"), Int("speech_version"), Value("speech_origin"),
                    _caption!.Text, Int("caption_offset"), Int("caption_duration"), Flag("confirm_caption"))
                : null),
        "titles" => new SaveMontageTitle(_id, new(Value("name"), _caption!.Text, Int("start_frame"), Int("duration_frames"),
            Int("fade_in_frames"), Int("fade_out_frames"), Value("font_id"), Int("font_size"), Value("color"), Value("align"),
            Value("position"), Value("background"), Flag("outline"))),
        "markers" => new SaveMontageMarker(_id, Value("name"), Int("frame")),
        _ => throw new FormatException("Выберите дорожку.")
    });

    private void Remove()
    {
        if (_kind is null || _id is null) return;
        if (!_delete) { _delete = true; Refresh(); return; }
        Run(() => new RemoveMontageTrackItem(_kind, _id));
    }

    private void Run(Func<MontageTrackCommand> command)
    {
        if (_snapshot is null || _stale || NeedsFrame) return;
        MontageTrackCommand edit;
        try { edit = command(); }
        catch (FormatException error) { Show(error.Message, true); return; }
        var baseline = _snapshot;
        Begin("Сохраняю дорожку…", async token =>
        {
            var saved = await _backend.EditScenarioAsync(edit, baseline, token).ConfigureAwait(false);
            return () =>
            {
                _snapshot = saved;
                _clean = Signature();
                Show($"Ревизия {Project.Revision} · сохранено");
                RebuildList();
                if (edit is not SetMontageClipAudio) Select(null, null);
            };
        });
    }

    public void AdvanceFrame(TimeSpan elapsed, IInputContext context)
    {
        if (_pending is not { IsCompleted: true } complete) return;
        _pending = null;
        try { complete.GetAwaiter().GetResult()(); }
        catch (BackendCommandException error)
        {
            _stale = error.Code is "revision_conflict" or "project_io_error" or "recovery_required";
            Show(error.Code switch
            {
                "revision_conflict" => "Проект изменился. Перечитайте дорожки и проверьте ввод.",
                "montage_audio_source" => "Звуковой поток недоступен или участок выходит за его длительность.",
                "invalid_speech" => "Проверьте роль, говорящего, выбранную версию и границы субтитра.",
                "speech_version_exists" => "Для новой версии выберите другой файл записи.",
                "speech_source_changed" => "Запись отличается от сохранённого SHA этой версии.",
                "invalid_montage_track" => "Проверьте время, привязку к окну, оформление и значения дорожки.",
                _ => error.Message
            }, true);
        }
        catch (Exception error) when (error is IOException or OperationCanceledException or InvalidOperationException or FormatException or System.Text.Json.JsonException)
        { _stale = true; Show(error.Message, true); }
        Refresh();
    }

    private void Begin(string message, Func<CancellationToken, Task<Action>> operation)
    {
        if (NeedsFrame) return;
        Show(message);
        _pending = operation(_closing.Token);
        Refresh();
    }

    private void Refresh()
    {
        _reload.IsEnabled = !NeedsFrame;
        var ready = _snapshot is not null && !NeedsFrame;
        _back.IsEnabled = !NeedsFrame;
        _navigation.IsEnabled = _list.IsEnabled = ready;
        _form.IsEnabled = ready;
        _native.IsEnabled = ready && !_stale;
        _save.IsEnabled = ready && !_stale && _kind is not null;
        _remove.IsEnabled = _save.IsEnabled && _id is not null;
        _discard.IsEnabled = ready && _kind is not null;
        _remove.Value = _delete ? "Подтвердить удаление дорожки" : "Удалить…";
    }

    private string Signature() => string.Join('\0', _fields.Values.Select(field => field.Value).Append(_caption?.Text ?? ""));
    private string Value(string key) => _fields[key].Value.Trim();
    private string? Optional(string key) => Value(key) is { Length: > 0 } value ? value : null;
    private int Int(string key) => int.TryParse(Value(key), NumberStyles.None, CultureInfo.InvariantCulture, out var value)
        ? value : throw NumberError(key);
    private double Real(string key) => double.TryParse(Value(key).Replace(',', '.'), NumberStyles.AllowDecimalPoint,
        CultureInfo.InvariantCulture, out var value) && double.IsFinite(value) ? value : throw NumberError(key);
    private bool Flag(string key) => Value(key).ToLowerInvariant() switch
    {
        "да" or "true" => true,
        "нет" or "false" => false,
        _ => throw new FormatException(_fields[key].AccessibleName + " · укажите да или нет.")
    };
    private FormatException NumberError(string key) => new(_fields[key].AccessibleName + " · укажите число.");
    private static string Number(double value) => value.ToString(CultureInfo.InvariantCulture);

    private void AddField(string key, string label, string value)
    {
        var field = new TextBox { AccessibleName = label, Value = value };
        _fields.Add(key, field);
        _form.Add(Stack(Text.Plain(label).WithRole(SceneView.MutedRole), field));
    }

    private void Show(string value, bool error = false) { _status.Value = value; _status.HasError = error; }
    private static Button Button(string label, Action action) => TrueMoon.Argentis.Button.Simple(label).WithRole(SceneView.CommandRole).OnClick(action);
    private static VStack Stack(params Element[] items) => VStack.Create().Configure(stack => stack.Spacing = 6).WithChildren(items);
    private static HStack Row(params Element[] items) => HStack.Create().Configure(row => row.Spacing = 6).WithChildren(items);
    private static void Clear(VStack stack)
    {
        foreach (var child in stack.Items.ToArray()) { stack.Items.Remove(child); child.Dispose(); }
    }

    public override void Dispose()
    {
        _closing.Cancel();
        if (_pending is { } pending)
            _ = pending.ContinueWith(task => _ = task.Exception, CancellationToken.None, TaskContinuationOptions.OnlyOnFaulted, TaskScheduler.Default);
        _closing.Dispose();
        base.Dispose();
    }
}
