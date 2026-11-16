# Перенос целого клипа внутри сегмента

16.11.2026, Studio **1.2611.16.2-alpha**, TrueMoon **1.2611.16.1-alpha**,
SDK 11.0.100 / net11.0 / runtime-async=on. Продолжение [drag/trim](../TimelineDrag.md).

## Поведение

- Линейка перемещает курсор. Короткий щелчок по телу клипа также выполняет seek.
  Drag тела начинается с порога 4 пикселя; края сохраняют прежний trim.
- Во время drag меняется только черновик: HTTP-записей и декодирования новых
  кадров нет. Полоса показывает выбранный клип и изменившиеся позиции соседей;
  source in/out, speed, take и ID остаются прежними.
- В обычном timeline начало ограничено окном сегмента, пересечения запрещены,
  касание границ допустимо. В активном сценарии точка отпускания выбирает место
  в порядке использований; сохраняются gap_before_frames и закрепления, после
  чего рассчитывается последовательная раскладка. Поэтому request start и
  фактическое начало могут различаться. Пример: A[0,8), B[8,12) → запрос 9
  даёт B[0,4), A[4,12), без изменения окна.
- Неизвестные времена, несовпадение сценария с сохранённой проекцией,
  закреплённый выбранный клип, незавершённая ручная обрезка или просмотр
  альтернативного дубля блокируют начало переноса. Заглушки без видео учитываются.
  Недопустимый preview помечается ошибкой; отпускание восстанавливает baseline
  без записи. Возврат к исходному месту/порядку также не пишет проект.
- При отпускании отправляется один существующий update_clip:
  payload содержит только id и start_frame, expected_revision берётся из
  pointer-down snapshot. Источник, дубль, длительность и скорость не отправляются
  повторно. Нового HTTP-контракта или миграции проекта нет.
- Escape, потеря фокуса/захвата, скрытие/отключение полосы, resize, invalid release
  и Dispose отменяют gesture и освобождают capture. Клавиатурные команды окна
  во время gesture блокируются существующим preview input handler.
- При 409 и другой ошибке/неопределённом ответе остаются локальный черновик и
  baseline, включается stale и запрещается повторная запись. Сначала отменить
  черновик и перечитать сервер. Автоматического retry нет; ответ после успешной
  записи сверяется с предсказанной раскладкой до применения.

## Проверки

57 новых deterministic cases: 35 для TimelineMove и 22 для gesture/model/HTTP
отказов. Новый opt-in case отдельно вызывает настоящий Python HTTP backend.

| Требование | Точная проверка |
|---|---|
| Черновик без IO, одна запись/revision, повторный PointerUp | `Move_DraftsWithoutIO_ThenWritesOnlyPositionOnceWithOriginalRevision` |
| Escape/focus/capture/resize/hidden/disabled/invalid-up/Dispose | `Move_CancellationRestoresSnapshotAndReleasesCaptureWithoutWriting` |
| Click/jitter/no-op/возврат к исходному месту | `Move_ClickJitterOrReturnToOriginDoesNotWrite` |
| 409/503/422, сохранность черновика и запрет retry | `Move_FailedWriteKeepsDraftAndBlocksRetryUntilDiscardAndReload` |
| Pending write и повреждённый ответ | `Move_PendingAndMalformedResponseNeverCauseASecondWrite` |
| Фактическая сценарная раскладка длинного клипа | `ScenarioMove_LongFirstClipCanMovePastShortLastClip_InFullWindow` |
| Сценарные паузы/закрепления/placeholders и source fields | `ScenarioMove_ReordersAndReflowsStoredGaps_PreservingAllSourceFields`, `ScenarioMove_ReflowPreservesNeighborAnchor`, `ScenarioMove_IncludesPlaceholderMissingFromVideoChoices` |
| Настоящий HTTP, точная история и конфликт другого writer | `Move_ActualScenarioMatchesDraft_OneCommitExactHistory_AndConflictDoesNotOverwrite` |

