# STUDIO-BACKEND3 · HTTP/CLI общего backend

Проверено 17.11.2026 UTC, Windows x64. Studio **1.2611.17.2-alpha** — следующий
UTC rev после опубликованной 1.2611.17.1-alpha / main `f0eae7408043f0989cc8a0000ecaae1b059aac0c`.
Version зафиксирована до финальных build/test/publish. SDK11.0.100/net11.0/
runtime-async=on, TrueMoon1.2611.16.1-alpha сохранены. Enerit protocol2 и
ProjectContract v1 не менялись; новый отдельный HTTP API имеет версию1.

## Владение и контракт

[BackendHttpServer](../TrueMoon.Tales.Studio/Backend/Http/BackendHttpServer.cs)
работает в процессе владельца backend. UI и HTTP используют один BackendConnection
и один owned child EXE/Mithril/Enerit. Адаптер не создаёт ProjectStore или отдельного
writer: запуск `--serve-project` явно открывает schema2 и занимает существующий
writer lease. UI может пользоваться той же сессией. Только backend рассчитывает
перенос и вызывает Save с ревизией/SHA и immutable history.

Сервер — Kestrel через FrameworkReference `Microsoft.AspNetCore.App`, без нового
NuGet-пакета. C# EXE всех ролей теперь требует **.NET11 и ASP.NET Core11 shared
runtime**. Это проверено по publish runtimeconfig; SDK этой машины включает оба.
Связывание только с literal IPv4 `127.0.0.1`, порт0 по умолчанию. appsettings/env
не могут добавить listeners. Перезапуск backend и повтор команд не включаются.

После готовности stdout владельца выдаёт один JSON: api_version, url, отдельный
случайный HTTP token, project_id, owner_pid, backend_pid, product_version.
Это credential сессии, не Enerit capability. Он нужен для **GET и POST** в
`X-Studio-Token`; token/bootstrap по HTTP не выдаётся. Проект выбран trusted
launcher/config, request не задаёт путь, корень, права или другой project ID.
Права, protected/legacy/reparse ограничения и lease предыдущего этапа сохранены.

| Метод/маршрут | Контракт |
|---|---|
| GET `/api/backend/v1/project` | Свежий snapshot: api_version=1, schema_version=2, fingerprint, scenario_active, project=ProjectContract v1 |
| POST `/api/backend/v1/move-clip` | Только clip_id, start_frame, expected_revision, fingerprint; ответ — тот же snapshot после Move либо no-op |

Пример тела: `{"clip_id":"clip-main","start_frame":14,"expected_revision":12,"fingerprint":"<64 hex из GET>"}`.
Fingerprint — SHA **точных байтов файла**, а не хэш JSON, напечатанного CLI.
Actor остаётся `studio:enerit`; произвольный actor в этот v1 request не принимается.
Чтение не мигрирует/не восстанавливает/не пишет; server startup также не мигрирует.
Явные --migrate-project/--recover-project остаются отдельными операциями.

Ошибки содержат error, message и reconciliation_required. Revision/SHA conflict,
writer/recovery/migration refusal дают409, invalid_move422, authority403,
неверный запрос400, метод405, content-type415, размер413; backend unavailable503,
deadline504, перегрузка429. После неопределённого ответа нужно явно перечитать
проект; retry, автоматический GET нового baseline или повтор POST не выполняются.
No-op не повышает revision/history. UI хранит свой конфликтный draft до Cancel/Reload.

HTTP принимает до4 одновременных операций, Kestrel до8 connections; body4096 bytes,
headers8192, request line2048, headers timeout5s, operation deadline12s.
Неизвестные/дублированные поля, query, чужой Host, Origin/Sec-Fetch-Site отвергаются.
Ответы no-store; CORS, redirects, файловый API и произвольные операции не добавлены.

## CLI и запуск

Из `src`, после restore/build по [README](../README.md):

```powershell
# Каталог — заранее явно мигрированная копия с directory grants в trusted config.
./.local/studio-build/bin/TrueMoon.Tales.Studio/release/TrueMoon.Tales.Studio.exe --serve-project <copy> --config <config.json>
# Та же сессия с production UI:
./.local/studio-build/bin/TrueMoon.Tales.Studio/release/TrueMoon.Tales.Studio.exe --serve-project <copy> --config <config.json> --with-ui
```

Параметр `--http-port <0..65535>` выбирает порт; занятый порт не перехватывается.
Процесс работает в foreground до остановки владельца. HTTP останавливается до
backend; авария владельца закрывает listener и приводит к завершению owned child.
Настоящие окна для QA запускать только по [QASessions](../QASessions.md).

Существующий `studio_agent.py` получает две отдельные команды. Python — stdlib only:

