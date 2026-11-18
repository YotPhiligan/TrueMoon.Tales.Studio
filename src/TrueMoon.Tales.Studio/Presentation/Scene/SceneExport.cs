using System.Security.Cryptography;
using System.Text.Json;
using TrueMoon.Tales.Studio.Backend;
using TrueMoon.Tales.Studio.Backend.Export;
using TrueMoon.Tales.Studio.Models;

namespace TrueMoon.Tales.Studio.Presentation.Scene;

// Owned by the window. Only Poll applies asynchronous results to presentation state.
internal sealed class SceneExport(BackendConnection backend, BackendOptions options) : IDisposable
{
    private readonly Lazy<BackendExportStorage> _storage = new(() => new(options));
    private readonly CancellationTokenSource _closing = new();
    private Task<Action>? _pending;
    private TimeSpan _pollTime;
    private bool _track, _loaded, _unresolved;
    private string? _key;

    internal string Profile { get; private set; } = "project";
    internal string Message { get; private set; } = "Проверка выполняется по сохранённому монтажу.";
    internal string? OutputDirectory { get; private set; }
    internal BackendExportCheck? Check { get; private set; }
    internal BackendExportStatus? Job { get; private set; }
    internal bool Busy => _pending is not null;
    internal bool Running => Job?.State is "running" or "stopping";
    internal bool MayInterrupt => Running || _unresolved;
    internal bool NeedsFrame => Busy || _track;
    internal bool CanCheck => _loaded && !Busy && !Running && !_unresolved;
    internal bool CanInspect => !Busy && (_key is not null || !_loaded);
    internal bool HasError { get; private set; }
    internal string? SavedFile { get; private set; }

    internal void Inspect()
    {
        if (Busy) return;
        var currentKey = _key;
        Begin("Читаю состояние экспорта…", async token =>
        {
            var storage = _storage.Value;
            var key = currentKey ?? storage.LoadClientKey();
            BackendExportStatus? job = null;
            if (key is not null)
            {
                try { job = await backend.FindExportAsync(key, token).ConfigureAwait(false); }
                catch (BackendCommandException error) when (error.Code == "export_not_found") { }
            }
            return () =>
            {
                OutputDirectory = storage.ClientOutputDirectory;
                _loaded = true;
                _key = key; _unresolved = false;
                Job = job; _track = Running;
                Message = job is null ? "Выберите профиль и проверьте монтаж." : "";
            };
        });
    }

    internal void SelectProfile(string profile)
    {
        if (!CanCheck || profile == Profile) return;
        Profile = profile; Check = null; HasError = false;
        Message = "Проверьте монтаж для выбранного профиля.";
    }

    internal void Validate(SceneSnapshot scene)
    {
        if (!CanCheck || scene.Fingerprint is not { } sha) return;
        var profile = Profile;
        Check = null;
        Begin("Проверяю сохранённый монтаж…", async token =>
        {
            var check = await backend.CheckExportAsync(profile, scene.Revision, sha, token).ConfigureAwait(false);
            return () => { Check = check; Message = "Монтаж готов к экспорту."; };
        });
    }

    internal bool CanStart(SceneSnapshot? scene) => CanCheck && Check is { } check && scene is not null &&
        scene.Revision == check.ProjectRevision && string.Equals(scene.Fingerprint, check.ProjectFingerprint, StringComparison.OrdinalIgnoreCase);

    internal void Start(SceneSnapshot scene)
    {
        if (!CanStart(scene) || Check is not { } check) return;
        var key = Guid.NewGuid().ToString("N");
        Check = null; Job = null; SavedFile = null; _unresolved = true; _key = key;
        Begin("Отправляю задание…", async token =>
        {
            _storage.Value.SaveClientKey(key);
            var job = await backend.StartExportAsync(key, check.Profile, check.ProjectRevision, check.ProjectFingerprint, token).ConfigureAwait(false);
            return () => { _unresolved = false; ShowJob(job); };
        });
    }

    internal void Cancel()
    {
        if (!Busy && Job is { State: "running" } job)
            Begin("Запрашиваю отмену…", async token =>
            {
                var result = await backend.CancelExportAsync(job.Id, job.Revision, token).ConfigureAwait(false);
                return () => ShowJob(result);
            });
    }

    internal void Recover()
    {
        if (!Busy && Job is { } job)
            Begin("Сверяю сохранённое задание и файл…", async token =>
            {
                var result = await backend.RecoverExportAsync(job.Id, token).ConfigureAwait(false);
                return () => ShowJob(result);
            });
    }

