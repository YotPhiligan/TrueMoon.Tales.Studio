# Shift+F10 и drag/trim таймлайна

Продолжение: [непрерывный просмотр и звуковая волна](Playback.md).

07.11.2026. Продолжение [окна и меню](WindowMenus.md); TrueMoon `0.2.5.1-alpha`.
Python HTTP API остаётся владельцем записи проекта.

## Поведение

- Shift+F10 выбирает меню строки дубля/клипа или рабочей области по элементу
  в фокусе. Команды совпадают с правым щелчком; Escape возвращает фокус тому
  же контролу. Открытие меню не выбирает дубль.
- Drag по линейке или телу выбранного клипа перемещает курсор. Декодируется
  один кадр одновременно, затем последний запрошенный; устаревший результат
  не показывается. Повтор показанного кадра не запускает новый FFmpeg.
  Записей проекта нет.
- Цветные края — области trim. Инспектор и полоса показывают черновик.
  Левая граница меняет вход и положение, сохраняя правый край; правая меняет
  исключающий выход. Участок можно расширять в пределах источника и сегмента.
- Границы привязаны к кадрам проекта и учитывают скорость. Длина — минимум
  один кадр. Расширение ограничено соседями, включая клипы без видео;
  закреплённое начало не перемещается. Сервер подтверждает сценарную раскладку.
- Отпускание отправляет одну команду с `expected_revision`: левая граница —
  существующий `update_clip`, правая — `edit_clip_source`. Повтор PointerUp
  и жест без изменений не записывают проект. Запись не ждёт декодирования.
- Escape, потеря фокуса/захвата, resize или закрытие отменяют текущий trim
  без записи. Ручной черновик сначала нужно сохранить/отменить: drag не
  записывает его попутно. Во время жеста shortcuts сохранения/смены клипа
  блокируются. Ручная обрезка сохраняет прежние кнопки.
- Ошибка или конфликт сохраняют черновик и требуют сверки ревизии;
  автоматического повтора записи нет. Код движка и Python API не менялись.

## Автоматические проверки

В решение зарегистрирован [тестовый проект](TrueMoon.Tales.Studio.Tests/TrueMoon.Tales.Studio.Tests.csproj)
с закреплёнными в Studio версиями xUnit/Test SDK. `IFrameDecoder` позволяет
управлять завершением кадров без FFmpeg. Input проходит через настоящий
`UiSession` с headless surface на одном UI-потоке.

После restore из [README](README.md), из `src`:

```powershell
dotnet test TrueMoon.Tales.Studio.slnx --no-restore --artifacts-path .local/studio-build --configuration Debug '-bl:.local/drag-trim-tests-debug-{}.binlog'
dotnet test TrueMoon.Tales.Studio.slnx --no-restore --artifacts-path .local/studio-build --configuration Release '-bl:.local/drag-trim-tests-release-{}.binlog'
```

Обе конфигурации: **23/23**, без предупреждений и ошибок. Binlog и сборки —
в `src/.local/`. Проверен запуск через решение, а не только отдельный проект.

| Требование | Проверки |
|---|---|
| Меню строки и возврат фокуса | `ShiftF10_UsesFocusedRow_AndEscapeRestoresSameFocus`, `ShiftF10_OnTimelineRestoresTimelineFocus` |
| Одна запись, ревизия и повторное открытие | `Trim_ReleaseWritesOnce_WithRevision_AndReopensSavedBounds` |
| Отмена, resize, недопустимый ввод, ручной черновик | `Trim_CancelRestoresBounds_WithoutWriting`, `Trim_InterruptedGeometryOrInvalidReleaseCancelsWithoutWriting`, `Trim_PreservesAnExistingManualDraft` |
| Захват вне полосы и отсутствие лишних записей | `Trim_OutsideReleaseClampsToOneFrame_AndWritesOnce`, `Trim_UnchangedGestureDoesNotWrite` |
| Конфликт/ошибка без повтора | `Trim_WriteFailureKeepsDraft_AndDoesNotRetry` |
| Последний кадр и объединение движений | `Scrub_CoalescesMoves_AndPresentsFinalFrameWithoutWriting` |
| Скорость, ёмкость, соседи, закрепление, дробный выход | `LeftTrim_MapsSpeedAndKeepsRightEdge`, `LeftTrim_RespectsPreviousPlaceholderAndAnchor`, `RightTrim_ClampsDurationToMediaCapacity`, `RightTrim_RespectsNextClipAndPartialOriginalEnd` |

## Настоящее окно и CPU MP4

Debug QA-сессия `61f289bbbb5f4e5d967497b361e9a261`, порт 61161:

1. Правый щелчок по дублю B, Escape, Shift+F10 — команды самого дубля у строки;
   Enter показал TAKE B. Drag курсора вернул TAKE A / FRAME 06. Полный ответ
   API остался побайтно прежним, ревизия 5.
2. Правый край: `[0,8) → [0,6)`, ревизия 6, показан FRAME 05.
3. Левый край: источник `[2,6)`, положение `[2,6)`, ревизия 7, FRAME 02.
4. Правый край отпущен над предпросмотром вне полосы: расширение ограничилось
   `[2,12)`, 10 кадров, ревизия 8, FRAME 11. После QA-02 повторно открыт QA-01:
   участок `[2,12)` и вход FRAME 02 сохранились.

POST ровно три. Второй клип, сегменты, исходники и assets совпали с baseline;
история 5/6/7 сохранена, исходный снимок совпал с baseline. SHA-256 двух
исходных MP4 совпали, Jobs отсутствуют. Повторяемая сверка:

```powershell
& 'E:/ComfyUI_windows_portable/python_embeded/python.exe' -s -B Verification/CheckTimelineDragQA.py .local/qa-sessions/61f289bbbb5f4e5d967497b361e9a261
```

Результат — `drag-trim-checks.json` в каталоге сессии. Штатный CPU-экспорт этой
ревизии создал 24 кадра при 24 fps. Сопоставлены все 16 кадров занятых участков
по видимому FRAME: выходные 2–11 соответствуют источнику 2–11, 12–17 —
источнику 0–5 второго клипа. Остальные кадры чёрные; звуковая дорожка этой
беззвучной сцены имеет нулевой пик. Проект остался побайтно прежним.
MP4 и `cpu-mp4-checks.json` сохранены в QA-копии; код экспорта не менялся.

Последняя Release-сборка проверена на свежей сессии
`1a2884f0881642e6a5fdd4683060cfe0`, порт 56978: курсор FRAME 06,
левая граница `[0,8) → [2,8)`, одна запись, ревизия 6, FRAME 02.
Обе сессии закрылись вместе с окнами (`stopped/ui-exit`, код 0).

## Границы

Native-проверка — 1280×820, CFR 24 fps. Escape/потеря захвата, resize и отказы
записи проверены routed input/управляемым HTTP handler; аппаратная отмена
посреди удержания мыши и полная DPI-матрица не проверялись. Скорости и дробные
границы проверены математическими тестами; CPU MP4 здесь проверен при 1× без
речи, переходов и титров. Непрерывное воспроизведение, звуковая волна, перемещение
целого клипа, длинная шкала и измерение задержки остаются следующими этапами.
Новых ограничений TrueMoon, требующих заявки, для этого среза не обнаружено.