Полные non-incremental Debug/Release builds: **0 warnings / 0 errors**.
Каждый полный suite: **Passed: 247, Failed: 0, Skipped: 5**. Skips — прежние
native/device/copy/symlink проверки и новый opt-in HTTP case; последний отдельно
дал **Passed: 1, Failed: 0, Skipped: 0**. Python `test_server.py`: **10 tests, OK**,
включая согласованную общую продуктовую версию. Старые native/device результаты
не объявляются повторно проверенными этой задачей.

Из `src`:

```powershell
dotnet test TrueMoon.Tales.Studio.slnx --no-restore --artifacts-path .local/studio-build -c Debug --filter 'FullyQualifiedName~TimelineMove' '-bl:.local/clip-move-1.2611.16.2-alpha/focused-{}.binlog'
dotnet build TrueMoon.Tales.Studio.slnx --no-restore --no-incremental --artifacts-path .local/studio-build -c Release -m '-bl:.local/clip-move-1.2611.16.2-alpha/build-Release-{}.binlog'
dotnet test TrueMoon.Tales.Studio.slnx --no-build --no-restore --artifacts-path .local/studio-build -c Release --logger trx --results-directory .local/clip-move-1.2611.16.2-alpha/Release '-bl:.local/clip-move-1.2611.16.2-alpha/Release-{}.binlog'
```

Та же последовательность build/test выполнена для Debug. Версия зафиксирована до
финальных проверок: remote/local main `002a47b`, прежняя Studio 1.2611.16.1-alpha,
следующий UTC rev=2; зависимость TrueMoon и её архивы сохранены.

## Реальная HTTP/CPU приёмка

Свежая управляемая QA-сессия **b0de054c0e9b448a81352adf5dc0b437**, порт **52012**.
Тест добавил второй клип в первое окно только через API синтетического проекта:
ревизия 6 → 7 при переносе, один undo и один побайтно точный снимок истории.
Сохранены scenario items, source blocks, assets и окна; изменён только порядок
и его проекция. Отмена не меняет JSON. Внешний writer создал marker и ревизию 8;
перенос с устаревшей ревизией получил 409 и сохранил внешний JSON, после явного
discard/reload прочитана ревизия 8.

```powershell
& 'E:/ComfyUI_windows_portable/python_embeded/python.exe' -s -B Verification/WindowMenusQA.py --seconds 240
& 'E:/ComfyUI_windows_portable/python_embeded/python.exe' -s -B Verification/CheckMoveQA.py <ID>
& 'E:/ComfyUI_windows_portable/python_embeded/python.exe' -s -B Verification/CheckMoveQA.py <ID> --cpu
& 'E:/ComfyUI_windows_portable/python_embeded/python.exe' -s -B Verification/WindowMenusQA.py --stop <ID>
```

CPU MP4: 24 кадра при 24 fps, проверены все 18 кадров с материалом и 6 чёрных;
max MAE=5.8259491 при пороге 8. Подтверждены новый порядок B/A, source frames,
неизменность второй сцены, JSON проекта и SHA двух исходных MP4. Exports и FFmpeg
закрыты; QA остановлена по ID, tool session завершена, owned processes/порт отсутствуют.
`move-http.json`, `move-cpu.json`, TRX/binlog и экспорт находятся в каталоге сессии.
Общие логи — `.local/clip-move-1.2611.16.2-alpha/`.

Два начальных запуска QA остановлены после исправления самого integration test:
setup использует Content-Length JSON, а UiSession остаётся на owner thread.
Ни один из этих отказов не объявляется дефектом production или фреймворка.

## Границы

Физический drag в настоящем окне, DPI, длинная шкала и отзывчивость здесь не
принимались. Проверены routed input/layout через headless UiSession, настоящий
HTTP и CPU MP4. Межсегментный перенос и свободная независимая раскладка действия
в schema 1 не добавлены; целевая модель нескольких клипов действия остаётся
следующим этапом. Python production API, авторские проекты, ComfyUI/GPU и TrueMoon
не менялись. Исходная причина старого native AV остаётся открытой.
