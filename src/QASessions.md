# Процессы проверок Studio

Повтор native-падения, ProcDump и автоматический App lifecycle —
[диагностический отчёт](NativeCrash.md). `--procdump <procdump64.exe>` запускает
только заданный UI под отладчиком в той же Job; dump лежит внутри сессии.
Gateway сохраняет исходный Windows exit status в `ui-exit.json`/`server-exit.json`.

Звуковая сцена и дополнительные проверки — [PlaybackQA / playback](Playback.md).
Она использует тот же контроллер/registry; `--long` добавляет длинный участок
для ручной проверки паузы, `CheckPlaybackQA.py` ограничивает вспомогательные
интеграционные и CPU-процессы отдельным Job Object.

Проверочные серверы и native UI запускаются одной ограниченной сессией из `src`:

```powershell
& 'E:/ComfyUI_windows_portable/python_embeded/python.exe' -s -B Verification/WindowMenusQA.py --ui .local/studio-build/bin/TrueMoon.Tales.Studio/debug/TrueMoon.Tales.Studio.exe
```

Без `--ui` создаётся только изолированный сервер. Максимальное время всей сессии,
включая подготовку, — 900 секунд, старта сервера — 45 секунд. При необходимости
задать положительные `--seconds` и `--startup-seconds`; бессрочного режима нет.
Настоящее окно требует интерактивного рабочего стола: запуск в ограниченном
неинтерактивном окружении не подтверждает отображение UI.

Скрипт печатает ID, manifest и свободный loopback endpoint. Baseline, рабочая копия,
логи и `session.json` находятся в `src/.local/qa-sessions/<id>/`. Создаются два
тестовых клипа, GPU и авторские проекты не используются. Сохранённые QA-артефакты
не удаляются при остановке. Старые отдельные `.pid` не подтверждают владение.

```powershell
& 'E:/ComfyUI_windows_portable/python_embeded/python.exe' -s -B Verification/WindowMenusQA.py --status
& 'E:/ComfyUI_windows_portable/python_embeded/python.exe' -s -B Verification/WindowMenusQA.py --stop <id>
```

По завершении задачи остановить **свои** сессии по ID и дождаться завершения
tool sessions. `--stop-all` останавливает все зарегистрированные сессии; применять
только при явном запросе на общую остановку, учитывая параллельную работу агентов.
Серверы, запущенные раньше напрямую через `app.py` или прежние QA-скрипты,
в реестр не попадают: сначала отдельно сверить путь, команду и время создания.
Нельзя останавливать процессы по одному имени `python` или номеру PID.

## Владение и завершение

[Контроллер](Verification/QASession.py) регистрирует PID, точное время создания,
exe, запускающую оболочку, argv, команды, endpoint и состояние сессии.
[Windows Job Object](Verification/QAProcesses.py) объединяет сервер, окно и их
потомков, включая FFmpeg. Handle не наследуется. Флаг
[`JOB_OBJECT_LIMIT_KILL_ON_JOB_CLOSE`](https://learn.microsoft.com/en-us/windows/win32/api/winnt/ns-winnt-jobobject_basic_limit_information)
завершает группу даже при аварийном выходе контроллера. До включения в группу
gateway ждёт токен через stdin; EOF или ошибка назначения не запускают работу.
Если Job Object недоступен, запуск завершается с ошибкой без обхода защиты.

Нормальное закрытие окна, выход сервера/запускающей оболочки, ошибка старта,
таймаут, Ctrl+C и `--stop` завершают всю сессию. В `finally` контроллер останавливает
группу, проверяет нулевое число активных процессов, дожидается gateway и только
затем записывает `stopped`. Остановка по таймауту/команде принудительная; для
сохранения тестового черновика сначала закрыть окно штатной командой UI.

`--stop` сначала посылает запрос контроллеру. Если он не ответил за 7 секунд,
процесс проверяется по exe и времени создания **на том же handle**, через который
будет остановлен; несовпадение исключает остановку. Завершение контроллера
закрывает его Job Object. После аварии manifest может остаться `running`:
`--status` показывает `inactive`, если владелец больше не совпадает/не существует.
Записи не используются для остановки внешнего API/ComfyUI. Контроллер Windows QA
не меняет жизненный цикл обычного пользовательского приложения.

## Проверка 07.11.2026

Остановлены прежние Python-серверы `53652`, `37200` и native окно `33492` после
проверки exe/команды/времени создания. Финальная проверка процессов выполняется
отдельно от проверки registry.

```powershell
& 'E:/ComfyUI_windows_portable/python_embeded/python.exe' -s -B Verification/test_qa_session.py
```

10 проверок проходят; используется стандартный `unittest`, реальные короткие
CPU-процессы и отдельные QA-копии. Тесты native окно не открывают.

| Требование | Проверка `QASessionTests` |
|---|---|
| Очистка потомков с сохранением постороннего процесса | `test_job_close_kills_descendants_preserves_unrelated_process` |
| Авария контроллера | `test_supervisor_crash_kills_descendants` |
| EOF до назначения владельца | `test_eof_before_assignment_does_not_start_work` |
| Ошибка включения в группу | `test_assignment_failure_stops_worker_before_work` |
| Устаревший PID/несовпадающий exe | `test_stale_identity_cannot_stop_another_process` |
| Таймаут подготовки и работающего сервера | `test_startup_timeout_stops_server_worker`, `test_runtime_timeout_stops_ready_server` |
| Ошибка запуска UI очищает HTTP-сервер | `test_ui_start_failure_stops_http_server` |
| Выход запускающей оболочки | `test_launcher_exit_stops_session` |
| Явная остановка сессии | `test_stop_request_stops_registered_session` |

Дополнительно запущена настоящая Debug-сборка на интерактивном рабочем столе:
сессия `0dbcac798e224defa7a1655c728a9bc3`, endpoint `127.0.0.1:59191`.
Окно `Argentis QA · Tales Studio` закрылось штатно, затем сессия завершилась
с кодом 0, `state=stopped`, `reason=ui-exit`. Проверена также явная остановка
сессии с native процессом (`d04998666c6142bfb8e21953e3314b31`).
Отмена tool session через Ctrl+C (`e4fc8f4661e84487bc6e2db7f3c1ffd5`)
прервала контроллер без записи `stopped`; Windows всё равно остановила его
процессы, registry показал `inactive`. Финальная независимая сверка WMI не
обнаружила Python/Studio/FFmpeg; порты 59260, 51762, 54344, 59191 и 58550
больше не слушают. Прежние tool sessions и новые проверочные запуски завершены.
Изменений production C# и повторной .NET-сборки в этом проходе нет.
