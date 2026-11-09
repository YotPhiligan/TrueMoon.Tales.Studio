# True Moon Studio — локальный API 0.13

> Studio 0.15 / M2: код независим от Book; текущие пути, конфигурация и правила
> совместимости описаны в [Workspace.md](Workspace.md). Старые отчёты фиксируют
> прежние версии. GPU запускается только отдельной явной операцией launch.


Базовый адрес `http://127.0.0.1:8787`. UI и внешние агенты используют общие операции. Область файлов задаётся зарегистрированными корнями конфигурации Studio: библиотеки доступны для чтения, запись разрешена только в явные рабочие каталоги. Защищённый TrueMoon недоступен. Это HTTP API; MCP-обёртка ещё не реализована.

В 0.4 доступны карточка brief, расширенный `/agent/context` и долговечные `/agent/requests`. [Полный контракт, состояния и CLI](AgentIntegration.md). Legacy `/context` сохранён. Bootstrap объявляет возможности agent и automatic_dispatch=false. Ни чтение контекста, ни создание/claim задания генерацию не запускают.

Задание `scope.intent=breakdown_segment` работает в режиме `propose`. Передайте `proposal` при `finish` вместе с текстом `result`. GET `/api/projects/{pid}/agent/requests/{rid}/breakdown` рассчитывает бюджет текущей ревизии и возвращает `items`, `status`, `issues`, `project_revision`, `fps` и поля бюджета в кадрах. POST на тот же путь принимает `expected_revision` проекта из предпросмотра и `actor`, повторно проверяет бюджет и применяет весь набор одной операцией. Описание полей предложения и CLI приведено в [ScenarioAgent.md](ScenarioAgent.md#разбивка-сегмента-с-агентом).

В 0.5 доступны GET/POST `/api/projects/{id}/scenario` и CLI scenario; [операции и правила времени](Scenario.md). Поле timeline.scenario хранит сценарные намерения, включая неизвестное время и конфликты; допустимая монтажная проекция обновляется атомарно. Пространственные правки активного сценария через прежний timeline API используют ту же последовательную укладку. `add_item`, `update_item` и `add_catalog_video` принимают необязательные сценарные заметки `sound_notes` и `continuity_notes` в `payload`. Пустая строка очищает заметку. Действие `set_animatic_frame` принимает `segment_id`, `id` сценарного использования и `asset_selection`. Выберите изображение по `asset_id` или по `file` из каталога, либо передайте `null` для снятия выбора. Выбранный `animatic_asset_id` хранится у сценарного использования и участвует в отмене.

## Сессия и версия

`GET /api/bootstrap` возвращает `token`, список проектов, профиль и наличие необходимых файлов ядра. При любой записи передавать `X-Studio-Token: <token>` и JSON `Content-Type: application/json`. Токен меняется при перезапуске сервиса, хранить его в памяти клиента. Сервис проверяет Host/Origin и отклоняет cross-site запросы; чтение из локального агента без Origin допустимо. Токен не даёт доступа извне машины и не является отдельной учётной записью.

Запись проекта требует `expected_revision` из последнего GET. Не повторять запись вслепую после 409: перечитать состояние и согласовать правку. Поле `actor`, например `agent:codex`, сохраняет происхождение версии. Успешная запись возвращает полный новый проект. Ошибка — `{ "error": "…", "details": … }` с HTTP 4xx/5xx. Не записывать напрямую `Studio.project.json`, пока сервис работает.

## Чтение

| Метод и путь | Результат |
|---|---|
| GET `/api/health` | Версия/идентификатор сервиса; генерацию не запускает |
| GET `/api/projects` | Краткий список проектов |
| GET `/api/projects/{project}` | Полное состояние: revision, assets, segments, blocks и takes |
| GET `/api/projects/{project}/context?block={block}` | Агентный контекст с фактическими путями материалов и соседями |
| GET `/api/projects/{project}/source?block={block}` | Текст исходной карточки, если она есть |
| GET `/api/projects/{project}/history` | Список снимков истории |
| GET `/api/projects/{project}/jobs` | Задания с доступным прогрессом и временем |
| GET `/api/projects/{project}/jobs/{job}` | Сохранённое состояние одного задания |
| GET `/api/projects/{project}/takes/{take}/comparison` | Метаданные видео и сохранённые входы конкретного дубля. Seed строкой, профиль, длительность, референсы и Guides из связанного снимка. Неизвестные поля возвращаются как null. |
| GET/HEAD `/api/media?project={project}&asset={asset}` | Материал по ID, с поддержкой одиночного Range для перемотки |

Seed хранится строкой uint64. `settings.seconds` — запрос длины источника 2–8 с; `montage_seconds` — независимое положительное монтажное окно либо null. `settings.mode`: `guided`/`context`, `settings.size`: `[896,512]`/`[1280,736]`. Метки Picture следуют порядку `references`, Guides ссылаются на устойчивый `reference_id`, `frame`: 0, −1 или допустимый промежуточный индекс. Состав точных полей читать из проекта/контекста; технические входы фиксируются при подготовке.

## Запись

- POST `/api/projects` — `{ "name": "Название" }`, создать пустой проект.
- PATCH `/api/projects/{project}/blocks/{block}` — `{ "expected_revision": 4, "actor": "agent:codex", "block": { "prompt": "…", "notes": "…" } }`. Разрешённые поля: title, notes, prompt, references, guides, settings, montage_seconds, context_take_id. Передаются только изменяемые поля; содержимое массивов/настроек передаётся целиком.
- POST `/api/projects/{project}/operations` — `{ "expected_revision": 4, "actor": "author", "operation": "…", "payload": { … } }`.

| operation | payload |
|---|---|
| `add_segment` | `{ "name": "Название" }` |
| `add_block` | `{ "segment_id": "…", "label": "CL-…" }` |
| `reorder_references` | `{ "block_id": "…", "order": ["ref-id-2", "ref-id-1"] }`; все ID ровно один раз; Picture перенумеруются атомарно |
| `review_take` | `{ "block_id": "…", "take_id": "…", "state": "selected", "notes": "…" }`; состояния unreviewed/selected/accepted/rejected |
| `create_continuation` | `{ "block_id": "…", "take_id": "…" }`; исходный дубль должен иметь native AV и последний PNG |
| `add_asset` | `{ "path": "абсолютный, legacy-относительный или именованный путь @arts/…", "block_id": "…", "role": "персонаж" }`; block_id необязателен |
| `restore_block` | `{ "block_id": "…", "history_file": "имя файла снимка" }`; восстанавливает редактируемые поля, сохраняя текущие дубли |

Загрузка файла: POST `/api/projects/{project}/upload?name={filename}&revision={revision}&block={block}&role={role}`; тело — бинарный файл, X-Studio-Token, Content-Length, до 128 МиБ. Имя/роль URL-кодировать. По умолчанию копия сохраняется под уникальным именем в Production/Imports. UI передаёт `mode=project-media` для сохранения звука в Audio, видео в video, опор в Frames и прочих изображений в References; для уже существующего внутри книги материала удобнее `add_asset` без копирования.

Для импорта локального видео как самостоятельного библиотечного блока передать `mode=library-video` вместо `block` и `role`. Принимаются MP4, WebM, MOV с читаемым видеопотоком. Копия помещается в `video/` в корне выбранного проекта. В успешной ревизии появляются материал и библиотечный блок с одним неотобранным дублем. При конфликте версии или ошибке чтения загрузка не добавляется в проект.

## Генерация — отдельные действия

1. POST `/api/projects/{project}/blocks/{block}/validate`, JSON `{}`: техническая проверка сохранённого блока, возвращает errors/warnings/timing.
2. POST `/api/projects/{project}/blocks/{block}/prepare`, `{ "expected_revision": 4, "actor": "agent:codex" }`: точный снимок и граф без staging/очереди. Возвращает ID подготовленного задания.
3. Только при авторизованном запуске POST `/api/projects/{project}/jobs/{job}/launch`, `{ "actor": "agent:codex" }`: один дубль. Не повторять после разрыва соединения; сначала читать состояние того же job. Изменение блока, входных файлов, подготовленного снимка или реализации ядра даёт 409 до запуска.
4. GET состояния; POST `/api/projects/{project}/jobs/{job}/stop`, `{}` — остановка только принадлежащих заданию процессов с проверкой идентичности.

Для нового успешного задания `take_number` указан в задании и в дубле. Дубль ссылается через `video_asset_id` на монтажный MP4 в `video/STUDIO.<блок>.RunNN.mp4`. Повторный сбор того же задания использует прежний файл и ID дубля. Исходные выходы collect остаются в каталоге задания.

Подготовка не является разрешением на GPU-запуск или серию. После перезапуска сервиса заново получить token; старые задания наблюдать по прежним ID, не создавать дубликаты автоматически. Для изменения промпта всегда сохранить новую ревизию, затем подготовить новое задание. Продолжение создаётся как редактируемый черновик: исходные текстовые фазы нужно привести к новому действию.

Пример чтения из PowerShell без запуска генерации:

```powershell
$studioBase = 'http://127.0.0.1:8787'
$studioSession = Invoke-RestMethod "$studioBase/api/bootstrap"
$studioProject = Invoke-RestMethod "$studioBase/api/projects/claire"
$studioContext = Invoke-RestMethod "$studioBase/api/projects/claire/context?block=$($studioProject.blocks[0].id)"
```

## Таймлайн (0.2)

GET `/api/projects/{project}/timeline`: сохранённый монтаж либо пустой объект версии 1 для старого проекта, без записи. Поля: version, fps, segments, clips, markers, music, clip_audio, undo, redo. GET `/api/projects/{project}/media-info?asset={id}` читает метаданные локального видео на CPU без изменения проекта.

POST `/api/projects/{project}/timeline`, обычный X-Studio-Token и JSON:

```json
{"expected_revision":6,"actor":"agent:codex","action":"move_segment","payload":{"id":"tseg-…","start_frame":240}}
```

Ответ — весь новый проект с revision. Конфликт версии — 409, неверные позиции/участки — 422, без частичной записи. Позиции/длины монтажа — целые кадры fps проекта. Старые 1× клипы сохраняют целый source_in_frame; Studio 0.8 допускает дробные source-позиции при явных speed/source_out_frame. До 200 сегментов, 2000 клипов, 1000 маркеров; предел позиции — 518400 кадров. Исходные blocks/segments и монтажные clips/segments — отдельные сущности.

| action | payload |
|---|---|
| `add_segment` | name, start_frame, duration_frames, необязательный color: mint/blue/violet/amber/rose/teal |
| `move_segment` | id, start_frame; сдвигает все принадлежащие клипы на ту же delta |
| `resize_segment` | id, start_frame и/или duration_frames; клипы сохраняют абсолютные позиции и должны помещаться |
| `update_segment` | id, name и/или color |
| `edit_segment` | id, start_frame, необязательные name/color/duration_frames; атомарно переносит группу и меняет окно |
| `remove_segment` | id; убирает диапазон и его монтажные клипы, сохраняя источники |
| `add_clip` | segment_id, block_id, take_id или null, start_frame, duration_frames, source_in_frame (по умолчанию 0) |
| `update_clip` | id и изменяемые segment_id/take_id/start_frame/duration_frames/source_in_frame |
| `edit_clip_source` | id; source_in_frame, source_out_frame (исключающий), speed; пропущенные поля берутся из текущего клипа |
| `slip_clip` | id, delta_frames: сдвиг обеих границ источника без изменения длины/скорости |
| `split_clip` | id, frame: абсолютный целый кадр внутри клипа |
| `set_clip_anchor` | id, enabled: закрепить/снять закрепление начала; снятие сохраняет положение |
| `set_gap_protected` | id, enabled: защитить/разрешить удаление паузы перед блоком |
| `compact_segment` | segment_id: убрать незащищённые внутренние паузы, сохранив первый блок, окно и закрепления |
| `remove_clip` | id; исходный дубль/файл сохраняется |
| `add_marker` | name, frame |
| `update_marker` | id, изменяемые name/frame |
| `remove_marker` | id |
| `set_music` | asset_id аудио, start_frame, duration_frames; необязательные source_in_frame=0, gain=0.8, muted=false |
| `update_music` | изменяемые start_frame/duration_frames/source_in_frame/gain/muted |
| `remove_music` | пустой объект; файл и библиотечный asset сохраняются |
| `set_clip_audio` | enabled: bool; звук исходных клипов в общем просмотре |
| `undo` / `redo` | пустой объект; до 30 состояний, только монтаж |

Сегменты не пересекаются, клипы лежат внутри назначенных диапазонов без перекрытий; касание границ допустимо. Дубль должен принадлежать исходному блоку. Заглушка имеет take_id=null и source_in_frame=0. При назначении старого видео без длительности сервис читает метаданные в рабочую копию проекта и сохраняет их только вместе с успешной операцией. Размещение не меняет отбор дубля и не запускает генерацию.

Новые операции Studio 0.8 принимают legacy-размещения в timeline.scenario. Speed — конечное число 0.25–4, default 1; source_out_frame больше входа и не превышает реальную длину. Duration_frames равна ceil((out-in)/speed−1e-8). Дробные source-позиции нужны при разделении retimed-видео; это единицы кадров fps проекта, а не индекс native AV-latent. Опциональные anchor_frame (целый кадр либо null) и gap_protected (bool) сохраняются в сценарии и допустимой проекции. Неизвестные времена запрещают уплотнение/разрез. Ошибка бюджета или anchor_overlap сохраняет сценарное намерение/старую допустимую сборку по правилам 0.5. Новые действия доступны также через scenario API с теми же именами; у них clip ID передаётся в id. Ни одно не меняет генерационные входы и не запускает GPU.

## Музыка и звуковая волна (0.3)

Одна музыкальная дорожка `timeline.music` либо null. Объект содержит id, asset_id, start_frame, source_in_frame, duration_frames, gain (0–1), muted (bool). Она может начинаться вне сегмента и продлевать общий просмотр; маркеры просмотр не продлевают. Скорость 1×. Участок ограничен реальной длиной файла. `clip_audio` по умолчанию false. Музыка и звук клипов входят в отмену/повтор, в том числе для старых состояний без этих полей.

GET `/api/projects/{project}/waveform?asset={id}` возвращает seconds, sample_rate=16000, source_sample_rate, channels, peaks_per_second=100, peaks (0–1), source_sha256 и время анализа. Это чтение без изменения revision и без GPU. Производный кэш — `Production/Preview/Audio/<SHA256>.v1.json`; изменение байтов исходника инвалидирует кэш. Анализ ограничен 40 с внутри worker и 45 с снаружи; максимальная длина звука — час. Неверный/неаудио asset и ошибки декодирования возвращают 4xx. Успешный set_music добавляет audio_info к asset атомарно с монтажом.

Для загрузки аудио использовать существующий upload без параметра block либо add_asset с путём в разрешённом каталоге. Форматы: mp3, wav, ogg, flac, m4a, aac, opus; фактическая поддержка зависит от установленного декодера и браузера. Воспроизведение — действие браузера, HTTP endpoint автоматического play нет.

## Черновой экспорт MP4 (0.9)

В 0.10 экспорт дополнительно включает sound_events; остальные ограничения и версии статуса сохраняются.

Экспорт использует отдельные CPU-процессы и `Production/Exports/`, не Jobs генерации. Проект/его revision не изменяются. [Размеры, ограничения и состав сборки](DraftExport.md).

| Метод и путь | Тело / результат |
|---|---|
| GET `/api/projects/{project}/exports/readiness` | Проверка тайминга, выбранных дублей и доступности файлов без запуска экспорта. Возвращает `project_revision`, `ready` и `issues` с кодом, описанием и привязкой к элементу, когда она доступна. |
| GET `/api/projects/{project}/exports` | Последние 50 статусов, новые первыми; UI показывает 12 |
| POST `/api/projects/{project}/exports` | `{ "expected_revision": 30, "profile": "draft", "actor": "agent:codex", "client_key": "export-test-000001" }`; HTTP 202, статус нового либо прежнего экспорта |
| GET `/api/projects/{project}/exports/{export}` | Статус, progress, исходная project_revision, size/fps/frames/seconds; у готового — result и video_url |
| POST `/api/projects/{project}/exports/{export}/stop` | `{ "expected_revision": 2, "actor": "agent:codex" }`; версия **статуса экспорта**, HTTP 202 |
| GET/HEAD `/api/projects/{project}/exports/{export}/video` | Только completed: MP4, одиночный Range/HEAD для перемотки; прочие состояния 409 |

Профили `draft` (по умолчанию, до 896 px), `project` (размер проекта, черновое качество) и `final` (размер проекта, H.264 CRF 18, AAC 320 кбит/с). Параметры кодирования сохраняются в `Request.json`. Создание проверяет версию **проекта**, сохранённые сценарные конфликты, дубли/файлы, размер и лимит 600 с. Одновременно выполняется один экспорт; второй даёт 409. `client_key` необязателен, 16–80 символов `[A-Za-z0-9_-]`; повтор с теми же project_revision/profile/actor возвращает прежний статус без повторного рендера. Изменённые параметры с тем же ключом дают 409. При сетевой ошибке сначала читать список/статус, не генерировать новый ключ вслепую.

Состояния: queued → running → completed/failed; остановка — stopping → cancelled. После перезапуска незавершённый экспорт становится interrupted, если нет подтверждённого завершённого результата. Готовый файл сверяется по SHA256 при восстановлении такого задания; автоматического повторного запуска нет. Статус имеет собственную revision; progress не увеличивает её и не пишет проект.

CLI принимает общие `--url`, `--project`, `--actor` **до** команды. Пример чтения: `studio_agent.py --project claire exports`. Создание: `studio_agent.py --project claire export --revision 30 --profile draft --client-key export-test-000001`. Остановка: сначала `export-status --id export-…`, затем `export-stop --id export-… --revision N` со свежей версией статуса. Все записи проходят обычную проверку X-Studio-Token/Host/Origin.


## Библиотека ассетов

GET `/api/projects/{project}/asset-library` возвращает `project_revision` и `assets` со списками текущих связей `uses`. У найденных на диске файлов `asset_id=null`. GET/HEAD `/api/projects/{project}/asset-library/preview?file={relative_path}` отдаёт найденные медиа с Range только из четырёх каталогов проекта. Зарегистрированные ассеты используют обычный `/api/media`. Чтение каталога не изменяет проект.

Выбор ассета для записи передаётся как `asset_selection` с одним полем `asset_id` или `file` (путь из каталога). `media-info`, `audio-streams` и `waveform` принимают `file` для чтения параметров до регистрации. Для изображений используйте операцию `assign_catalog_image` с `block_id`, `asset_selection` и необязательным `guide_frame`. Действие сценария `add_catalog_video` принимает `segment_id`, `asset_selection`, `source_in_frame`, `duration_frames` и необязательный `audio_stream`. Звуковые действия `add_sound`, `update_sound` и `set_music` также принимают `asset_selection`. Новая регистрация сохраняется в той же ревизии, что и назначение.

## Звук сегмента и блока

Optional массив `timeline.sound_events` читается без миграции старого проекта. Операции через POST `/api/projects/{project}/timeline`, обычные expected_revision/actor/token:

| action | payload |
|---|---|
| `add_sound` | segment_id либо clip_id для звука блока, name, lane (`voice`/`sfx`/`ambience`), asset_id (audio либо video), stream (0-based audio ordinal), offset_frames, source_in_frame, duration_frames, gain (0–1), muted (bool) |
| `update_sound` | id и изменяемые поля из add_sound |
| `remove_sound` | id; исходник сохраняется |

Длины/позиции — целые кадры fps проекта, source_seconds устанавливается сервером после анализа выбранного потока. Offset неотрицательный, скорость звука 1×, до 64 событий. Начало в проекте = начало сегмента + offset_frames. Хвост не обрезается, может продлить общий монтаж/экспорт; preview сегмента ограничен его окном. Событие с clip_id использует начало блока вместо начала сегмента и обрезается по его концу в просмотре и экспорте. Разрез блока делит и его звук с сохранением входа в источник, удаление блока убирает привязанные события. Сегментный звук остаётся независимым. Изменения входят в undo/redo. `mute_native_audio` в add_item/update_item или add_clip/update_clip отключает родное аудио этого размещения при включённом звуке клипов. Чтение не запускает генерацию.

| Метод и путь | Назначение |
|---|---|
| GET `/api/projects/{project}/audio-streams?asset={id}` | Список audio ordinal, codec/channels/sample_rate, language/title; пустой список при отсутствии звука |
| GET `/api/projects/{project}/waveform?asset={id}&stream=1` | Волна выбранного потока аудио **или видео**, default stream=0; SHA/stream-кэш, bounded CPU |
| GET/HEAD `/api/projects/{project}/sound-preview?event={id}` | WAV выбранного потока/участка без gain/mute/delay; Range/HEAD. Именно этот отрезок Web Audio размещает во времени, скорость 1× |

Производные WAV/волна не меняют project revision. WAV-кэш зависит от SHA/stream/in/duration/fps, повторно используется при изменении только времени/gain/mute. Подготовка ограничена 45 с. Пути ограничены книгой. Agent context получает timeline.sound_events и optional sound_sources с путями и зарегистрированными хэшами уникальных доноров (при наличии); путь не доказывает прослушивание.

CLI: `studio_agent.py --project claire timeline` читает монтаж, `timeline --file operation.json --revision N` применяет JSON вида `{"action":"add_sound","payload":{...}}`. Работа с донором через существующие add_asset/upload. Нового GPU-задания эти действия не создают.


## Переходы через чёрный (0.13)

Optional `timeline.transitions`: массив `{left_clip_id,right_clip_id,fade_out_frames,pause_frames,fade_in_frames}`. ID относятся к **монтажным использованиям**, не библиотечным блокам. Пара должна быть соседней в глобальном порядке клипов; допускаются разные сегменты. Один переход на входящий клип, до 2000 записей. Legacy читается без поля/записи.

POST `/api/projects/{project}/timeline`, обычные token/actor/expected_revision:

```json
{"expected_revision":48,"actor":"agent:codex","action":"set_transition","payload":{"left_clip_id":"clip-a","right_clip_id":"clip-b","fade_out_frames":12,"pause_frames":12,"fade_in_frames":12}}
```

`remove_transition` принимает `{ "right_clip_id": "clip-b" }`. Все длительности — неотрицательные целые кадры fps проекта. Нулевые фазы на плотном стыке снимают настройку. Set отклоняется при конфликтном/неизвестном сценарном времени. Запись входит в undo/redo, не меняет клипы/источники/сегменты/музыку/маркеры/закрепления/звуковые события. CLI `timeline --file operation.json --revision N` использует тот же контракт; context.timeline включает optional transitions.

Пусть B — начало правого клипа, G — существующий промежуток. Pause >= G; fade_out + pause - G <= длины левого клипа; fade_in <= длины правого. Затемнение: `[B-pause-fade_out, B-pause)`, чёрный: `[B-pause,B)`, появление: `[B,B+fade_in)`. Входящая и следующая исходящая фазы одного клипа не пересекаются. Меньшая пауза требует отдельного редактирования размещения, лишнее время покрывает хвост левого клипа. Конфликт возвращает 422 без изменения проекта.

Удаление/перестановка/split автоматически убирают только настройки пар, которые перестали быть соседними; undo восстанавливает их. Изменение длительности при сохранённой паре с недостаточным бюджетом отклоняется, а не меняет эффект молча. Экспорт фиксирует transitions и derived transition_windows в снимке. Предпросмотр использует те же midpoint-уровни на сетке fps; звук независим. [Примеры и ограничения](BlackTransitions.md).

## Титры (0.14)

Optional `timeline.titles`: массив в порядке наложения. Старые файлы не мигрируются при чтении. Обычный POST `/api/projects/{project}/timeline` и CLI `timeline --file operation.json --revision N` поддерживают `add_title`, `update_title`, `remove_title`. Общие token/actor/expected_revision, история/undo и конфликт 409 сохраняются. `update_title` принимает `id` и изменяемые поля; `remove_title` — только `id`.

Пример payload для `add_title` (24 fps):

```json
{"name":"Название","text":"Сказки истинной луны\nNero — Satisfy","start_frame":96,"duration_frames":48,"fade_in_frames":6,"fade_out_frames":6,"font_id":"georgia","font_size":52,"color":"#FFFFFF","align":"center","position":"center","background":"black","outline":true}
```

Начало/длительность/плавность — целые кадры проекта; длина минимум один кадр, сумма плавности не больше длительности. Начало абсолютно, титр может продлить таймлайн; операции над клипами/сегментами его не переносят. `align`: left/center/right; `position`: upper/center/lower; `background`: transparent/black. `font_id` только из каталога, пути к шрифтам не принимаются. Текст нормализует CRLF/CR и табуляцию; до 1000 символов/12 ручных строк, до 64 титров и четырёх одновременных. Layout overflow/суммарная площадь PNG >16M возвращает 422 до commit. Поля `id` выдаёт сервер.

| Метод и путь | Назначение |
|---|---|
| GET `/api/title-fonts` | Доступные id/label шрифтов, без локальных путей |
| GET `/api/projects/{project}/title-layers` | Производные PNG и размещение для всех титров: project_revision/canvas_size/layers |
| POST `/api/projects/{project}/title-preview` | `{expected_revision,title:{...}}`, обычный token/origin guard; PNG для чернового оформления без записи проекта/истории |
| GET/HEAD `/api/projects/{project}/title-images/{key}` | Производный PNG, Range/HEAD; key — 64 hex, путь ограничен проектом |

Layer содержит id/key/sha256/canvas_size/x/y/width/height/font_id/font_sha256/wrapped_text/background/url. Это cropped RGBA глифы; чёрный фон создаётся как группа вместе с ними. PNG-кэш зависит от текста/оформления/размера проекта/файла шрифта; время/название/тип фона не меняют глифы. Экспорт фиксирует PNG+SHA и отказывается от подменённого слоя. Agent context получает optional timeline.titles. [Оформление и ограничения](Titles.md).

## Каталоги · 0.15

`GET /api/workspace` — read-only диагностика загруженных mounts, корней чтения/записи,
state/projects и protected_roots. Изменение конфигурации требует перезапуска сервера;
HTTP-операции не расширяют allowlist. Подробнее: [Workspace.md](Workspace.md).

## M4 · Book отключён

Видео/Production/исследования находятся в TrueMoon.Tales.Videos. Конфигурация
`legacy_read_enabled: false` оставляет legacy_root только логическим пространством
старых имён: стат/чтение/запись туда запрещены, unmapped пути возвращают 403.
Aliases перенаправляют относительные и абсолютные старые строки до файлового IO.
Экспорт получает только реальные новые пути. Исторический prepared с config в старом
пространстве имён возвращает 409 без запуска/мутации; подготовить свежий дубль.
Новые prepare/launch работают с новыми физическими путями. Исторические снимки
не переписываются. Для применения конфигурации перезапустить Studio.
