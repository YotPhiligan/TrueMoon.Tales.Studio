# True Moon Studio — локальный API 0.13

Согласованная [модель действий, источников и монтажа](ProjectModel.md) описывает
целевое развитие. Ниже — работающий HTTP-контракт: `blocks` и source `block_id`
обозначают библиотечный источник; `timeline.scenario` пока связывает сценарное
использование с одним клипом. Множественные связи действия ещё не введены.
Переименовывать поля или отправлять новые payload по одному описанию модели нельзя.
[Совместимость и переход](ProjectModel.md#совместимость-с-текущими-проектами).

> Studio 0.15 / M2: код независим от Book; текущие пути, конфигурация и правила
> совместимости описаны в [Workspace.md](Workspace.md). Старые отчёты фиксируют
> прежние версии. GPU запускается только отдельной явной операцией launch.


17.11.2026 [STUDIO-BACKEND4](src/Verification/BackendMedia.md): этап protocol3
добавил MediaInfo/Frame. Protocol4 описан в STUDIO-BACKEND5, protocol5 — в STUDIO-BACKEND6, protocol6 — в STUDIO-BACKEND7. Protocol7 добавил проверку и поиск задания для окна Studio. Protocol8 добавил авторские команды сценария и библиотеки, protocol9 — операции над клипами, protocol10 — дорожки и рабочий просмотр. Protocol11 добавил диапазон рабочего просмотра, protocol12 — структурированные ошибки готовности, protocol13 — список проблем. Protocol14 добавил привязку к версии главы и аниматик всей шкалы. Protocol15 добавил чтение стоп-кадра раскадровки. Protocol16 расширил команды экранного сценария. Protocol17 добавил сохранение состояний стыка сцен. Protocol18 добавил замечания к кадрам. HTTP APIv1, storage schema2 и ProjectContractv1 действуют в текущей поставке.

- Enerit `WorkingReviewAsync` принимает границы `startFrame` и `endFrame`
  в кадрах сохранённого проекта. Правая граница исключается. Пара `0, 0`
  запрашивает весь проект. Запуск сверяет ревизию и SHA, статус и отмена
  используют ID задания. В статусе диапазона `range_start` — первый
  кадр проекта, `frames` — длина файла предпросмотра.
- Рабочий просмотр использует временное CPU-задание той же backend-сессии.
  API долговечных заданий экспорта сохраняет запуск полного монтажа.
- При отказе подготовки Enerit может вернуть в `BackendExportReply.JobJson`
  причину и ID клипа, ассета или дорожки. Для перехода UI сверяет `revision`
  и `fingerprint`. Ошибка пересечения отключённого legacy-корня не имеет
  цели перехода. Физические пути и текст системного исключения не передаются.
- Enerit `CheckReadinessAsync` читает сохранённые `expectedRevision` и
  `fingerprint` до и после проверки монтажа. В `JobJson` возвращаются
  `revision`, `fingerprint`, `total` и до 24 элементов `issues`.
  Порядок соответствует кадрам проекта, общие ошибки идут первыми.
  Проверка не стартует CPU-рендер и не создаёт экспортное задание.
- `ReadAdaptationTextAsync` читает UTF-8 Markdown из разрешённого `@texts`, до
  2 МиБ. Номера строк начинаются с 1, обе границы включаются. `FragmentJson`
  содержит логический путь, SHA-256 исходных байтов, строки и фрагмент до
  80 строк и 4096 символов. `bind_adaptation_text` сверяет SHA главы при
  сохранении. При изменении главы возвращается `text_changed`. Привязка и
  `save_animatic_still` используют writer сценария и ожидаемые ревизию и SHA
  проекта. `reference: null` удаляет привязку.
- `WorkingReviewAsync` с операцией `start_animatic` принимает только границы
  `0, 0`. Сохраняет ревизию, SHA снимка и отпечатки входов, возвращает
  `animatic: true` в статусе временного задания. При отсутствии длительности,
  пересечении или выходе временного плана из окна возможен `animatic_timing`.
  Статус, отмена и воспроизведение используют существующие операции Enerit.
- Enerit `StoryboardStillAsync` принимает ID сегмента и действия, ревизию и
  SHA проекта. Путь берётся из сохранённой привязки действия и проверяется
  общим media resolver. Ответ содержит один CPU-кадр BGRA 480 × 270 с SHA
  пикселей. `asset_id` в этом ответе равен ID действия, `frame_index` равен 0.
  Снимок проверяется до и после чтения. Видео раскадровки читается через
  существующие `MediaInfoAsync` и `FrameAsync`.
- В Enerit `save_action` принимает опциональное `sound_notes`. Пропуск или
  `null` сохраняет прежнюю заметку, пустая строка очищает её. Назначение
  сцены хранится в `description` сегмента, видимое действие в `description`
  блока. `move_scenario_action` ставит `action_id` перед `before_action_id`
  внутри `segment_id`. `before_action_id: null` ставит действие в конец.
  Writer сверяет сохранённую ревизию и SHA. Если ранее размещённый временный
  план теряет позицию, возвращается `scenario_order_timing`.
  Для окна мигрированного сценария сохранённая перестановка фиксирует
  текущее ручное размещение клипов в этом окне.
- Enerit protocol19 добавляет команды `EditScenarioAsync` для эталонов.
  `add_reference_version` с `reference_id: null` создаёт эталон по `name`,
  `kind`, `label` и `image_reference`. Варианты `kind` — `character`,
  `costume`, `location`, `prop`, `style`. С указанным `reference_id` команда
  добавляет версию и принимает `name: null`, `kind: null`. Backend фиксирует
  SHA-256 PNG/JPEG из разрешённого `@mount`. Номера версий начинаются с 1.
  `assign_scene_reference` принимает `segment_id`, опциональный `action_id`,
  `reference_id` и номер `version`. `version: null` снимает назначение.
  `record_take_reference` принимает `source_id`, `take_id`, `reference_id`
  и номер `version`. Перед записью повторно сверяется SHA. Повтор с той же
  версией возвращает прежний снимок, попытка выбрать другую даёт
  `reference_input_recorded`. Все команды используют ревизию и SHA
  проекта и текущего writer. HTTP APIv1, schema2 и ProjectContract v1 сохранены.
- Enerit protocol20 добавляет `link_draft_final_take` через `EditScenarioAsync`.
  Поля `source_id`, `draft_take_id`, `final_take_id` выбирают два готовых
  видео одного источника. `method` принимает `direct` или `full_transfer`.
  При Full Transfer ожидаются 24 кадра/с и равное число кадров. Writer
  сверяет ревизию и SHA проекта, вычисляет SHA файлов (до 512 МиБ каждый)
  и сохраняет снимки в свойствах финала. Повтор той же связи идемпотентен.
  Для другого финального результата импортируется новый дубль.
- `TakePairFrameAsync` выбирает связанную пару по `sourceId` и `finalTakeId`,
  сторону по `draft` и время по `finalFrame`. Ревизия и SHA проекта
  сверяются до и после чтения. Ответ использует `BackendMediaReply` с
  `source_sha256` и SHA пикселей. Если файл изменился, возвращается
  `take_pair_changed`. Для прямой генерации кадр черновика выбирается по
  времени финала, по окончании черновика показывается последний кадр.
- Enerit protocol21 добавляет `PrepareFullTransferAsync`. Команда принимает
  ожидаемые ревизию, SHA проекта и JSON с `client_key` (32 строчные hex-цифры),
  `source_id`, `draft_take_id`, `seed` (`uint64`), `prompt`, `images`
  (`reference` и `role`), `guide_picture`, `motion_role`, `motion_notes`.
  Длина результата следует из 24 fps черновика на сетке 17k+5, от 56 до
  192 кадров. Допускается 1–8 PNG/JPEG из разрешённых `@mount`, один Guide 0.
  Writer публикует `Prepared.json` только после проверки входов, графов и
  снимка. Повтор того же ключа со снимком и входами возвращает прежний ответ.
  Изменение запроса даёт `transfer_key_conflict`, повреждение графа или
  конфигурации — `transfer_damaged`. Ответ содержит SHA черновика, запроса,
  конфигурации и Build, а также `staged: false`, `enqueued: false`.
  Проектная ревизия и история записи не меняются.
- Enerit protocol22 добавляет `FullTransferJobAsync` для текущего проекта.
  `jobId` состоит из `ft-` и 32 строчных hex-цифр. При `expectedRequestSha: null`
  возвращается проверенная квитанция по сохранённому снимку. Указанный SHA
  включает staging под текущим writer. Повтор команды сверяет содержимое
  и копирует только отсутствующие входы, `transfer_stage_conflict` сохраняет
  файл с другим SHA. Смена разрешённого пути даёт `transfer_stage_path_changed`.
  После проверки `Staged.json` фиксирует `state: staged`, `staged: true`,
  `enqueued: false`. `Prepared.json`, графы Preview и ревизия проекта сохранены.
- Enerit protocol23 добавляет `FullTransferRunAsync` с операциями
  `status`, `launch`, `stop` и `CollectFullTransferAsync`. Обе команды
  сверяют ID задания и SHA запроса. Запуск сверяет точную ревизию и SHA
  первоначального снимка и наличие всех staged-файлов. `Run/Launch.json`
  фиксирует SHA манифеста запуска до старта worker. Повторный запуск того
  же ID отклоняется с `transfer_already_launched`.
- Статус возвращает `prepared`, `staged`, `running`, `stopping`,
  `cancelled`, `timed_out`, `interrupted`, `failed` или `completed`.
  В `completed` возвращается записанный SHA MP4. После потери backend
  сохраняются проверяемый завершённый результат либо `interrupted`, если
  есть только запуск. Команды статуса и открытия не отправляют граф.
- Сохранение сверяет текущие ревизию и SHA под writer, проверяет исходный
  черновик и SHA готового MP4 и добавляет дубль с постоянным ID в тот же
  источник. Монтажное назначение не меняется. Повтор той же регистрации
  с актуальной ревизией не добавляет новую запись истории.

- POST /api/backend/v1/media-info: asset_id, expected_revision, fingerprint.
- POST /api/backend/v1/frame: те же обязательные поля и frame>=0.
- Ответ api_version1/schema_version2/media: asset_id/revision/fingerprint,
  fps/frame_count/source_width/source_height, frame_index(-1дляmetadata),
  width480/height270, pixels(base64BGRA), pixel_sha256, error_code.
  В metadata pixels и pixel_sha256 пустые; физические пути не возвращаются.
- Зарегистрированный video asset разрешается по trusted mounts/aliases с deny
  protected/reparse/traversal/disabled legacy. Baseline до/после decode;
  read не захватывает writer и не пишет revision/history/cache.
- 404 media_not_found,403 access_denied,422 invalid_media/invalid_frame,
  409 revision_conflict/migration/recovery,429 media_busy,503 decoder_unavailable,
  504 media_timeout; прежние auth/Host/body/field limits/no retry сохранены.
- CLI backend-media и backend-frame --asset/--revision/--sha256; frame требует
  --frame и --output нового PNG. Проверяются размеры/types/SHA, existing/ADS
  output запрещён. Tool paths задаёт trusted config, не HTTP/IPC.

17.11.2026: отдельный [backend protocol2](src/Verification/BackendMove.md)
поддерживает AcquireWriter/MoveClip/MigrateProject/RecoverProject с launch
capability. MoveClip получает clip ID/start, expected_revision и exact-byte SHA;
возвращает ProjectContract v1. Пути и права задаёт trusted launcher/config.
Сохраняются stable ошибки writer_required/write_not_enabled/writer_busy,
revision_conflict/migration_required/recovery_required/recovery_conflict,
invalid_move/access_denied. Отмена/no-op не пишут; неопределённый ответ требует
явного перечитывания, без автоматического retry. Python commit использует общий
writer lease, а schema2 get/commit возвращают HTTP409; эти HTTP-маршруты пока не
являются адаптером schema2-команд. Новые CLI: --edit-project, --migrate-project,
--recover-project с теми же --config/allowlist. Read-only --project сохранён.

17.11.2026 [STUDIO-BACKEND3](src/Verification/BackendHttp.md): отдельный Kestrel API
в процессе владельца с единственным backend writer, .NET11/ASP.NET Core11 runtime.
--serve-project выбирает schema2-проект/config, порт0 по умолчанию; --with-ui
использует ту же сессию. Ready JSON выдаёт URL и HTTP token. Token требуется в
X-Studio-Token для GET/POST; bootstrap/выбор пути/смена проекта через этот API отсутствуют.

- GET /api/backend/v1/project — api_version1/schema_version2/fingerprint/scenario_active/
  project(ProjectContract v1). Fingerprint — SHA точных байтов хранилища.
- POST /api/backend/v1/move-clip — строго clip_id, start_frame, expected_revision,
  fingerprint; одна команда Enerit, ответ того же формата. No-op не пишет.
- 409 для revision/SHA conflict и writer/migration/recovery refusal,422 invalid_move,
  400 malformed/unknown/duplicate fields,403 authority,405 method,413 size,415 content,
  503 backend unavailable,504 deadline,429 busy. Ошибка содержит error/message/
  reconciliation_required. После неопределённого ответа перечитать явно, без retry.
- CLI: studio_agent.py backend-project/backend-move с --url и --token либо
  STUDIO_BACKEND_HTTP_TOKEN; для Move обязательны --clip/--start-frame/--revision/
  --sha256. --project отвергается: проект закреплён владельцем. Actor в этом v1
  фиксирован backend; формат legacy HTTP и старые CLI-команды не меняются.

Базовый адрес `http://127.0.0.1:8787`. UI и внешние агенты используют общие операции. Область файлов задаётся зарегистрированными корнями конфигурации Studio: библиотеки доступны для чтения, запись разрешена только в явные рабочие каталоги. Защищённый TrueMoon недоступен. Это HTTP API; MCP-обёртка ещё не реализована.

В 0.4 доступны карточка brief, расширенный `/agent/context` и долговечные `/agent/requests`. [Полный контракт, состояния и CLI](AgentIntegration.md). Legacy `/context` сохранён. Bootstrap объявляет возможности agent и automatic_dispatch=false. Ни чтение контекста, ни создание/claim задания генерацию не запускают.

Задание `scope.intent=breakdown_segment` работает в режиме `propose`. Передайте `proposal` при `finish` вместе с текстом `result`. GET `/api/projects/{pid}/agent/requests/{rid}/breakdown` рассчитывает бюджет текущей ревизии и возвращает `items`, `status`, `issues`, `project_revision`, `fps` и поля бюджета в кадрах. POST на тот же путь принимает `expected_revision` проекта из предпросмотра и `actor`, повторно проверяет бюджет и применяет весь набор одной операцией. Описание полей предложения и CLI приведено в [ScenarioAgent.md](ScenarioAgent.md#разбивка-сегмента-с-агентом).

В 0.5 доступны GET/POST `/api/projects/{id}/scenario` и CLI scenario; [операции и правила времени](Scenario.md). Поле timeline.scenario хранит сценарные намерения, включая неизвестное время и конфликты; допустимая монтажная проекция обновляется атомарно. Пространственные правки активного сценария через прежний timeline API используют ту же последовательную укладку. `add_item`, `update_item` и `add_catalog_video` принимают необязательные сценарные заметки `sound_notes` и `continuity_notes` в `payload`. Пустая строка очищает заметку. Действие `set_animatic_frame` принимает `segment_id`, `id` сценарного использования и `asset_selection`. Выберите изображение по `asset_id` или по `file` из каталога, либо передайте `null` для снятия выбора. Выбранный `animatic_asset_id` хранится у сценарного использования и участвует в отмене.

## Read-only IPC backend

[STUDIO-BACKEND1](src/Verification/BackendReadOnly.md) добавляет отдельные типизированные Hello/OpenProject через Enerit. HTTP-маршруты ниже остаются прежними; смены writer и записи через IPC в этом этапе нет.

## Формат хранилища

Ниже описан действующий Python HTTP API для schema 1.
[Хранилище C# schema 2 и явная миграция](src/ProjectStore.md) ещё не подключены
к этим маршрутам; транспортный ProjectContract и schema 2 envelope нельзя
отправлять как payload старых операций. Переключение владельца записи
и backend/IPC выполняется отдельным этапом.

STUDIO-BACKEND5 добавляет Enerit protocol4 OpenPlayback/ReadPlayback/ClosePlayback
для UI-просмотра выбранного клипа со звуком и revision/SHA. HTTPv1 и маршруты
CLI сохраняются; HTTP playback endpoint этим этапом не вводится.
[Контракт и границы](src/Verification/BackendPlayback.md).

STUDIO-BACKEND6 добавляет TrimClipAsync в Enerit protocol5. HTTPv1
получает POST `/api/backend/v1/trim-clip` с clip_id, source_in_frame,
source_out_frame, start_frame, expected_revision, fingerprint. Source-in/out —
конечные неотрицательные числа в кадрах проекта, start — целый абсолютный кадр.
Обрезка не меняет speed/take; один writer, no-op без commit, baseline conflict409,
invalid_trim422. CLI: `backend-trim --clip ID --source-in N --source-out N
--start N --revision R --sha256 SHA`; URL/token задаются как у backend-move.
[Контракт и проверки](src/Verification/BackendTrim.md).

## CPU MP4 для schema2

STUDIO-BACKEND7 добавляет экспорт через Enerit protocol6 к тому же владельцу
проекта. Все запросы ниже — POST с JSON и обычным `X-Studio-Token`. Префикс
маршрутов — `/api/backend/v1/`.

| Маршрут | Обязательные поля |
|---|---|
| `export-start` | `client_key`, `profile` (draft/project/final), `expected_revision`, `fingerprint` сохранённого проекта |
| `export-status` | `id` |
| `export-cancel` | `id`, `expected_job_revision` |
| `export-recover` | `id` |
| `export-chunk` | `id`, `artifact_sha256`, `offset` в байтах |

Запуск, отмена и recovery требуют writer-сессию. Повтор запуска с тем же
ключом и исходными данными возвращает сохранённое задание, включая terminal
status. Тот же ключ для другого профиля или baseline даёт конфликт. Статус
содержит отдельную ревизию, SHA запроса, прогресс и метаданные завершённого
MP4. Chunk выдаёт до 256 КиБ бинарных данных с `Content-Length`. CLI сверяет
размер и итоговый SHA перед созданием нового файла. Настройка, ошибки и
полный пример цикла приведены в [контракте экспорта](src/Verification/BackendExport.md).

Enerit protocol7 добавляет `CheckExportAsync` и `FindExportAsync` для C#-окна.
Предстартовая проверка принимает профиль, сохранённую ревизию и SHA, сверяет
доступность входов и возвращает размер, FPS и длительность. Она не принимает
задание и не запускает рендер. Поиск принимает ключ отправки, привязанный к
текущему проекту, и читает статус принятого задания без повторной отправки.
[Сценарий в окне](src/WindowDesign.md#workspace-export).

<a id="scenario-edits"></a>
## Сценарий и материалы через Enerit

Protocol8 добавляет `EditScenarioAsync` для интерфейса того же EXE. Команда
принимает ключ сеанса, наблюдаемую ревизию и SHA, а также ограниченный
JSON с полем `operation`. Допустимы `save_segment`, `save_action`,
`link_source`, `import_video`, `relink_video`, `place_clip` и `assign_take`.
Тип команды выбирает набор полей. Лишние поля и повтор ключей отклоняются.
Идентификаторы новых сущностей выдаёт backend.

Команда использует текущего writer. Успех возвращает снимок с новой ревизией,
совпадающая правка возвращает прежний снимок. Конфликт требует перечитывания
перед следующей явной отправкой. Видео проверяется по конфигурации resolver
и данным ffprobe. Правки доступны в C#-экране, который читает сравниваемые
кадры штатным `FrameAsync`. HTTP/CLI читают результат через APIv1.
[Экран и поведение](src/WindowDesign.md#scenario-library).

Protocol17 добавляет `save_scene_join` с полями `left_window_id`,
`right_window_id`, `exit_state`, `entry_state` и `bridge`. Нужны
соседние сохранённые окна двух разных сцен в порядке шкалы. Текстовые поля
обязательны и ограничены 1600 знаками. Связка требует оба состояния. Три
пустых поля удаляют запись. Один вызов сохраняет её в
`project.properties.studio_scene_joins_v1` с общей ревизией и SHA. Новый
HTTP/CLI-метод для этой записи не добавлялся.
[Стык в раскадровке](SegmentPreview.md#studio-scene-joins).

Protocol18 добавляет `save_frame_note`. Для создания передайте
`id: null`, `anchor` и `text`. Для правки текста передайте ID заметки
и `anchor: null`. Backend сверяет ревизию, SHA проекта и показанный
кадр перед созданием. Привязка видео содержит ID окна, клипа, дубля
и ассета, исходный номер и частоту кадров, первоначальную позицию и SHA
декодированного кадра. Привязка опоры содержит ID окна и действия,
смещение в плане и SHA файла. `StoryboardStillAsync` возвращает этот
SHA в `source_sha256`. Записи хранятся в `project.properties.studio_frame_notes_v1`.
[Замечания в раскадровке](SegmentPreview.md#studio-frame-notes).

<a id="montage-edits"></a>
## Правки монтажа и просмотр CPU MP4 через Enerit

Protocol9 расширяет JSON команды `EditScenarioAsync`. Требуются ключ
сеанса, `expected_revision` и SHA256 точных байтов проекта.

| `operation` | Поля |
|---|---|
| `split_clip` | `clip_id`, `frame` — абсолютный кадр строго внутри клипа |
| `remove_clip` | `clip_id` |
| `undo_montage`, `redo_montage` | Без дополнительных полей |
| `set_black_transition` | `left_id`, `right_id`, `fade_out`, `pause`, `fade_in` — целые длительности в кадрах. Три нуля удаляют переход. |

Split сохраняет ID слева, создаёт ID справа и делит участок источника по
скорости клипа. Действия, дубли и окна сохраняются. Удаление защищённого
клипа возвращает `montage_protected_clip`. В protocol9 правка клипа с
затронутым звуком отклонялась. Protocol10 распространяет split и удаление
на привязанные события. Переход требует соседние
клипы, чёрную паузу не короче разрыва и место для обоих затуханий. Ошибка
геометрии возвращает `montage_transition_conflict`.

`BackendProjectReply` сообщает `CanUndoMontage` и `CanRedoMontage` для точной
ревизии/SHA. История из 16 правок живёт в текущем процессе writer. Undo/redo
публикуют выбранный снимок новой ревизией с обычным журналом и byte-exact
предыдущим состоянием в истории. Промежуточная сторонняя запись блокирует
применение, а новая монтажная правка сбрасывает ветку redo.

`FrameAsync` и `OpenPlaybackAsync` читают завершённый MP4 текущего проекта
по зарезервированному ID `review-{export-id}-{all|start_frame}`. В режиме
сегмента запросы кадров отсчитываются от начала окна. Backend сверяет
ревизию, SHA проекта, статус, размер и SHA артефакта. Звук берётся из AAC
того же MP4 и выдаётся в PCM. HTTPv1 и CLI сохраняют прежние команды.
Просмотр использует готовый экспорт без записи проекта или нового задания.

<a id="buffered-review-playback"></a>
## Звук готового предпросмотра через Enerit

Protocol27 добавляет вид потока `review_audio` для `OpenPlaybackAsync`.
Он принимает ID готового предпросмотра и локальный кадр старта. Звуковой
поток работает одновременно с потоком видео. Каждый поток имеет свой ID,
ограниченную очередь и отдельное закрытие через `ClosePlaybackAsync`.
Оба проверяют ревизию и SHA проекта при открытии и чтении. Перед декодированием
backend сверяет SHA MP4 и удерживает проверенный файл открытым.

`TotalSamples` описывает весь интервал предпросмотра. Первый
`SampleOffset` соответствует локальному кадру старта на абсолютной
звуковой сетке проекта. Завершение возвращает `SampleOffset = TotalSamples`.
PCM передаётся порциями до 32 КиБ с SHA, как и для звука отдельного клипа.

<a id="episode-readiness"></a>
## Проверка эпизода через Enerit

Protocol30 добавляет параметр `episode` в `CheckReadinessAsync`.
`false` возвращает техническую проверку монтажа. При `true` отчёт дополнен
полем `episode` с хронометражем (`frames`, `fps`), счётчиками окон,
описанных стыков и включённых речевых событий. `episode.notes` — число
замечаний к эпизоду, остальные строки учитываются как технические ошибки.
Идентификатор окна в `window_id` относится к сохранённому снимку. Для стыка
это следующее окно, а `frame` указывает его начало. Запрос и переходы
проверяют ревизию и SHA проекта. Список до 24 строк и общее число замечаний
сохраняются, экспорт и запись проекта не запускаются.

<a id="timeline-interchange"></a>
## Экспорт монтажных данных через Enerit

Protocol29 добавляет `ExportInterchangeAsync`. Команда принимает ключ сеанса,
ревизию и SHA сохранённого проекта schema2. Backend сверяет снимок до сбора
данных и перед записью результата. Под `exports_root` появляется новый каталог
`Montage-<id>` с `Timeline.xml` и `Report.json`. Каталог публикуется одной
операцией. Ответ содержит имя каталога, ревизию, SHA проекта, SHA XML и число
замечаний. Сборка MP4 и рабочие задания при этом не запускаются.

XML использует Final Cut Pro 7 `xmeml` v4 и локальные URI оригиналов.
Монтажные координаты даны в кадрах проекта с целым FPS. Отчёт сохраняет
окна, полные размещения, маркеры, переходы, титры, звуковые события и коды
ограничений. Для OpenShot маркеры читаются из отчёта. Клип с изменённой
скоростью или дробной обрезкой сохраняется в отчёте с точными координатами
и пропускается в XML. Переходы через чёрный для представимых клипов
передаются ключами непрозрачности. Звук дубля использует первый поток.
Отдельная дорожка поддерживает WAV, FLAC и AAC с первым потоком и целой
обрезкой. Неподдерживаемые размещения, титры и субтитры указаны в отчёте.
[Приёмка](Status.md#studio-ui10-interchange).

<a id="selective-proxies"></a>
## Прокси предпросмотра через Enerit

Protocol28 добавляет `TakeProxyAsync` для выбранных ID источника и дубля.
Операции `prepare_4k`, `prepare_manual`, `status`, `cancel` и `original`
проверяют ревизию и SHA проекта. Подготовка выполняется в фоне и публикует
результат после сверки оригинала, покадровой сетки и аудиопакетов. Для
повторного включения ранее проверенного прокси используется та же
операция подготовки. Квитанция хранится в `cache_root` отдельно от истории.

`PreviewFrameAsync`, видео отдельного клипа и рабочий просмотр используют
доступный выбранный прокси с проверкой SHA оригинала и производного файла.
При промахе кэша они читают оригинал. `FrameAsync`, сравнение дублей
и любой самостоятельный экспорт используют исходные ассеты.

<a id="dependency-comparison"></a>
## Сравнение сохранённых зависимостей через Enerit

Protocol25 добавляет `CheckDependenciesAsync`. Передайте номер более
ранней ревизии schema2, текущую ревизию и SHA проекта. Ответ содержит
ревизии и SHA обоих снимков, общее число строк и ограниченный список
изменений. Запрос читает архивный JSON проверяемой ревизии без writer.
Смена текущего снимка во время чтения даёт `revision_conflict`.

Строки связываются со стабильными ID сцен, действий, дублей и клипов,
когда такие связи есть в сохранённых данных. Изменение входов монтажа
включает поиск завершённых MP4 с точными ревизией и SHA выбранного снимка.
Проверяются квитанция и SHA запроса. Ответ содержит сохранённый SHA MP4.
При недоступном хранилище, ошибке чтения или превышении лимита поиска
`export_search_complete` имеет значение `false`. Сравнение опирается на
сохранённые метаданные и снимки версий. Файлы медиа сверяются при проверке
готовности и воспроизведении результата.

<a id="job-snapshot-comparison"></a>
## Сравнение заданий и подготовка версии через Enerit

Protocol26 добавляет `CompareJobSnapshotsAsync`. Передайте два разных ID
заданий одного проекта, текущую ревизию и SHA проекта. Чтение сверяет
текущий снимок до и после сравнения. Ответ содержит точный текст и SHA
промпта, ревизию задания и сохранённую опору движения. Для Omni.v2 Full
Transfer (`ft-…`) это дубль и SHA Video 1 из проверенного `Request.json`.
Для прежнего Motion Context (`job-…`) это `context_take_id` и SHA
предыдущего latent, если он записан в `source_files`. Сверяются SHA
`Prompt.txt`, `Config.json` и `Block.snapshot.json` из архивной квитанции.
Записанные старые пути сопоставляются как метаданные. Чтение медиа по ним
не выполняется. Ошибки сравнения возвращаются как `job_snapshot_ids`,
`job_snapshot_missing`, `job_snapshot_invalid` или `revision_conflict`.

`PrepareFullTransferAsync` принимает необязательное поле `predecessor`
с `id` и `request_sha256` одного прежнего задания Full Transfer того же
источника. Writer сверяет прежний запрос, ревизию и SHA текущего проекта,
а затем фиксирует промпт, выбранный черновик и SHA фактических входов
в отдельном `Request.json`. У новой версии свой `client_key`. Повтор
того же запроса возвращает прежнюю квитанцию, а смена входов с тем же
ключом возвращает конфликт. Подготовка создаёт CPU-граф и не копирует
входы в ComfyUI и не отправляет граф в очередь.

<a id="montage-tracks"></a>
## Дорожки и рабочий предпросмотр через Enerit

Protocol10 расширяет `EditScenarioAsync` командами `save_music`, `save_sound`,
`save_title`, `save_marker`, `remove_track_item` и `set_clip_audio`.
Они используют ту же ревизию, SHA, writer и историю. Данные размещаются в
совместимых свойствах `music`, `sound_events`, `titles`, `markers` и `clip_audio`.
`save_sound` принимает смещение от окна или клипа в кадрах проекта.
Аудиовход задаётся зарегистрированным ID либо явным `@mount/путь`. Перед
записью resolver проверяет путь, ffprobe проверяет поток и длину исходника.
Split перемещает событие к правой части или делит его на границе разреза,
сохраняя исходник, громкость и абсолютное монтажное время.

Protocol24 добавляет необязательный объект `speech` к `save_sound` на
дорожке `voice`. Речевое событие принадлежит окну сегмента, а `action_id`
может уточнять действие в этой сцене. Укажите `role` (`narrator` или
`dialogue`), `speaker` и `version`. При `version: 0` backend записывает
новую версию по `input` и `stream`, фиксирует `origin` и вычисляет SHA-256.
Для выбора прежней версии передайте её номер. Файл, поток и SHA берутся
из сохранённой версии. Остальные версии сохраняются в `sound_events[].speech`.

`subtitle` задаёт текст, `subtitle_offset_frames` и
`subtitle_duration_frames` задают полуоткрытый интервал от начала речи.
После смены версии, окна или тайминга текущую связку подтверждает поле
`confirm_subtitle`. Диагностика `subtitle_review` ведёт к речевой дорожке,
а `speech_source_changed` сообщает о несовпадении SHA выбранной записи.
Подтверждённые субтитры накладываются на видео общим CPU-рендерером.
Прежние звуковые события сохраняют прежний формат.

`WorkingReviewAsync` поддерживает `start`, `status` и `cancel`.
Start принимает уникальный ключ, ревизию и SHA проекта. Cancel принимает
ID и ревизию задания. Временный рендер использует ту же CPU-проекцию с
профилем `draft` и тот же лимит одного активного CPU worker. По завершении
`FrameAsync` и playback читают его проверенный файл по ID просмотра.
Временное задание принадлежит backend-сессии и очищается при её штатном
закрытии. HTTP API экспорта и CLI сохраняют собственный список артефактов.

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