```powershell
$env:STUDIO_BACKEND_HTTP_TOKEN = '<token из ready JSON>'
python ../studio_agent.py --url <url из ready JSON> backend-project
python ../studio_agent.py --url <url из ready JSON> backend-move --clip <id> --start-frame 14 --revision 12 --sha256 <fingerprint из snapshot>
```

Вместо переменной можно передать `--token` перед командой. --revision/--sha256
обязательны, --project для новых команд отвергается: проект закреплён владельцем.
CLI проверяет envelope/version/fingerprint, отключает proxy из окружения и redirects,
отправляет одну команду без legacy bootstrap/actor и не повторяет запись. Успех —
exit0, conflict409 — exit3, прочая ошибка — exit1 с automatic_retry=false.
Старые CLI-команды и Python HTTP для schema1 сохраняются. Legacy Web UI ещё не
читает canonical schema2; он не объявляется переключённым на новый HTTP API.

<a id="studio-native-cli"></a>
## C# CLI в том же EXE · 21.11.2026

В **1.2611.21.12-alpha** чтение проекта, перенос и обрезка клипа выполняются
через существующую сессию `--serve-project`. Передайте её адрес
`http://127.0.0.1:<порт>/` из ready JSON. Команды работают в отдельном процессе
того же EXE, без чтения локального профиля или захвата writer.

```powershell
$env:STUDIO_BACKEND_HTTP_TOKEN = '<token из ready JSON>'
$url = '<url из ready JSON>'
$snapshot = .\TrueMoon.Tales.Studio.exe backend-project --url $url | ConvertFrom-Json
# Выберите ID клипа и координаты для своего проекта.
.\TrueMoon.Tales.Studio.exe backend-move --url $url --clip clip-main --start-frame 14 --revision $snapshot.project.revision --sha256 $snapshot.fingerprint
# Перед другой правкой прочитайте актуальную ревизию и SHA.
$snapshot = .\TrueMoon.Tales.Studio.exe backend-project --url $url | ConvertFrom-Json
.\TrueMoon.Tales.Studio.exe backend-trim --url $url --clip clip-main --start-frame 14 --source-in 8 --source-out 48 --revision $snapshot.project.revision --sha256 $snapshot.fingerprint
```

Токен можно также задать через `--token`. `backend-trim` принимает `--start`
как прежнее имя `--start-frame`. При успехе stdout содержит JSON снимка HTTPv1.
Ошибки печатаются как JSON в stderr. Коды выхода: 0 — успех, 2 — параметры,
3 — конфликт, 1 — прочие ошибки, 130 — прерывание пользователем.
Для чтения и правки проекта отправляется один запрос на команду. При
`reconciliation_required: true` после правки сначала перечитайте проект.

С **1.2611.21.13-alpha** метаданные и кадр тоже можно получить из EXE.
Используйте `asset_id` материала и актуальный снимок.

```powershell
$snapshot = .\TrueMoon.Tales.Studio.exe backend-project --url $url | ConvertFrom-Json
.\TrueMoon.Tales.Studio.exe backend-media --url $url --asset video-a --revision $snapshot.project.revision --sha256 $snapshot.fingerprint
.\TrueMoon.Tales.Studio.exe backend-frame --url $url --asset video-a --frame 20 --revision $snapshot.project.revision --sha256 $snapshot.fingerprint --output .\frame-20.png
```

`--frame` — индекс кадра исходника с нуля. `backend-media` возвращает FPS,
размер и число кадров, поле пикселей пустое. `backend-frame` сверяет SHA
пикселей, записывает PNG размером 480 × 270 в существующую папку и возвращает
`output_path` и `png_sha256`. Путь должен указывать на новый файл.

