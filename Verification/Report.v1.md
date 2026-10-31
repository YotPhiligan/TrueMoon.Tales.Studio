# True Moon Studio 0.1 — проверка первого выпуска

30.10.2026. Windows, portable Python 3.12, локальный UI на 127.0.0.1:8787. [Снимок интерфейса](Studio.Desktop.v1.jpg), [проверки файлов и хэши реализации](Checks.v1.json).

Последующая проверка по отдельному запросу автора: [один реальный GPU-дубль через Studio и создание продолжения](Control.Run01.Report.md). Ниже сохранено состояние первоначального выпуска до GPU-прохода; его хэши и счётчики относятся к тому снимку.

## Результат

Первая версия открывает реальные проекты и материалы, сохраняет/восстанавливает блоки, показывает MP4 и сравнение, выдаёт контекст для агента и подготавливает native граф Omni.v1.1 без staging/очереди. Сохранены 54 импортированных карточки Клэр с 61 дублем и 37 карточек опенинга с 34 дублями; это результат импорта, а не утверждённая монтажная структура. Ни один дубль не принят автоматически. Сравнительные MP4 доступны как материалы и не считаются отдельными генерационными дублями.

## Автоматические проверки

Команды из корня книги:

```powershell
& 'E:/ComfyUI_windows_portable/python_embeded/python.exe' -s -B -m unittest discover -s TrueMoonTales/AMV/UI/tests -p 'test_*.py' -q
node TrueMoonTales/AMV/UI/tests/test_logic.mjs
node --check TrueMoonTales/AMV/UI/static/app.js
node --check TrueMoonTales/AMV/UI/static/logic.js
git diff --check
```

**52 unittest — OK, 3,235 с**; JS: PASS для шести разделов, сетки кадров/context, удаления Picture, escaping и timecode. Синтаксис JS и Python AST прошёл. `git diff --check` не выявил ошибок; Git выдал обычное уведомление о LF/CRLF ранее изменённого Opening/Plan.md. Тесты используют временные проекты, временный HTTP-сервер и имитацию границы запуска, не затрагивают GPU или чужие процессы.

| Требование | Конкретное свидетельство |
|---|---|
| Сохранение после перезапуска и неизменность источников | `test_edit_creates_immutable_history_and_survives_restart_without_touching_sources` |
| Конкурентная запись без затирания | `test_concurrent_writes_at_same_revision_have_exactly_one_winner` |
| Перестановка Picture и сохранение Guides | `test_reference_cycle_renumbers_prompt_once_and_preserves_guide_identity` |
| Проверка парного native AV и пространственного размера | `test_context_requires_paired_av_and_matching_size` |
| Подготовка без staging/запуска | `test_prepare_freezes_exact_config_without_staging_or_starting_worker` |
| Отказ после изменения снимка/ядра | `test_changed_block_snapshot_refuses_launch_before_intent`, `test_changed_runner_implementation_refuses_launch_before_intent` |
| Защита от второго запуска | `test_launch_is_exclusive_and_second_click_does_not_start_another_thread` |
| Остановка только проверенных процессов | `test_stop_with_unverified_runner_does_not_kill_a_process`, `test_stop_kills_only_verified_runner_and_its_verified_children` |
| Восстановление без повторной подачи | `test_restart_observes_owned_native_server_without_resubmitting` |
| MP4 Range/HEAD для перемотки | `test_media_full_ranges_suffix_and_head_return_correct_bytes` |
| Закрытие плеера без ложного 500/ошибки сервера | `test_abandoned_media_connection_closes_without_500_or_server_error` |
| Ограничение локального HTTP | `test_foreign_host_origin_and_fetch_site_are_rejected` |
| Импорт не выдаёт сравнение за генерацию | `test_import_comparison_is_asset_and_new_is_the_only_generation_take` |

В ходе проверки исправлены конфликт имени `headers` с BaseHTTPRequestHandler, обработка обрезанных/некорректных заголовков AV, контроль изменения подготовленных Config/Prompt/snapshot/ядра и округление сетки времени JS в соответствии с Python. Для port binding включён exclusive bind, для повторного запуска ярлыка — обнаружение уже работающего Studio до импорта/восстановления. При завершении просмотра обнаружен штатный Windows ConnectionAbortedError; обработка расширена на разрывы соединения. Регрессионный HTTP-тест проверяет три вида разрыва и доступность сервера после них.

## Браузерная проверка

Проверено в настоящем браузере через UI:

- Открытие MC03: пять референсов загружаются, две временные опоры связаны с Picture 4/5; источник 56 кадров / 2,333 с, Context 22 → 34 новых / 1,417 с.
- «Проверить» возвращает отсутствие ошибок и предупреждение о приоритете контекста над стартовым Guide.
- «Подготовить» создало **job-8f79f70c84104d99**, профиль omni-context. Native Build: staged=false, 56 кадров, API-хэш совпадает. Нет Launch.intent, Run или Submission.json. Хэши исходников, снимка входов и ядра совпадают; новое GPU-задание не создавалось.
- Изменение заметки → сохранить → обновить страницу → восстановить предыдущую версию через историю. Текст заметки возвращён точно, текущая revision Клэр — 4. История временной проверочной правки сохранена.
- Два настоящих MC03 MP4 в сравнении декодируются как 896×512 / 1,416667 с, одновременно воспроизводятся без media error. Никакой приёмки дублей эта проверка не выполняла.
- Вкладка ИИ выводит JSON фактического блока; исходная Markdown-карточка открывается и читается. Переключение на опенинг показывает обзор с 37 блоками, возврат к Клэр работает.
- После перезапуска сервиса сохранённые версии и подготовленное задание восстановились. В консоли браузера ошибок/предупреждений не обнаружено. Вкладки инспектора возвращаются к началу при переключении.

## Границы проверки и следующий проход

Новый GPU-дубль через Studio **не запускался**. Реальная цепочка запуск → GPU → collect → продолжение и аварийная остановка под нагрузкой ещё не проверены; тесты процессов не заменяют её. Таймеры и native операции переиспользованы из Omni.v1.1, а не заново объявлены доказанными по UI.

Встроенного LLM, MCP, Full Transfer через Video 1, монтажа, переходов, титров, музыки и финального рендера в 0.1 нет. Узкий мобильный интерфейс не был целью этого выпуска; проверена настольная компоновка. Художественное качество и точность движения оценивает автор отдельно от технического успеха.

Собственный сервер предпросмотра после проверки остановлен. Для работы запускать Start-Studio.cmd. Следующий шаг UI-02 — один отдельно разрешённый контрольный дубль из интерфейса и проверка сохранённых результата/метрик/продолжения.
