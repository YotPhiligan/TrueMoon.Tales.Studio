# Статус разработки Studio

Обновлено 17.11.2026 (STUDIO-BACKEND6; прежние разделы сохраняют свои даты). Состояние рабочей копии Studio. [План и ближайшая очередь](Plan.md#ближайшая-очередь) · [Модель проекта](Scenario.md#структура-проекта).

После завершения этапа обновлять дату, отмечать доступные изменения, результаты проверок и оставшиеся ограничения. Приоритеты и критерии реализации вести в Plan.md.

<a id="backend-trim"></a>
## STUDIO-BACKEND6 — обрезка через общий backend, 17.11.2026

Studio **1.2611.17.6-alpha**, TrueMoon **1.2611.17.1-alpha**, SDK11/net11/runtime-async.
Новый набор включает изменения BACKEND5 и использует следующий rev6;
main d613e87/17.4 сверён перед выбором версии. [Контракт и evidence](src/Verification/BackendTrim.md).

- Trim проходит через единственный writer того же owned EXE/Mithril/Enerit,
  protocol5; expected_revision/SHA и server-side source metadata обязательны.
  Один commit/history, no-op без записи; schema2/ProjectContractv1/HTTPv1 сохраняются.
- Общий расчёт UI/backend показывает локальный draft и scenario 1:1 reflow.
  Фактическая длина клипа отделена от Action.PlannedDurationFrames; protected gap,
  anchors, окно и неизвестные поля сохраняются. M:N editing/split не вводятся.
- UI: оба края, отмена, conflict draft и явное перечитывание; response проверяется
  против рассчитанного draft. HTTP/CLI trim использует ту же backend-сессию.
- CPU MP4 acceptance относится к отдельной synthetic scene через действующий
  DraftRenderer; это не новый backend-export и не полная авторская media acceptance.

Финальные Debug/Release: **по710 passed,0 failed,5 прежних skipped**;
published EXE: **94 passed,0 failed,0 skipped**; Python: **323 tests,OK**.
Новые87C#cases,10raster/16HTTP;231frozen inputs после проверки не изменились.
Обе strict XML/emit сборки:0errors/9generatedCS8669 прежней категории, без suppressions.
Сравнение selected fixture с действующим CPU DraftRenderer прошло по PTS,
H.264/AAC, кадрам с frame ID, чёрным границам и PCM; пределы и evidence в отчёте.

Следом — backend CPU export с immutable request/job, status/cancel/recovery и SHA
артефакта. Физический ввод, Windows DPI и аппаратный audio/native профиль отдельно.
Предыдущий редкий FocusLost timeout и границы его диагностики сохраняются в BACKEND5.

<a id="backend-playback"></a>
## STUDIO-BACKEND5 — просмотр клипа со звуком, 17.11.2026

Studio **1.2611.17.5-alpha**, TrueMoon **1.2611.17.1-alpha**, SDK11/net11/runtime-async.
Версия зафиксирована до финальной приёмки; актуальный main d613e87/1.2611.17.4-alpha
сверен через GitHub. [Контракт, результаты и evidence](src/Verification/BackendPlayback.md).

- UI использует playback-сессии того же owned backend EXE/Mithril/Enerit,
  protocol4. Storage schema2, ProjectContractv1 и HTTPv1 сохранены.
- Непрерывный CFR480×270BGRA, source-in/out/speed/FPS, очередь3; native/music/events
  потоково смешиваются backend в48kHz stereo f32le с gain/mute и absolute sample grid.
  UI получает bounded packets с SHA; пути и декодеры остаются за trusted resolver.
- Play/pause/resume/seek подключены в production Cobalt SceneView; точный курсор
  проекта не восстанавливается с потерей кадра из округлённого source frame.
  Sample clock стартует после первого кадра; смена клипа/Reload/focus/close останавливают поток.
- Каждый Open/Read проверяет revision/SHA до/после данных. Конфликт требует Reload;
  просмотр не меняет проект, историю и writer. Cleanup ограничен дедлайнами;
  потерянный Open-ответ освобождается по idle lease<=20s, автоматического повтора нет.

- Финальные Debug/Release: **по623 passed,0 failed,5 прежних skipped**;
  published EXE: **68 passed,0 failed,0 skipped**; Python: **312 tests,OK**.
  Новые100C#cases,14сквозных;221frozen inputs после проверок не изменились.
  Strict XML/emit builds0errors/9generatedCS8669 закреплённого Enerit, без подавлений.
- Промежуточный FocusLost cleanup-timeout не повторился в финальных D/R/publish
  с10 последовательными focus cycles каждый. Его отдельная причина не установлена;
  усиление Close/регрессия и диагностическая граница описаны в отчёте.

| Контур | Текущая граница |
|---|---|
| Python/Web schema1 | Прежние монтаж, генерация и MP4; историческая приёмка относится к своим датам |
| C# backend schema2 | Открытие, миграция/recovery, sole writer, перенос, HTTP/CLI, resolver/кадры и просмотр выбранного клипа со звуком |
| Целевая модель | DTO/storage поддерживают действия/источники/клипы; полный M:N редактор и split с сохранением действия ещё открыты |

Следом — backend trim/export с revision/SHA и CPU MP4 parity. Непрерывный монтаж
нескольких клипов, VFR, переходы/титры, legacy Web schema2, physical input/DPI и
аппаратная AV-задержка остаются отдельными проверками. Исторические версии и
числа тестов ниже сохраняются. [План](Plan.md#backend-playback).

<a id="backend-media-frames"></a>
## STUDIO-BACKEND4 — media resolver и кадры, 17.11.2026

Studio **1.2611.17.4-alpha**, TrueMoon **1.2611.17.1-alpha**, SDK11/net11/runtime-async.
Версия зафиксирована до финальной приёмки и выше main dcba204/1.2611.17.3-alpha
по NuGet comparison. [Контракт, команды и evidence](src/Verification/BackendMedia.md).

- Protocol3: metadata/один CFR-кадр по asset ID и observed revision/SHA через
  существующий owned backend. Baseline проверяется до/после decode; read-only
  процесс читает кадр даже при writer другой сессии. JSON/revision/history/IDs
  не меняются, writer не перехватывается; schema2/project-contract1 сохранены.
- Trusted resolver поддерживает mounts, relative/absolute/named aliases до IO
  disabled legacy; protected/allowlist/traversal/reparse/device проверки.
  FFmpeg/ffprobe bounded/owned CPU; выход480×270BGRA +pixel SHA. Raw-memory codec
  копирует/возвращает rental; frame cache на диске не создаётся.
- HTTPv1 media-info/frame и CLI backend-media/backend-frame с token, строгими
  полями/types/длиной/SHA и PNG в новый файл через xb; no proxy/redirect/retry.
  Optional config ffmpeg_executable/ffprobe_executable выбирает tools доверенно.
- UI показывает настоящий кадр и перематывает линейкой/кнопками с source-in/out,
  разными fps и speed. Trim/playback не включены. Missing media сохраняет
  metadata editing, переход на него очищает прежнее изображение.
- Финальные D/R: **по523 passed,0 failed,5 прежних skipped**; Python **312 tests,OK**;
  published EXE **54 passed,0 failed,0 skipped**. Новые153C#cases и17Python tests.
  Strict XML/emit builds0errors/7generatedCS8669 (5прежних+2нового DTO), без NoWarn.
- UI/CLI кадры5/20/64 совпали с независимым full decode; actual preview crop,
  разные реальные asset files, late baseline conflict и stop/cancel/EOF/overflow
  cleanup проверены. 99PNG D/R и48publish совпали; native/font SHA и disposal
  подтверждены. Авторские данные, GPU, TrueMoon-код и native окна не затронуты.

Следом — непрерывное воспроизведение и звук; backend trim/export/полная MP4 parity,
legacy Web UI schema2, physical Escape/keyboard/DPI/long timeline и прежний native
AV остаются открытыми. [План](Plan.md#backend-media-frames).

<a id="framework-requests-acceptance"></a>
## STUDIO-REQUESTS1 — приёмка заявок, 17.11.2026

Studio **1.2611.17.3-alpha**, TrueMoon **1.2611.17.1-alpha**. STUDIO-BACKEND3
закоммичен и отправлен в main как c78ab18 /1.2611.17.2-alpha; текущий набор —
отдельное обновление зависимостей и Source-режима package bootstrap.

Принят ENE6e: generated XML docs исправлены,19CS1591 исчезли. Остаются5CS8669
из существующего nullable backlog; NoWarn не добавлен. Cobalt fixes и SplitView
уже были приняты; их graph/lifecycle/layout/routed checks повторены. Список заявок
актуализирован; подключение панелей отделено от их физической приёмки.

- Полный комплект12packages собран из зафиксированного framework commit22d7f11
  в изолированном каталоге Studio. Native SHA прежний; SHA/nuspec/dependency graph
  и matching generators проверены. Параллельный TrueMoon checkout не изменялся,
  внешняя публикация пакетов не заявлена.
- Non-incremental strict XML/emit builds D/R:0errors/5warnings(CS8669).
  Собственный undocumented public member даёт ожидаемый CS1591 в обоих negative controls.
- Полные D/R: **по370 passed,0 failed,5 прежних skipped**; Python **295 tests,OK**;
  published Release EXE **37 passed,0 failed,0 skipped**. Настоящие UI/CLI/HTTP/
  Enerit/Mithril/writer и конфликты revision/SHA проверены.
- PNG87D/R и36publish совпали;87Release-кадров совпали с BACKEND3 на прежнем
  комплекте. Все raster root/session освобождены. Авторские данные/GPU/native
  окна не использовались; физический ввод/DPI/media/MP4 остаются открытыми.

[Команды/evidence/cleanup и границы](src/Verification/FrameworkRequests.md) ·
[План](Plan.md#framework-requests-acceptance). Следующий функциональный этап —
backend media resolver/aliases и предпросмотр; редактор остаётся move-only.

<a id="backend-http-cli"></a>
## STUDIO-BACKEND3 — HTTP/CLI и одна сессия UI, 17.11.2026

Studio **1.2611.17.2-alpha**, следующий UTC rev после1.2611.17.1-alpha/main f0eae74.
SDK11/net11/runtime-async, TrueMoon1.2611.16.1-alpha и Enerit protocol2 сохранены.
C# EXE теперь требует .NET11 **и ASP.NET Core11 shared runtime** (FrameworkReference,
новых NuGet-пакетов нет). Version зафиксирована до финальных build/test/publish.

--serve-project поднимает versioned HTTP v1 на127.0.0.1, --with-ui добавляет окно
в той же сессии. HTTP и UI используют один BackendConnection и единственный
owned Mithril/Enerit writer. Чтение и Move принимают/возвращают canonical snapshot;
для Move обязательны expected_revision и SHA точных байтов. Автоматических retry,
нового baseline для записи, миграции/recovery и нового writer в адаптере нет.

- studio_agent.py получил backend-project/backend-move, token и required revision/SHA,
  валидацию ответов, отказ от redirects/env proxy и записи без исходного baseline.
  Прежние Python HTTP/CLI schema1 остаются; legacy Web UI ещё не переключён.
- Loopback token нужен для GET/POST, проверяются Host/Origin/размер/форма запроса.
  Пути и права выбираются только trusted launcher/config. Ошибки conflict409,
  invalid_move422, backend unavailable503 и прочие классифицированы.
- 32 новых C# cases +7 Python tests. Полные D/R: **по370 passed,0 failed,5 skipped**;
  Python **295 tests,OK**; published Release EXE **37 passed,0 failed**.
  Non-incremental builds0 errors/24 прежних generated warnings Enerit; подавлений нет.
- Сквозной production UI+raster и отдельный CLI process: один child PID, CLI commit
  во время drag, UI conflict draft, Cancel/Reload, последующий UI commit и CLI read/no-op.
  Scale1/1.5/2; HTTP entrypoint, occupied port, auth/limits и owner exit cleanup прошли.
- PNG просмотрены; listeners/UI/backend/CLI освобождены. Авторские данные,
  TrueMoon-код и GPU/ComfyUI не затронуты; native/physical/DPI/MP4 не объявлены закрытыми.

Далее — backend media resolver/aliases, preview/trim/export. Новый API не превращает
move-only редактор в полный монтажный backend. [План](Plan.md#backend-http-cli) ·
[Контракт, запуск, проверки и evidence](src/Verification/BackendHttp.md).

<a id="backend-move-ipc"></a>
## STUDIO-BACKEND2 — перенос через Enerit и передача writer, 17.11.2026

Studio **1.2611.17.1-alpha** (UTC17.11, rev1); TrueMoon1.2611.16.1-alpha,
SDK11/net11/runtime-async сохранены. Version зафиксирована до финальных проверок.

MoveClip принимает ID/start, expected_revision и exact-byte SHA; сервер использует
ту же TimelineMove-раскладку, один ProjectStore.Save и immutable history. При scenario
перестановка Actions и reflow клипов сохраняют материал, gaps/anchors и другие окна.
No-op и отмена не пишут. Конфликт/потерянный ответ сохраняют draft без повторной отправки.

- Явный --edit-project и grants каталога включают writer; read-only режим сохранён.
  Backend удерживает OS lease; Python commit берёт тот же lease. После явного
  --migrate-project старый Python get/commit отклоняет schema2 даже после release.
  --recover-project восстанавливает durable intent один раз. Protocol2/Contract1.
- ISceneService подключает прежний SceneView к Enerit в move-only режиме.
  Медиа/trim/export ещё не подключены; Python HTTP/CLI работают со schema1.
  Проверки используют изолированные синтетические копии, авторские данные не менялись.
- 34 новых cases. Полные non-incremental D/R: по **338 passed, 0 failed, 5 skipped**;
  Python **288 tests, OK**; published Release EXE **16 passed, 0 failed**.
  Сборки0 errors, 24 generated warnings Enerit:19 CS1591/5 CS8669 (четыре новых
  метода добавили четыре прежних XML-doc предупреждения, подавления нет).
- Сквозной raster/IPC без Computer Use: настоящий EXE/Mithril/Enerit/ProjectStore,
  Cobalt SceneView, scale1/1.5/2, Escape, одна запись, конфликт точных байтов,
  lost reply и reload. Межъязыковой lease, concurrent commands и crash release прошли.
  Native/physical/DPI и CPU MP4 media-путь этим не закрыты.
- Все созданные UI fixtures/процессы освобождены; GPU/ComfyUI/native QA не запускались.
  Исходники TrueMoon и его пакеты не менялись.

Далее — адаптер HTTP/CLI общего сервиса и backend media resolver/preview/export.
[План](Plan.md#backend-move-ipc) · [команды, evidence и границы](src/Verification/BackendMove.md).

<a id="ui-raster-harness"></a>
## STUDIO-UIQA1 — raster-стенд без Computer Use, 17.11.2026

Добавлены тестовый RasterWindow/IWindowController, настоящие SceneView и ProjectWindow
из Cobalt/IFactory, CPU Skia с фиксированными OFL-шрифтами и PNG/manifest evidence.
Routed drag/Escape/focus/resize проверяются вместе с данными и командами;
loading/error/reload, close/veto/error/cancel и disposal — отдельными сценариями.

- 18 новых checks. Полные Debug/Release: каждый **304 passed, 0 failed, 5 skipped**.
  Non-incremental builds: 0 errors, прежние 20 generated warnings Enerit;
  новых предупреждений нет. Temp cleanup отдельно: 6 passed в Release.
- На logical scale 1/1.5/2 создаются реальные PNG. Кириллица и Ё проверены;
  draft меняет пиксели клипа, отмена возвращает их без записи, release отправляет
  одну команду с expected_revision, конфликт сохраняет черновик.
- Font/native SHA, версии и результаты освобождения дерева/session сохраняются
  в manifest. PNG просмотрены; некоторые служебные символы не покрыты тестовым
  default font, длинный inspector-текст обрезается. Межверсионные goldens и полная
  visual acceptance не заявлены; native/physical/DPI и CPU MP4 остаются отдельно.
- UI-сервисы здесь управляются doubles. Настоящий Enerit/backend проверен
  существующими отдельными process tests; единый сквозной raster/IPC case впереди.
  Новых native окон/QA-сессий, GPU/ComfyUI и авторских данных этот этап не использует.
- 56 production-входов backend-этапа совпали по SHA. Studio **1.2611.16.3-alpha**
  сохранена: только тесты/тестовые assets и документация.

Далее — [STUDIO-BACKEND2](Plan.md#backend-readonly), общий сервис и передача writer.
[Запуск, матрица и evidence](src/Verification/UIRaster.md) · [инструкция](src/UITesting.md).

16.11.2026 — документация [STUDIO-UIQA-DOC](Plan.md#ui-testing-guidance):
закреплены [правила UI-проверок](src/UITesting.md) и общая инструкция фреймворка,
добавлены ссылки из AGENTS/README/QA. Сверены API, тестовые примеры и ссылки;
новых запусков build/test/native нет. На дату этого документационного этапа raster fixture/тестовый host ещё
предстояли (реализованы выше в STUDIO-UIQA1); ранее открытая visual/physical приёмка сохраняет свои границы.
Версия Studio 1.2611.16.3-alpha не менялась (документация).

<a id="backend-readonly"></a>
## STUDIO-BACKEND1 — backend того же EXE и чтение через Enerit, 16.11.2026

Studio **1.2611.16.3-alpha**; TrueMoon **1.2611.16.1-alpha**, SDK11/net11/runtime-async сохранены.
Добавлены три exact-version пакета: Enerit, его generator и Mithril, SHA/nuspec
из ENG7B3 сверены; полный локальный набор теперь 12 архивов.

`--project <каталог> --config <workspace.json>` открывает read-only окно,
`--inspect-project` читает без окна. Mithril запускает backend-экземпляр того же EXE;
Enerit проверяет роль, версии, parent/owned PID и capability перед OpenProject.
Корень выбирает доверенный launcher; protected/disabled legacy/reparse запрещены,
write grants пусты. Schema 1/2 читается без миграции и recovery. HTTP-редактор
и его существующее владение записью сохранены; новые монтажные команды ещё впереди.

- Новые backend checks: Passed 39, Failed 0, Skipped 0. Полные Debug/Release:
  каждый Passed 286, Failed 0, Skipped 5. Publish process/IPC: Passed 7, Failed 0.
- Полные D/R builds: 0 errors, по 20 generated warnings Enerit (15 CS1591, 5 CS8669).
  XML docs — [заявка владельцу](../TrueMoon/src/docs/requests/enerit/20261116-studio-generated-xml-docs-f2ad173c/Request.md);
  nullable context уже в backlog модуля. Предупреждения не подавлены.
- Реальные own-child startup/stop, failure cleanup, owner exit/crash, сохранность
  другого одноимённого процесса и reconnect с ревизией 11→12 прошли; deadline и отмена проверены.
- Прежняя изолированная копия Клэр: schema1/revision55, 55 sources/62 takes/2 windows,
  SHA неизменен. Авторский проект и медиа не затронуты.
- Cobalt создаёт окно; UiSession/routed input подтверждают снимок и перечитывание.
  Native QA 0abc7b377d504d60a4eaaebc11a5662c остановлена по ID, потомков нет.
  Computer Use отказал в активации окна; визуальная native-приёмка остаётся открытой.
  QA lifecycle: 13 Python checks, OK. GPU/ComfyUI не запускались.

Далее — [STUDIO-BACKEND2](Plan.md#backend-readonly): одна прикладная команда с
expected_revision и явная передача writer на копии. Длинный таймлайн, DPI/keyboard
и изолированная аппаратная отмена Escape сохраняются отдельными пунктами.
[Запуск, тесты и границы](src/Verification/BackendReadOnly.md).

<a id="clip-move-within-segment"></a>
## STUDIO-MOVE — перенос целого клипа, 16.11.2026

Studio Version=1.2611.16.2-alpha, следующий дневной UTC rev после 1.2611.16.1-alpha.
Перед выбором номера сверена remote main: 002a47b0d7fbc0e2b2959a11bdf11791cafefc7e.
Version зафиксирована до финальной проверки; SDK/net11/runtime-async и закреплённая
TrueMoon 1.2611.16.1-alpha сохранены. Python product labels читают общую Version.

Drag тела клипа с порогом 4 px ведёт локальный черновик; линейка и короткий клик
остаются seek, края — trim. Отображаются перестановка и reflow соседей сценария
с сохранением gap/anchors, либо свободная допустимая позиция plain timeline.
Одна запись update_clip передаёт только id/start_frame и исходный expected_revision;
source in/out, speed, take, ID и окно сохраняются. No-op и отмена не пишут проект.
409/ошибки/неопределённый ответ сохраняют черновик и требуют discard/reload,
автоматического retry нет. Действующий HTTP API и schema проекта не менялись.

- 57 новых deterministic cases: расчёт раскладки, сохранность материала,
  routed gesture/cancel/capture/geometry, no-op, 409/503/422 и повреждённый ответ.
- Полные non-incremental D/R builds: 0 warnings / 0 errors.
  Каждый suite: Passed: 247, Failed: 0, Skipped: 5. Новый opt-in HTTP case
  отдельно: Passed: 1, Failed: 0, Skipped: 0. Python HTTP/version — 10 tests, OK.
- Реальный Python HTTP на синтетической QA-копии: перенос 6 → 7, одна history/undo,
  точные предыдущие байты; отмена не пишет. Внешняя правка подняла revision до 8,
  устаревший перенос вернул 409 без потери внешней правки; discard/reload прочитал 8.
- CPU MP4: 24 кадра/24 fps, проверены 18 кадров материала и 6 чёрных;
  max MAE=5.8259491 < 8. JSON, окна/связи/история и SHA исходных MP4 сохранены.
  Все три собственные QA-сессии остановлены по ID, tool sessions завершены.

Основная приёмка ограничивалась routed input/HTTP/CPU; дополнительная native QA
описана ниже. Полные keyboard/DPI, длинная шкала и latency остаются открытыми;
старые native/device результаты не переносятся на этот этап.
Авторские проекты, GPU/ComfyUI и код TrueMoon не затронуты. Далее — длинный
таймлайн и физический ввод, затем выбранный backend/IPC с одним владельцем записи.
Дополнение native QA после push 871e451: mouse drag в настоящем Release-окне
прошёл, 6 → 7, одна запись/undo/history и точные прежние байты. Обратный drag
сохранил 8. После ручной попытки Escape JSON/SHA, revision=8, history и POST не
изменились. Escape остановил Computer Use и мог вызвать FocusLost, поэтому
изолированная native-приёмка Escape не заявляется. QA остановлена, процессы/порт
освобождены. [Подробности](src/Verification/ClipMove.md#проверка-настоящего-окна--16112026).

[План](Plan.md#clip-move-within-segment) · [проверки и границы](src/Verification/ClipMove.md).

<a id="dotnet11-framework-refresh"></a>
## STUDIO-NET11 — переход на .NET 11, 16.11.2026

Этап выполнен: SDK 11.0.100 / TF и TestsTF=net11.0 / LangVersion=latest /
runtime-async=on. Studio Version=1.2611.16.1-alpha; зависимость TrueMoon закреплена
на 1.2611.16.1-alpha. Все девять архивов приняты с SHA/nuspec проверкой из
ENG7B3 manifest; повторный импорт immutable. SkiaSharp 4.156.1 и matching extended
native DLL доставлены в publish. Microsoft.NET.Test.Sdk 18.12.0, xUnit 2/VSTest;
legacy dotnet-xunit удалён. Исходники TrueMoon не менялись/не пересобирались.

Правило версии уже совпадало с TrueMoon: x.yyMM.d.rev[-postfix], UTC и независимый
дневной счётчик, одно повышение на production-набор до финальной проверки.
Remote main при выборе номера — bf40a5c5409e67fa883fec09b49599f78e985921,
Version=0.1.0.1-alpha; условие override сохранено, автоматического bump при build нет.
Python launch/bootstrap/health/Server header читают общий props; HTTP/schema/IPC
versions не менялись. Исправлен прежний потерянный exit code точки входа:
RunStudioAsync возвращал 2 при неверных аргументах, но Program не передавал его ОС.

- Debug и Release: Passed: 190, Failed: 0, Skipped: 4 каждый.
  Полные non-incremental D/R с generated sources/XML docs и CS1591-as-error:
  0 warnings / 0 errors, по 7 generated sources.
- Python: 288 tests, OK; узкий HTTP-набор 10 tests, включая общую версию.
- Отдельная свежая JSON-копия Клэр: 1 passed, schema 1 → 2, ревизии 55 → 56 → 57,
  ID/история/неизвестные данные и SHA авторского исходника сохранены.
- Native lifecycle: 1 passed, 11 окон, 882 кадра resize/seek. Playback: 1 passed,
  скорости 1×/0.5×/2×, реальное аудиоустройство, cancel/drain. CPU MP4: 216 кадров,
  RMSE=0.0055880039; проект и 4 медиа SHA сохранены, POST=0, Jobs нет.
- Независимый снимок восстановлен с пустым cache; Release publish, net11 runtime,
  единая версия deps и native SHA проверены. Обе QA-сессии остановлены по ID,
  процессы и tool sessions завершены. Symbolic links остаются skip (Windows 1314).

Это приёмка Windows x64/framework-dependent текущего HTTP-прототипа. Полные
physical keyboard/DPI/media, VFR/несколько клипов, AV latency и исходная причина
0xC0000005 открыты. Backend/IPC, переключение владельца записи и автоматическая
миграция авторских проектов не выполнялись. GPU и авторские медиа не затронуты.
[План](Plan.md#dotnet11-framework-refresh) · [команды, evidence и границы](src/Verification/DotNet11.md).

## Правило нумерации поставок — 15.11.2026

В [инструкциях](AGENTS.md#versioning) принята схема `x.yyMM.d.rev[-postfix]`,
дата UTC, дневной счётчик с 1 и одно повышение версии production-набора до
финальных проверок. Источник — `src/Directory.Build.props`; версия зависимости
TrueMoon и версии контрактов учитываются отдельно. Это только документация:
текущая версия, Python/HTTP-метки и соседние незакоммиченные изменения сохранены.
Проверены согласованность правила, ссылки и diff; build/test/GPU не запускались.
Результаты последующих разделов относятся к указанным в них прежним проверкам.

## ProjectStore, запись и явная миграция — 10.11.2026

Добавлена DLL TrueMoon.Tales.Studio.Persistence: codec schema 1/2 и ProjectStore
Open/Migrate/Save/Recover. Открытие не пишет; миграция отдельно переводит schema 1
в envelope 2 с полным legacy_document. ID/связи сохраняются, неизвестные поля
schema 2 сопоставляются по стабильным ID и сохраняются при изменениях.

Запись проверяет expected_revision, точный SHA baseline и локацию, сохраняет
предыдущие байты в Production/History и применяет File.Replace. Эксклюзивный
lease координирует сотрудничающие экземпляры; journal требует явного recovery
после неопределённого результата commit. Отмена до intent сохраняет исходник,
после intent выполняется завершение/recovery без повторной ревизии.
Действуют явные write roots и protected roots; пути JSON не дают файловых прав.

Прошли 34 новые synthetic проверки. Изолированная JSON-копия Клэр прошла
55 → 56 → 57: 55 источников, 62 дубля, 2 окна, 0 действий/клипов. История точная,
seed и SHA авторского исходника сохранились; A–B–A проверен отдельной синтетикой.
Debug/Release всего решения — по 190 passed, 0 failed, 4 skipped. Из skipped:
native/FFmpeg, отсутствующая Windows-привилегия symbolic link и opt-in копия,
которая отдельно дала 1 passed. Пересборка Release — 0 ошибок/предупреждений.

Авторские проекты и медиа не менялись, GPU/UI не запускались. Текущий Python
API ещё schema 1; подключение прав Workspace и backend/IPC остаётся отдельным
этапом. Для реальной миграции нужен остановленный прежний владелец записи.
[API, commit/recovery, границы и доказательства](src/ProjectStore.md).

## C#-контракты и чтение текущего формата — 10.11.2026

Добавлена DLL TrueMoon.Tales.Studio.Contracts: version=1 DTO действий, источников,
дублей, окон и клипов, JSON-кодек транспорта и структурная проверка ссылок.
Поддерживаются несколько источников/клипов у одного действия, повторное
использование источника и независимые ID действия и клипа. EXE остаётся один.

LegacyProjectReader читает schema_version=1 в памяти. Сценарный замысел и последний
допустимый монтаж сохраняются отдельно; проект без timeline не получает
сценарные сегменты из производственных групп. Полный OriginalDocument сохраняет
неизвестные поля, пути, историю и генерационные данные после освобождения входа.
Отсоединённые от черновика сохранённые размещения получают явные diagnostics.

Итоговый узкий набор — 60 passed, 0 failed. Debug/Release всего решения —
по 156 passed, 0 failed, 2 skipped (опциональные native и FFmpeg/audio).
Пересборка Release — 0 ошибок и 0 предупреждений. Проверки используют
синтетические DTO/JSON в памяти; UI, устройства и GPU не запускались.

JSON контрактов не является форматом Studio.project.json. Адаптер записи,
миграция, транзакции expected_revision и новые монтажные команды ещё предстоят;
текущий UI/API и авторские проекты остаются на прежней схеме.
[API контрактов, границы и команды проверок](src/ProjectContracts.md).

## Действия, источники и монтаж — 09.11.2026

Принята целевая модель: сценарий → сегменты → блоки-действия; библиотека хранит
источники и дубли, монтаж — клипы в окнах тех же сегментов. Действие допускает
несколько источников и несколько склеек, источник используется повторно.
Плановое и фактическое время разделены, фиксированное окно сохраняется.

Определения и правила совместимости собраны в [ProjectModel.md](ProjectModel.md).
Обновлены AGENTS, оба README, архитектура, план, сценарная/монтажная документация,
описание API и инструкции агентам. В плане оставлены открытыми новый контракт,
связи нескольких источников/клипов, группировка и миграционная приёмка.

Это документационное решение. JSON проектов, текущие команды API/CLI и код
не менялись; авторские данные и исторические отчёты сохранены. Проверены новые
Markdown-ссылки/якоря, терминология и форматирование; runtime/native/GPU и
медиапроверки не запускались.

<a id="cobalt-generator-requests"></a>
## Заявки Cobalt: исправления завершены во фреймворке — 09.11.2026

Владелец Cobalt закрыл обе заявки как `completed` в задаче COB6e:
[короткие hint names](../TrueMoon/src/docs/requests/cobalt/20261107-studio-generated-hint-names-87952664/Decision.md) и
[XML docs служебной регистрации](../TrueMoon/src/docs/requests/cobalt/20261107-studio-generated-xml-docs-56375bd8/Decision.md).
Hint names ограничены 128 символами и имеют стабильный SHA-256 полного ключа;
публичные registration/Init получили XML summary. Runtime/lifetime контракты сохранены.

В TrueMoon проверены Debug/Release: generator 96/96, runtime/adapter 179/179;
независимый потребитель локального пакета `0.2.5.2-alpha` — 9 generated sources,
11 проверок графа/cleanup, 0 ошибок и предупреждений. Emit/XML docs/CS1591-as-error
и отрицательный контроль собственного CS1591 прошли.
[Команды и границы проверки владельца](../TrueMoon/src/docs/cobalt/HISTORY.md#cob6e-generator-2026-11-09).

**Приёмка Studio завершена 09.11.2026:** `StudioTrueMoonVersion` обновлён на
`0.2.5.2-alpha`, полный набор из 9 пакетов собран `Update-TrueMoonPackages.ps1`
из текущего TrueMoon в локальный feed (`src/.local/truemoon-feed`, SHA-256 manifest).
Debug и Release — по 90 passed, 0 failed, 2 опциональные интеграции пропущены.
Полная Release-сборка — 0 предупреждений: прежние два CS1591 из generated-регистрации
исчезли. Отдельно `EmitCompilerGeneratedFiles=true` + `WarningsAsErrors=CS1591`
в Debug и Release: 7 generated-файлов записаны на Windows, ошибок нет (CS0016 по
hint names не воспроизводится), `Init` имеет XML summary. Отрицательный контроль
собственного недокументированного public-члена даёт ошибку CS1591. Студийный
Cobalt graph/lifetime/cleanup повторно прошли в этих же запусках.
Типы `TrueMoon.Argentis.SplitView`/`SplitOrientation` доступны в установленном
пакете Argentis `0.2.5.2-alpha` (проверено рефлекссией по lib DLL). Native UI
Studio не запускался; подключение `SplitView` в `StudioLayout` остаётся открытым
этапом UI-UX05. Внешняя публикация пакетов по-прежнему не заявлена.
Исторические результаты 07.11 сохранены.

## Заявка Alloy: разделители реализованы во фреймворке — 09.11.2026

Получен результат `STUDIO-20261107-5d593644` / `ui.resizable-panes`:
`TrueMoon.Argentis.SplitView` реализован и проверен владельцем Alloy.
[Decision](../TrueMoon/src/docs/requests/alloy/20261107-studio-resizable-panes-5d593644/Decision.md),
[заметка передачи](../TrueMoon/src/docs/requests/alloy/20261107-studio-resizable-panes-5d593644/Notes/20261109T122600Z-alloy-splitview-completed-a6f84c1e.md),
[API и ограничения](../TrueMoon/src/docs/alloy/SPLIT_VIEW.md).

Framework/local-delivery scope завершён: horizontal/vertical panes, min/max,
pointer capture/cancel, клавиатура и retained state. Проверена локальная поставка
`0.2.5.2-splitview.20261109.1`; внешняя публикация обычной версии этим не заявлена.
Framework test/native/package результаты принадлежат TrueMoon и не являются
новой проверкой Studio.

**Интеграция Studio остаётся открытой:** подключение в `StudioLayout` выполнено
09.11.2026 после приёмки пакетов `0.2.5.2-alpha`: цепочка из трёх `SplitView`
(навигация/инспектор/монтаж), скрытие панелей переподключает цепочку без
пересоздания контролов, размеры — UI state с восстановлением по «Восстановить
панели». `SplitViewLayoutTests`: Debug/Release — всего 96 passed, 0 предупреждений.
[Результат и границы](src/WindowMenus.md#разделители-панелей--09112026).
Физический ввод, настоящее окно, тема и скриншотная сверка с Aether — отдельная
приёмка по QASessions; native UI этими запусками не проверялся. Следующий шаг —
[UI-UX05](Plan.md#ui-ux05--дизайн-ui-из-truemoon--aether).
Этой записью изменены Plan/Status, код компоновки Studio и её тесты;
проекты, GPU и процессы фреймворка не затрагивались.

## Модель запуска одного EXE — 07.11.2026

Уточнены архитектура и план: один TrueMoon.Tales.Studio.exe запускается как
UI-экземпляр и средствами фреймворка как служебный backend-экземпляр. Все
backend-сервисы размещаются внутри служебного экземпляра, связь — Enerit IPC,
создание и освобождение сервисов — Cobalt. Отдельный backend EXE не создаётся;
Program остаётся общей минимальной fluent-композицией.

Это согласованная целевая модель; запуск двух ролей ещё не реализован. Текущий
C# UI работает через HTTP с Python-сервисом. Обновлены критерии готовности IPC,
владения экземпляром, остановки/перезапуска и поставки одного EXE. Проверены
изменённые Markdown-ссылки и согласованность документов; runtime, native и GPU
проверки для этой правки документации не запускались.
[Архитектура](Architecture.md#целевая-модель-запуска) ·
[Порядок перехода](Plan.md#прототипы-и-порядок-перехода).

## Native-падение: диагностика и повторы — 07.11.2026

Исходный 0xC0000005 в HostedUiWindow.Tick не воспроизведён. Причина остаётся
неизвестной; исправление runtime/движка не заявляется. На том же Release DLL
прошли startup, кадр вперёд, playback, popup/Escape, максимизация/восстановление,
сворачивание/активация и отдельный проход с пятью full GC. Два интерактивных
прохода завершены по дедлайну, поэтому не считаются штатным закрытием.

Отдельный native-тест прошёл 10 циклов App start/stop во время загрузки и после
кадра, затем 40 секунд resize/seek/GC: 880 кадров, все 11 окон закрылись штатно.
Дампа не появилось: повторного unhandled native exception не было. Это ограниченный
smoke, полная медиаматрица, DPI и длительный авторский монтаж не проверялись.

QA-контроллер теперь сохраняет полный Windows exit status и поддерживает
локальный ProcDump внутри своей Job, без установки глобального crash handler.
Добавлен явный ограниченный CheckNativeQA; обычные тесты окна не открывают.
Python lifecycle — 12/12. Итоговые Debug/Release — по 90 passed, 0 failed,
2 optional skipped. Аудит шести сессий: собственные процессы завершены,
проект/два MP4 неизменны, POST/Jobs нет. Подробности, точные тесты и команды:
[отчёт native-диагностики](src/NativeCrash.md). Причина AV остаётся в Plan.md.

## Композиция App и владение HTTP — 07.11.2026

Program содержит только fluent-композицию UseStudioServices / UseStudioWindow /
RunStudioAsync. Проверка CLI и типизированные StudioOptions находятся в Configuration,
настройка окна — в Presentation. HTTP-сервис создаётся Cobalt через конструктор,
сам создаёт HttpClient/handler и освобождает их. Сохранены локальный endpoint,
таймаут 10 с, запрет proxy/redirects и прежние аргументы/exit code 2.

Debug/Release — по Passed: 90, Failed: 0, Skipped: 1. Проверены generated-граф,
singleton, отмена запросов и однократный Dispose handler при sync/async завершении,
очистка при ошибке конструктора и старта App, CLI до конфигурации сервисов.
Release-exe с неверными аргументами возвращает 2 и подсказку. Полная Release-сборка
прошла: 0 ошибок, два прежних CS1591 генератора Cobalt; новых предупреждений нет.

Native smoke не пройден: QA 67873a7bab504dca94ca16cd2e68f265, порт 64290,
код 1, ui.log — 0xC0000005 в TrueMoon.Alloy.Hosting.HostedUiWindow.Tick.
Причина не установлена, дефект HTTP/DI или движка не заявляется без воспроизведения.
Computer Use затем остановлен физическим Esc; новых UI-действий/запусков не было.
Контроллер завершил сессию stopped/ui-exit; владелец и все зарегистрированные
потомки отсутствуют. Проект и SHA двух MP4 сохранились, POST/Jobs нет.
Полная медиаматрица не повторялась; дальнейшая UI-приёмка остаётся открытой.
[Композиция, владение, тесты и команды](src/README.md#композиция-app-и-владение-http).

## DI на Cobalt — 07.11.2026

Studio переведена с Microsoft DI на штатный Cobalt через App.RunAsync.
UseStudioServices собирает граф: generated singleton-модель, стили и фабрики
окна/тайтлбара; registration factories передают runtime-параметры HTTP/FFmpeg.
Генератор включён в приложении и поставляется пакетом Cobalt. Adapter reference
и его версия удалены, пакетный граф и скрипт feed теперь используют 9 пакетов
TrueMoon одной версии 0.2.5.1-alpha; Microsoft DI в assets/deps отсутствует.

Добавлены четыре headless-кейса: реальный Cobalt resolver и generated-конструкторы,
singleton identity, start/stop, общая модель разных View и sync/async disposal с
отменой запроса и сохранением заимствованного HttpClient. Debug/Release — по
Passed: 71, Failed: 0, Skipped: 1. Полная Release-сборка — 0 errors, 2 CS1591
из служебной регистрации Cobalt. Собственных предупреждений нет.

Подтверждены ограничения generator: слишком длинные hint names при диагностическом
emit на Windows и отсутствие XML docs у module initializer. Обычная сборка работает,
предупреждения не подавляются. Опубликованы отдельные входящие заявки:
[hint names](../TrueMoon/src/docs/requests/cobalt/20261107-studio-generated-hint-names-87952664/Request.md) и [XML docs](../TrueMoon/src/docs/requests/cobalt/20261107-studio-generated-xml-docs-56375bd8/Request.md).
Код движка, планы и текущая работа его владельцев не изменялись.

Release-окно открыло изолированную сцену и показало FRAME 00. Alt+F4 завершил UI
и сервер с кодом 0, stopped/ui-exit; сессия 01e5543800ba450899a1dd663bd2fd92,
порт 54630. JSON проекта и SHA двух MP4 сохранились, POST/Jobs нет. Проверена
смена DI и lifecycle окна; полная медиаматрица повторно не запускалась.
[Регистрации, точные тесты и команды](src/README.md#di-на-cobalt).

## Структура C#-решения — 07.11.2026

Код прототипа разложен по каталогам `Models`, `Editing`, `Infrastructure`,
`Media` и `Presentation` (`Scene`/`Shell`). Пространства имён соответствуют
каталогам; `Program.cs` остаётся точкой входа и регистрации зависимостей.
Общие модели вынесены из HTTP-клиента и расчёта аудио. Playback-часть модели
окна размещена рядом с основной как `SceneViewModel.Playback.cs`. Тесты
сгруппированы по тем же областям, общая fixture находится в `Support`.

Сравнение с исходными файлами подтвердило сохранность тел типов и методов:
изменены размещение, namespace/using и выделены два файла моделей.
Debug/Release — по `Passed: 67, Failed: 0, Skipped: 1`, без предупреждений
и ошибок сборки. Реальная аппаратная интеграция в этом переносе не запускалась;
предыдущая медиапроверка остаётся историей этапа ниже. Приложение/QA-серверы
не запускались. Следующая функциональная задача — перемещение целых клипов
и длинный таймлайн. [Карта каталогов и команды](src/README.md#структура-c-решения).

## Непрерывный просмотр и звуковая волна — 07.11.2026

В C#-окне реализованы воспроизведение выбранного CFR-клипа со звуком,
пауза/продолжение и повтор после последнего кадра. Волна строится по тому же
миксу native-аудио, музыки и событий, который слышен при просмотре. Сохраняются
mute/gain/stream, позиции и правила скорости действующего проекта. Видео
следует счётчику звукового устройства; очередь ограничена тремя кадрами.
Подготовка выполняется вне UI-потока с отменой и дедлайном. Seek/trim/смена
клипа/потеря фокуса/закрытие останавливают собственные медиа-ресурсы.

Debug и Release: по 67 passed, 0 failed, без предупреждений; одна аппаратная
интеграция по умолчанию пропускается и отдельно прошла на живой QA-сцене.
Проверены FFmpeg при 0.5×/1×/2×, PCM, реальное звуковое устройство и отмена
заполненной очереди. На изолированном проекте штатный CPU MP4 дал 216 кадров
при 24 fps; четыре контрольных кадра соответствуют источнику, RMSE PCM/AAC
0.005588, событие 880 Hz попало в нужный интервал. Проект/исходники сохранились,
POST и Jobs отсутствуют. Python production API и исходники TrueMoon не менялись.

В настоящем Release-окне видны волна, ход до последнего кадра и «Конец клипа»;
кнопка паузы удержала FRAME 435, Enter продолжил с той же позиции. Routed-тест
Space прошёл, физическое нажатие в автоматизации не подтвердило паузу и остаётся
клавиатурной приёмкой. VFR, вся сцена со стыками/переходами/титрами, полный
звуковой скоростной набор, DPI и измерение AV-задержки ещё не проверены.

QA работает только на синтетической копии с Job, дедлайнами и явным stop.
Проверки жизненного цикла — 10/10. Окна, серверы и собственные проверочные
процессы завершены. Новых подтверждённых ограничений TrueMoon в этом срезе нет.
[Реализация, точные тесты, команды и границы](src/Playback.md).

## Shift+F10 и drag/trim таймлайна — 07.11.2026

Исправлен контекст Shift+F10: меню выбирается по строке дубля/клипа или рабочему
контролу в фокусе; Escape возвращает фокус тому же элементу. Добавлены drag
курсора и обоих краёв выбранного клипа с захватом указателя, кадровой сеткой,
локальным черновиком и одной записью при отпускании. Левый край сохраняет
правый, учитываются скорость, доступная длина источника, соседи и закрепление.
Escape/потеря захвата/resize отменяют жест. Конфликт и ошибка сохраняют черновик
без повтора записи. Частые запросы кадров объединяются, устаревшие не показываются.

В решение включён тестовый проект: Debug и Release — по 23/23, без ошибок и
предупреждений. В настоящем Debug-окне Shift+F10 показал команды дубля B;
перемотка не изменила проект. Три trim-жеста дали ровно три POST и ревизии
5 → 6 → 7 → 8, конечный источник/положение `[2,12)`, 10 кадров. Повторное
открытие показало FRAME 02. Второй клип, сегменты, assets и исходники сохранились,
история 5/6/7 присутствует, SHA двух исходных MP4 совпали, Jobs не созданы.
Последняя Release-сборка отдельно подтвердила drag и одну запись левого края.

Штатный CPU-экспорт QA-ревизии 8: 24 кадра при 24 fps, все 16 кадров занятых
участков совпали с ожидаемыми номерами источника; промежутки чёрные, звук
тестовой беззвучной сцены имеет нулевой пик. Проект остался побайтно прежним.
Проверка при 1× без речи, переходов и титров; полная медиаприёмка остаётся открытой.
Окна и серверы обеих QA-сессий завершились вместе, проверочные процессы/порты
освобождены. Python API и исходники движка не изменялись; GPU не запускался.
[Поведение, точные тесты, команды и границы](src/TimelineDrag.md).

## Очистка процессов native QA — 07.11.2026

После проверки exe/команды/времени создания остановлены два оставшихся Python
сервера и окно Studio. В `src/Verification/WindowMenusQA.py` добавлен управляемый
запуск: общий Windows Job Object для сервера/UI/потомков, registry, 15-минутный
лимит, 45-секундный таймаут готовности, контроль запускающей оболочки,
`--status` и `--stop`. Остановка сначала запрашивается у контроллера; если
он не завершился за 7 секунд, применяется проверенная принудительная остановка.
Очистка выполняется и при аварии контроллера; повторное использование PID
защищено проверкой точного времени создания и exe на том же process handle.
AGENTS требует завершать собственные QA-сессии перед финальным ответом.

Все 10 проверок [жизненного цикла QA](src/Verification/test_qa_session.py)
прошли: очистка потомков с сохранением постороннего процесса, авария
контроллера, EOF/ошибка назначения в Job Object, защита устаревшего PID/exe,
таймаут старта и работы, ошибка запуска UI, выход оболочки и явная остановка.
Дополнительно обычное закрытие настоящего Debug-окна завершило QA-сервер
и контроллер (`stopped/ui-exit`, код 0). Отмена tool session через Ctrl+C
прервала контроллер без записи `stopped`, но Windows завершила его группу,
а `--status` показал `inactive`; сохранённый `running` не означает живой процесс.
В конце проверки 07.11.2026 независимая сверка WMI не обнаружила
Python/Studio/FFmpeg от проверок; QA-порты 59260, 51762, 54344, 59191
и 58550 больше не слушали. Проверочные tool sessions завершены.
Авторские проекты, GPU и код production C# не затронуты; .NET повторно не собирался.
[Команды, точные тесты и границы](src/QASessions.md).

## Последние изменения и проверки

- **Повторная проверка меню.** Правый щелчок, Escape, размещение у края и закрытие снаружи прошли; при прежнем проходе ответ API совпал до/после, ревизия 7. Найденный тогда дефект Shift+F10 исправлен и проверен текущим этапом выше. Раскрытие/сворачивание тайтлбара проверены, плавность 180-мс перехода по одиночным снимкам не измерялась; popup отдельной анимации не имеет. [История](src/WindowMenus.md#повторная-проверка-меню--07112026) · [Исправление](src/TimelineDrag.md).

- **Панели, тайтлбар и основные сценарии доработаны.** Штатные MenuBar/OverlayHost TrueMoon, меню Файл/Правка/Вид/Клип и контекстные команды. Навигация и инспектор занимают рабочую высоту, действия сохранения закреплены; доступны режим просмотра и скрытие панелей. Подтверждены два клипа (включая один дубль), просмотр альтернативы, переход по полосе, F10/Ctrl+S, Dark/Light и 880×640. Закрытие защищает черновик; «Остаться» сохраняет его, «Сохранить и закрыть» дожидается записи. Проверочная ревизия 5 → 6 → 7, финальный участок `[3, 8)`. Второй клип, история и SHA MP4 сохранены, Jobs отсутствует. Debug/Release — 0 ошибок и предупреждений. [Отчёт и границы](src/WindowMenus.md).

- **Первое окно на Argentis оформлено.** Сценарий и дубли, превью, инспектор, обзор монтажа, Dark/Light и собственный frame TrueMoon. Модель отделена от View/style; создание через App/DI-фабрику. Debug/Release — 0 ошибок и предупреждений. Проверены размеры 1280×820 и 880×640, разворачивание, покадровый шаг, черновик/отмена и смена темы. Сохранение через UI дало `[1, 8)`, ревизию 4; повторное открытие показало кадр 01. История и SHA двух MP4 сохранены, Jobs отсутствует. Разделители и интерактивная шкала остаются следующими задачами. [Отчёт](src/WindowDesign.md).

- **Пакеты TrueMoon обновлены до `0.2.5.1-alpha`.** Собран локальный набор из десяти пакетов, добавлены NuGet.Config с маршрутизацией TrueMoon в локальный feed и скрипт подготовки пакетов. Все результаты сборки движка направлены в `src/.local/`; исходники TrueMoon не редактировались. Восстановление и сборки Studio Debug/Release прошли с 0 предупреждений и 0 ошибок. Проверены единая версия графа, SkiaSharp `4.155.0`, SHA-256 доставленной native DLL и отказ CLI при неверных аргументах без открытия окна. Первоначальная проверка пакетов не включала окно; последующая HTTP-проверка описана в отчёте окна. Непрерывное воспроизведение остаётся открытым. [Воспроизведение сборки и ограничения](src/README.md).
- **В план включён UI-UX05.** Дизайн новой Studio следует общему дизайн-коду Argentis и редактору Aether: композиция панелей, Dark/Light, семантические роли, состояния полей и компактный инспектор. Первое окно реализовано и проверено в указанном срезе; полная визуальная и клавиатурная приёмка остаётся открытой. [Задачи и критерии](Plan.md#ui-ux05--дизайн-ui-из-truemoon--aether).

- **Прототип Argentis через HTTP.** На изолированной копии открыта сцена с двумя MP4 при постоянных 24 кадрах/с. Номера внутри изображений подтвердили шаг вперёд и назад. Обрезка `[0, 8) → [1, 3)` подняла ревизию с 3 до 4. После повторного открытия сохранились двухкадровый участок и кадр 01 на входе. История ревизии 3 совпала с исходным проектом побайтно, SHA-256 обоих MP4 совпали. Сборка Debug прошла без ошибок и предупреждений, прошли пять HTTP-тестов таймлайна. [Запуск и границы проверки](Verification/Argentis.Prototype.md).
- Монтажный MP4 новых генераций сохраняется в каталоге проекта `video/` с номером `RunNN`. Дубль появляется в карточке блока, native AV и последний кадр остаются в задании. Прошли 287 Python-тестов, включая повторный сбор и восстановление после сбоя сохранения. [Сохранение дублей](README.md#данные-и-восстановление).
- В браузере на отдельном тестовом проекте открыты три точки входа. Из карточки сегмента «Изменить блок» открывает сценарную форму. «Материалы и промпт» открывает «Исходник», где доступны постановка, референсы, опоры и дубли. Выбор клипа на шкале переключает инспектор на «В монтаже» и открывает доступ к «Участку и скорости».
- В форме «Изменить блок» рядом с описанием находятся выбор дубля и вход в него. Ряд клипов на шкале подписан «Блоки». Это два места, где подписи могут смешивать сценарную правку с монтажной. Точки входа описаны по текущим подписям. [Где редактировать](Scenario.md#где-редактировать).

## История перехода на C# и TrueMoon · срез STUDIO-MOVE

Текущий backend-срез описан [выше](#backend-trim). Следующие два пункта
сохраняют контекст приёмки 16.11.2026 и не задают актуальную очередь.

- **Текущий этап.** Через Python HTTP API работают открытие, drag/trim, Shift+F10, непрерывный просмотр выбранного CFR-клипа со звуком, waveform и пауза/продолжение. После STUDIO-MOVE: Studio `1.2611.16.2-alpha`, TrueMoon `1.2611.16.1-alpha`, net11.0/runtime-async=on; DI — Cobalt, HTTP принадлежит сервису, Debug/Release — по 247 passed и 5 ожидаемых skipped. Перенос целого клипа внутри сегмента проверен routed input/HTTP/CPU MP4. Видео имеет ограниченную очередь и следует sample clock; реальный FFmpeg/устройство/CPU MP4 проверены на изолированной сцене. VFR, монтаж нескольких клипов и отзывчивость длинной шкалы остаются открытыми. [Результат](src/Playback.md). [DI и lifecycle](src/README.md#di-на-cobalt).
- **Следующий шаг.** Измерение отзывчивости длинной шкалы и физическая приёмка переноса клипа. Довести клавиатурную/DPI и полную медиаприёмку, включая физический Space, стыки/переходы/титры и AV-задержку. Регулируемые панели остаются заявкой владельцу Alloy. Отдельный C#-сервис через Enerit следует за этапом HTTP. [Порядок перехода](Plan.md#прототипы-и-порядок-перехода).

## Готовность по этапам

Таблица описывает прежде всего Python/Web и прежние HTTP-прототипы. Наличие
возможности в этом контуре не означает её перенос в schema2 backend; текущие
границы сведены [выше](#backend-playback).

| Этап | Сделано | Что остаётся |
|---|---|---|
| UI-00/01, UI-06A/B | Локальное приложение, импорт материалов, карточка сегмента, фиксированные окна, отдельные вкладки референсов и опор | Сверить импорт и тайминг авторских проектов в UI-05 |
| UI-02 | Подготовка, ограниченный запуск и сохранение монтажного MP4 с номером дубля и метриками. Подтверждён контрольный GPU-дубль через Studio. Создаётся продолжение с native AV и последним кадром | Прогнать продолжение через UI, проверить остановку и восстановление активного задания. Добавить пресеты черновика и финальной генерации |
| UI-03A/B | Внешний агент через чат, API и CLI. Контекст, задания по блокам и сегментам, история и защита от конфликта | Встроенный помощник, MCP, сравнение предложенных правок и выборочное применение |
| UI-04, UI-08A | Монтаж видео и звуковых событий, музыка и маркеры, обрезка, постоянная скорость, уплотнение, переходы через чёрный, кириллические титры, черновой и финальный MP4 | Затухание звука и приглушение музыки под реплики. Сохранённые стили титров, смешивание клипов и переменная скорость |
| UI-UX01–04 | Верхняя навигация с поиском и «Ассетами», правый инспектор, подсказки к кнопкам. Регулируемая высота и плотность таймлайна, перемотка, масштаб, привязки и повтор стыка. Сравнение и замена дубля в выбранном монтажном участке | Кадры по обе стороны стыка и предупреждение о зависимости Motion Context при замене |
| UI-05 | Проверка готовности перечисляет проблемы тайминга, дублей и файлов и открывает нужный участок. Предпросмотр и MP4 сверены на контрольных сборках | Сверить рабочий фрагмент и полный монтаж Клэр, затем пройти сценарий опенинга. Добавить список готовности рядом с таймлайном |
| UI-06/07 | Обзор сценария с правкой описаний, заметки о звуке и стыках, библиотека ассетов, предложения разбивки с проверкой окна, аниматик сегмента | Привязка к версии текста и аниматик эпизода, версии эталонов и контроль непрерывности |
| UI-08–11 | Импорт звука, одиночные задания, снимки и связи дублей. Базовые проверки готовности доступны перед экспортом | Озвучка и субтитры, зависимости и пакетная очередь, прокси и передача монтажа, архив и пилот адаптации 3–5 минут |

Предыдущие проверки в браузере покрыли широкую и узкую компоновку, переходы из списка готовности, замену дубля с сохранением окна, отмену и конфликт ревизии, подсказки при наведении и с клавиатуры. После подсказок прошли 162 Node-теста. Сверка звука, титров и переходов в полном MP4 авторских проектов остаётся в UI-05. [Контрольный GPU-дубль](Verification/Control.Run01.Report.md) · [Проверки экспорта](Verification/DraftExport.v1.Report.md).

## Рабочие видеопроекты

Срез от 07.11.2026 из предыдущей сверки.

- **Клэр, ревизия 34.** У `Test1` отсутствуют длительность и назначенный дубль. У первого монтажного использования «доклад по связи и ответ командира» также отсутствует назначение. Дубль `CL-000A` подходит к его окну в 124 кадра.
- **Опенинг, ревизия 1.** Исходники импортированы. Монтажная шкала пуста.
