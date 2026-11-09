# Native-падение: диагностика и повторные проверки

07.11.2026. Исходный `0xC0000005` **не воспроизведён**. Причина остаётся открытой;
успешные повторы не доказывают исправление исходного сбоя. Изменены диагностический
QA-контроллер и проверки, поведение приложения не исправлялось по предположению.

## Исходное свидетельство

QA `67873a7bab504dca94ca16cd2e68f265`, порт 64290, остановлена через 34 секунды.
В `ui.log` и Windows Application/.NET Runtime записан fatal-стек:

```text
0xC0000005
TrueMoon.Alloy.Hosting.HostedUiWindow.Tick()
TrueMoon.Alloy.Hosting.UiOwnerLoop.Process(Work, Boolean)
TrueMoon.Alloy.Hosting.UiOwnerLoop.Run()
```

CoreCLR 10.0.1426.52326 / .NET 10.0.14; TrueMoon 0.2.5.1-alpha, Windows x64,
Skia/OpenGL. Дополнительного адреса native exception, faulting module или дампа
исходной ошибки нет. Стек не устанавливает виновность HTTP, Cobalt, Studio,
Alloy либо видеодрайвера. Поиск конкретного условия воспроизведения остаётся задачей.

Для первых повторов использован тот же Release DLL (время записи 21:25:44), SHA256:
`1481ea3c2bbaf23d11dd7a56ae8042295f0dda5cb51d81dc6c144ee5534e7e2a`.

## Фактические повторы

| Сессия | Проверка | Результат |
|---|---|---|
| `249f8ba34e7940dc9fe94a18ba004d9d`, 58564 | Startup, захват окна, активация, FRAME 00 → 01, Alt+F4 | Штатный UI exit, код 0, ui.log пуст |
| `7322215d6e9c44a3b5a744f628097f57`, 52727 | Максимизация, восстановление, сворачивание/активация | Сбоя не было; затем сработал 180-секундный дедлайн, это принудительная очистка |
| `bf61255a37c34da39df287951c399e6b`, 49987 | ProcDump: playback до FRAME 07, popup/Escape, Alt+F4 | ProcDump записал target exit `0x00000000`, dump не создавался |
| `13deccf5005c4303b6d96737ff976eca`, 51025 | ProcDump + одноразовый startup hook, пять full/compacting GC | Все пять GC прошли, окно оставалось работоспособным; затем дедлайн |
| `ae42affeb4054bd09f4e0671a6523ea0`, 58860 | 10 App start/stop: закрытие во время загрузки и после кадра | 1 native-тест passed, все 10 окон завершены |
| `2a8d34c90fbd40fa88ee947eefae5462`, 50930 | Те же 10 циклов + 40 секунд resize/seek/GC | 1 native-тест passed за 41 с; длительный проход дал 880 представленных кадров и успешное закрытие |

Обе server-only сессии затем истекли по своим дедлайнам. Аудит всех шести сессий:
владельцы/зарегистрированные потомки отсутствуют, JSON проекта и SHA двух MP4
сохранились, POST и Jobs отсутствуют. Артефакты — `src/.local/qa-sessions/<ID>/`;
общая сверка — `.local/native-crash-dumps/session-audit.json`.

Одноразовый GC hook и его журнал находятся только в `.local/native-hook/` и
`.local/native-crash-dumps/`. Он также показал обработанные FileNotFound для
необязательных Silk SDL backends; они не были unhandled и не являются объяснением AV.
Переменные startup hook/dump применялись только к дочерним QA-процессам.

## Исправление диагностики

[QASession](Verification/QASession.py) теперь сохраняет `ui-exit.json` /
`server-exit.json` с исходным Windows status и hex-кодом перед преобразованием
выхода Python gateway. Добавлен опциональный `--procdump`: debugger, UI, сервер
и потомки остаются в одной Job. Глобальный crash handler не устанавливается.
Завершение gateway под ProcDump проверяется вместе с target exit в ui.log.

ProcDump загружен с [официальной страницы Microsoft](https://learn.microsoft.com/en-us/sysinternals/downloads/procdump),
цифровая подпись Microsoft проверена. Локальный `procdump64.exe` SHA256:
`22c2a2b09ade73a342a18dab838354385f73fc98e324b9903fd61fff949c5124`.
Используются `-ma -e -g -x`, дампы сохраняются в каталоге своей сессии.

Проверки Python lifecycle после изменений — 12/12, включая сохранение
`0xC0000005` и состав команды debugger/target. Отдельный native-тест
`Native_RepeatedStartupAndClose_ReleasesWindowDuringLoadAndAfterVideo`
опционален: обычный запуск тестов не открывает окна.

Итоговые Debug/Release: по **90 passed, 0 failed, 2 skipped**. Две опциональные
проверки (native и FFmpeg/audio) пропущены в обычном запуске; native отдельно
пройден в сессиях выше. Это не заменяет полной медиаматрицы.

| Требование | Свидетельство |
|---|---|
| Не терять Windows status | `test_worker_records_full_native_exit_status` проверяет `0xC0000005` |
| Отладчик запускает только целевой UI и пишет дампы в его сессию | `test_debugger_launch_preserves_target_arguments_and_local_dump_directory` |
| Окно закрывается при загрузке и после вывода видео | `Native_RepeatedStartupAndClose_ReleasesWindowDuringLoadAndAfterVideo`, 11 окон, 880 кадров длительного прохода |

Команды итоговых детерминированных проверок:

```powershell
& 'E:/ComfyUI_windows_portable/python_embeded/python.exe' -s -B -m unittest discover -s Verification -p test_qa_session.py
dotnet test TrueMoon.Tales.Studio.slnx --no-restore --artifacts-path .local/studio-build --configuration Debug '-bl:.local/native-final-debug-{}.binlog'
dotnet test TrueMoon.Tales.Studio.slnx --no-restore --artifacts-path .local/studio-build --configuration Release '-bl:.local/native-final-release-{}.binlog'
```

## Повторяемая проверка

Из `src`, после сборки Release и подготовки локального ProcDump:

```powershell
& 'E:/ComfyUI_windows_portable/python_embeded/python.exe' -s -B Verification/WindowMenusQA.py --seconds 300
# Пока напечатанный endpoint жив:
& 'E:/ComfyUI_windows_portable/python_embeded/python.exe' -s -B Verification/CheckNativeQA.py <ID>
& 'E:/ComfyUI_windows_portable/python_embeded/python.exe' -s -B Verification/WindowMenusQA.py --stop <ID>
```

Checker запускает выбранный xUnit/VSTest native-тест с `--blame-crash` и full dump
в отдельной Job с 90-секундным пределом. Сам тест имеет предел 65 секунд.
Сохраняет `native-lifecycle.json`, лог, binlog и dump при ошибке. Для ручного повтора:

```powershell
& 'E:/ComfyUI_windows_portable/python_embeded/python.exe' -s -B Verification/WindowMenusQA.py --ui .local/studio-build/bin/TrueMoon.Tales.Studio/release/TrueMoon.Tales.Studio.exe --procdump .local/diag-tools/procdump/procdump64.exe --seconds 300
```

Дальнейшая локализация требует конкретного повторения с native dump. Исторический
сбой остаётся открытым до установления причины и проверки соответствующего исправления.