    internal void SaveFile(string name)
    {
        if (Busy || Job is not { State: "completed", ArtifactSha256: { } sha, ArtifactSizeBytes: { } length } job) return;
        Begin("Проверяю и сохраняю MP4…", async token =>
        {
            var storage = _storage.Value;
            if (!ValidFileName(name)) throw new InvalidDataException("Введите имя нового файла с расширением .mp4.");
            var root = storage.ClientOutputDirectory;
            var result = storage.Check(Path.Combine(root, name));
            var temp = storage.Check(Path.Combine(root, ".studio-export-" + Guid.NewGuid().ToString("N") + ".tmp"));
            if (File.Exists(result) || Directory.Exists(result)) throw new IOException("Файл с таким именем уже существует.");
            using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
            var created = false;
            try
            {
                await using (var stream = new FileStream(temp, FileMode.CreateNew, FileAccess.Write, FileShare.None, 65536, FileOptions.Asynchronous))
                {
                    created = true;
                    for (long offset = 0; offset < length;)
                    {
                        var bytes = await backend.ExportChunkAsync(job.Id, sha, offset, token).ConfigureAwait(false);
                        hash.AppendData(bytes);
                        await stream.WriteAsync(bytes, token).ConfigureAwait(false);
                        offset += bytes.Length;
                        if (offset > length) throw new InvalidDataException("Размер MP4 изменился при получении.");
                    }
                    stream.Flush(flushToDisk: true);
                }
                if (!string.Equals(Convert.ToHexString(hash.GetHashAndReset()), sha, StringComparison.OrdinalIgnoreCase))
                    throw new InvalidDataException("SHA-256 MP4 не совпадает с результатом экспорта.");
                token.ThrowIfCancellationRequested();
                File.Move(storage.Check(temp), storage.Check(result));
                created = false;
                return () => { Message = $"Сохранён {name} · SHA-256 совпадает"; SavedFile = result; };
            }
            finally { if (created) File.Delete(storage.Check(temp)); }
        });
    }

    private static bool ValidFileName(string name) => name.Length is > 4 and <= 120 && name == name.Trim() &&
        name.IndexOfAny(Path.GetInvalidFileNameChars()) < 0 && name.IndexOfAny(['/', '\\', ':']) < 0 &&
        string.Equals(Path.GetExtension(name), ".mp4", StringComparison.OrdinalIgnoreCase);

    private void ShowJob(BackendExportStatus job)
    {
        var changedArtifact = Job?.ArtifactSha256 != job.ArtifactSha256;
        Job = job; _track = Running; _pollTime = TimeSpan.Zero;
        if (changedArtifact) SavedFile = null;
        Message = job.State is "failed" or "interrupted" ? Explain(job.ErrorCode) : "";
        HasError = job.State is "failed" or "interrupted";
    }

    private void Begin(string message, Func<CancellationToken, Task<Action>> operation)
    {
        Message = message; HasError = false;
        var token = _closing.Token;
        _pending = Task.Run(() => operation(token), token);
    }

    internal bool Poll(TimeSpan elapsed)
    {
        if (_pending is { IsCompleted: true } completed)
        {
            _pending = null;
            try { completed.GetAwaiter().GetResult()(); }
            catch (Exception error) when (error is IOException or UnauthorizedAccessException or InvalidDataException or
                InvalidOperationException or ArgumentException or OperationCanceledException or JsonException)
            {
                _track = false;
                HasError = true;
                Message = error is BackendCommandException command ? Explain(command.Code) :
                    error is OperationCanceledException ? "Истекло время ожидания. Проверьте статус задания." : error.Message;
            }
            return true;
        }
        if (Busy || !_track || Job is not { } job) return false;
        _pollTime += elapsed;
        if (_pollTime < TimeSpan.FromMilliseconds(750)) return false;
        Begin("", async token =>
        {
            var status = await backend.ExportStatusAsync(job.Id, token).ConfigureAwait(false);
            return () => ShowJob(status);
        });
        return true;
    }

    internal static string StateText(BackendExportStatus job) => job.State switch
    {
        "running" => job.ProgressPercent is { } percent ? $"Рендеринг · {percent}%" : "Рендеринг…",
        "stopping" => "Остановка…",
        "completed" => "MP4 готов",
        "cancelled" => "Экспорт отменён",
        "interrupted" => "Экспорт прерван",
        _ => "Ошибка экспорта"
    };

    private static string Explain(string? code) => code switch
    {
        "revision_conflict" => "Сохранённый монтаж изменился. Перечитайте проект и проверьте его снова.",
        "export_conflict" => "Статус изменился. Прочитайте его перед повторным действием.",
        "media_not_found" => "Материал монтажа недоступен.",
        "access_denied" => "Конфигурация рабочего пространства запрещает доступ к материалу или каталогу.",
        "invalid_export" => "Проверьте материал, диапазоны, FPS, титры и переходы сохранённого монтажа.",
        "export_busy" => "Экспорт уже выполняется. Проверьте его статус.",
        "export_not_found" => "Принятое задание не найдено.",
        "export_timeout" => "Истекло время ожидания. Проверьте статус задания.",
        "export_unavailable" => "Проверьте настройку каталога экспорта и инструментов CPU.",
        "writer_required" => "Откройте проект в режиме редактирования.",
        _ => code is null ? "Откройте статус задания для проверки." : $"Код экспорта · {code}"
    };

    public void Dispose()
    {
        _closing.Cancel();
        if (_pending is { } pending) _ = pending.ContinueWith(task => _ = task.Exception, TaskContinuationOptions.OnlyOnFaulted);
        _closing.Dispose();
    }
}