С **1.2611.21.14-alpha** запуск, статус, отмена и скачивание MP4 доступны из
того же EXE. При потере ответа запуска или отмены проверьте статус указанного
в ошибке задания. С **1.2611.21.15-alpha** `backend-export-recover` явно
восстанавливает статус по ID после перезапуска backend.
[Команды экспорта](BackendExport.md#native-export-cli).

<a id="studio-native-agent-context-cli"></a>
## Контекст проекта и блока из C# CLI

С **1.2611.22.3-alpha** используйте адрес и токен существующей сессии
`--serve-project` для проекта schema2. Выберите ID сценарного действия
или ID источника. Также поддерживаются точный заголовок действия и
точная метка источника.

```powershell
$env:STUDIO_BACKEND_HTTP_TOKEN = '<token из ready JSON>'
$url = '<url из ready JSON>'
.\TrueMoon.Tales.Studio.exe backend-agent-context --url $url
$result = .\TrueMoon.Tales.Studio.exe backend-agent-context --url $url --block 'Действие A' | ConvertFrom-Json
$result.context.project.revision
$result.context.fingerprint
$result.context.block
```

CLI сверяет SHA снимка и селектор перед выдачей JSON. Неоднозначность
возвращает код выхода 3, отсутствие блока — 1. Повторный запрос
читает последнюю сохранённую ревизию проекта. [Формат ответа](../../API.md#native-agent-context).

<a id="native-transfer-cli"></a>
## Подготовка генерации из C# CLI

Команды появились в **1.2611.22.1-alpha**. `--form` принимает UTF-8 JSON с
полями [формы Full Transfer](../../API.md#native-transfer-http).
Передайте `client_key` из 32 строчных hex-цифр, ID источника и чернового
дубля, seed, промпт и упорядоченные референсы с ролями. Выберите Guide 0
через `guide_picture` и опишите роль и движение Video 1.

```powershell
$snapshot = .\TrueMoon.Tales.Studio.exe backend-project --url $url | ConvertFrom-Json
$job = .\TrueMoon.Tales.Studio.exe backend-transfer-prepare --url $url --form .\transfer-form.json --revision $snapshot.project.revision --sha256 $snapshot.fingerprint | ConvertFrom-Json
.\TrueMoon.Tales.Studio.exe backend-transfer-stage --url $url --id $job.id --request-sha256 $job.request_sha256 --revision $snapshot.project.revision --sha256 $snapshot.fingerprint
.\TrueMoon.Tales.Studio.exe backend-transfer-job --url $url --id $job.id --revision $snapshot.project.revision --sha256 $snapshot.fingerprint
```

Все три команды возвращают JSON квитанции с `enqueued: false`. Проверка
входов сохраняет `Staged.json`. Файлы для ComfyUI копируются в Temp при
отдельном запуске. При `reconciliation_required: true`
перечитайте проект и запросите квитанцию по ID `ft-<client_key>`,
который также указан в сообщении ошибки.

<a id="native-transfer-run-cli"></a>
## Запуск, статус, остановка и сбор

С **1.2611.22.2-alpha** после проверки входов вы можете запустить задание
явной командой. Используйте тот же URL и токен сессии `--serve-project`,
ID задания и SHA запроса из квитанции подготовки.

```powershell
.\TrueMoon.Tales.Studio.exe backend-transfer-launch --url $url --id $job.id --request-sha256 $job.request_sha256 --revision $snapshot.project.revision --sha256 $snapshot.fingerprint
.\TrueMoon.Tales.Studio.exe backend-transfer-status --url $url --id $job.id --request-sha256 $job.request_sha256
# Остановка активного задания
.\TrueMoon.Tales.Studio.exe backend-transfer-stop --url $url --id $job.id --request-sha256 $job.request_sha256 --revision $snapshot.project.revision --sha256 $snapshot.fingerprint
# Сбор задания со статусом completed
$snapshot = .\TrueMoon.Tales.Studio.exe backend-project --url $url | ConvertFrom-Json
.\TrueMoon.Tales.Studio.exe backend-transfer-collect --url $url --id $job.id --request-sha256 $job.request_sha256 --revision $snapshot.project.revision --sha256 $snapshot.fingerprint
```

Готовый дубль автоматически регистрируется, когда writer может сохранить
результат. `backend-transfer-collect` проверяет MP4 по SHA и возвращает
снимок проекта. Если дубль уже зарегистрирован, повтор возвращает
его без новой ревизии.

При потере ответа запуска, остановки или сбора проверьте статус. После
перезапуска `--serve-project` используйте его новый URL и токен. Сессия
подбирает готовый результат по квитанции, а незавершённый запуск остаётся
`interrupted`. Повторная команда запуска с тем же ID отклоняется.
[Контракт HTTP](../../API.md#native-transfer-run-http).

## Проверки

| Требование | Тест |
|---|---|
| Общий writer, exact baseline, одна history/no-op, независимые revision/SHA отказы | `BackendHttpTests.HttpMove_UsesSharedWriterAndExactBaseline_OneHistoryAndNoop_RejectsRevisionAndShaIndependently` |
| Authority, routes, malformed/duplicate/missing/unknown/oversized JSON | `BackendHttpTests.Http_RefusesInvalidAuthorityRoutesAndPayloads_WithoutProjectMutation` (11) |
| Domain/recovery/backend exit, без мутации | `BackendHttpTests.Http_ReportsDomainRecoveryAndDisconnectedBackend_WithoutRetryOrMutation` (3) |
| Нет auto migration, listener закрывается, writer остаётся у родителя | `BackendHttpTests.HttpStartup_DoesNotMigrateLegacy_AndStopClosesListenerWhileParentStillOwnsWriter` |
| Занятый порт сохраняется | `BackendHttpTests.HttpStartup_LeavesOccupiedPortAndProjectIntact` |
| Явный bounded serve-mode | `BackendHttpOptionsTests.ServeMode_IsExplicitAndBounded_AndDoesNotChangeManagedChildProtocol` (11) |
| Настоящие UI/CLI, один owned PID, конфликт во время drag и взаимное перечитывание | `BackendUiCliTests.UiAndCli_ShareWriter_ConflictDuringDrag_ReloadAndObserveEachOthersCommits` (raster1/1.5/2) |
| Настоящий entrypoint EXE/CLI и owner exit cleanup | `BackendHttpProcessTests.ServeEntrypoint_AdvertisesScopedApi_CliMovesThroughOwnedChild_AndOwnerExitReapsIt` |
| Точный CLI POST без bootstrap/actor | `BackendCliTests.test_move_sends_one_exact_observed_baseline_without_bootstrap_or_actor` |
| GET с token | `BackendCliTests.test_read_is_authenticated_and_does_not_call_legacy_bootstrap` |
| Conflict/transport/lost reply без retry | `BackendCliTests.test_conflict_transport_error_and_lost_reply_never_retry` |
| Ошибки до запроса | `BackendCliTests.test_invalid_baseline_and_project_selection_fail_before_request`, `test_invalid_endpoint_or_token_never_creates_http_client` |
| Недостоверный ответ без retry | `BackendCliTests.test_invalid_success_envelope_requires_reconciliation_without_retry` |
| Настоящий redirect не выполняется, env proxy не используется | `BackendCliTests.test_redirect_is_not_followed_and_environment_proxy_is_not_used` |

C# классы HTTP/Options/Process — `TrueMoon.Tales.Studio.Tests.Backend`, UI —
`TrueMoon.Tales.Studio.Tests.Raster`; Python — `tests/test_backend_cli.py`.
Новые32 C# cases и7 Python tests. Финальные non-incremental Debug/Release:
**каждый370 passed, 0 failed, 5 прежних skipped**; Python **295 tests, OK**.
Published Release consumer: **37 passed, 0 failed, 0 skipped**. Python для
interop/UI/CLI задан явно через STUDIO_TEST_PYTHON; без него эти cases opt-in skipped.
Builds:0 errors, прежние24 generated warnings Enerit (19CS1591/5CS8669), без новых
handwritten warnings/подавлений. [Существующая заявка генератору](../../../TrueMoon/src/docs/requests/enerit/20261116-studio-generated-xml-docs-f2ad173c/Request.md).

Из `src` целевой прогон после restore (выбрать установленный Python):

```powershell
$previous = $env:STUDIO_TEST_PYTHON
try {
    $env:STUDIO_TEST_PYTHON = 'E:/ComfyUI_windows_portable/python_embeded/python.exe'
    dotnet test TrueMoon.Tales.Studio.Tests/TrueMoon.Tales.Studio.Tests.csproj --no-restore --configuration Debug --artifacts-path .local/studio-build --filter 'FullyQualifiedName~BackendHttp|FullyQualifiedName~BackendUiCliTests' '-bl:.local/backend3-target-{}.binlog'
    if ($LASTEXITCODE -ne 0) { throw 'Backend3 checks failed' }
} finally { $env:STUDIO_TEST_PYTHON = $previous }
```

## Evidence и границы

`src/.local/backend3-final-de79aa27e4fc4e8693c102831e3eb55e/` содержит full-debug.trx,
full-release.trx, published.trx, build/test/publish logs, PNG/manifests для
Debug/Release/publish, runtimeconfig и acceptance/cleanup JSON. Binlogs отдельных
MSBuild вызовов — `src/.local/backend3-*.binlog`, Python log — backend3-python.log.
Published EXE проверен с CLI из этой рабочей копии; standalone Python-скрипт по-прежнему
поставляется в корне Studio, не внутри dotnet publish.
Сверены 87 одинаковых PNG Debug/Release и36 кадров published consumer с Release;
manifest подтверждают disposal дерева/session. Extended Skia проверен по SHA
в `publish/truemoon-native/win-x64/libSkiaSharp.dll`. cleanup.json фиксирует
0 оставшихся owned EXE,0 CLI helpers и0 owned TCP listeners.

UI использует production Cobalt/IFactory и CPU Skia с прежними фиксированными
шрифтами. CLI — отдельный настоящий Python process, HTTP/Kestrel, Enerit/Mithril,
ProjectStore и история настоящие. Snapshot при12 → CLI move/revision13 → конфликт
UI draft без записи → Cancel/Reload13 → UI move14 → CLI видит14 и no-op сохраняет14.
Это не physical Escape/native/DPI, не media preview/export или CPU MP4 parity.
Backend-редактор остаётся move-only; media resolver/aliases/trim/export — следующий
этап. Авторские проекты, GPU/ComfyUI и TrueMoon-код не затронуты.

Все созданные HTTP listeners, CLI и backend процессы ограничены fixture/deadline
и освобождаются в finally; UI освобождается на owner до async stop. Исторические
отчёты сохраняют свои версии. Коммит/push этим этапом не выполнялись.
