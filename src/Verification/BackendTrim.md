# STUDIO-BACKEND6 · обрезка клипа через общий backend

Поставка Studio **1.2611.17.6-alpha**, TrueMoon **1.2611.17.1-alpha**,
SDK11.0.100/net11.0/runtime-async=on. Предыдущий незакоммиченный срез BACKEND5
сохранён; номер нового production-набора выбран выше working17.5 и main17.4.

## Контракт и границы

Обрезка выбранного клипа проходит через тот же owned EXE/Mithril/Enerit backend,
единственный writer и ProjectStore. Protocol5 добавляет TrimClipAsync: clip ID,
source-in/source-out в кадрах проекта, абсолютный timeline start и observed
revision/SHA. Storage schema2 и ProjectContractv1 сохраняются.

Backend самостоятельно проверяет длину материала через trusted resolver и
media metadata. UI/CLI не передают файловые пути или дополнительные write grants.
Результат сохраняется одной атомарной операцией; no-op не добавляет ревизию и
историю. Conflicts/timeout не вызывают автоматический повтор команды.

UI показывает локальный черновик обоих краёв. До PointerUp записи нет;
Escape/focus loss/resize отменяют жест. При конфликте сохраняется draft и
требуется явное Cancel/Reload. Снимок после успешной записи сверяется с черновиком.

Обычный монтаж ограничен соседями и фиксированным окном. Сценарный монтаж 1:1
пересчитывается по фактическим длинам клипов с сохранением следующих authored
gaps/anchors и границ окна. Явный сдвиг начала меняет только соответствующий
допустимый gap; защищённые паузы не поглощаются. Плановая длительность действия
сохраняется отдельно и не переписывается при trim. ID действий, клипов, sources/
takes, пути, opaque properties и immutable history сохраняются.

Это не редактор M:N и не split: неоднозначные/несогласованные сценарные проекции
не редактируются этой командой. Скорость и выбранный дубль trim-командой не меняются.

HTTPv1 получает POST `/api/backend/v1/trim-clip`, Python CLI — `backend-trim`.
Они вызывают тот же backend/writer с обязательными revision/SHA, token и строгими
полями; без bootstrap, proxy, redirects и retries.

## Критерии приёмки

- Pure projection: оба края, частичный source frame, capacity/FPS/speed,
  границы окна/соседи, protected gap/anchor, scenario reflow, planned≠actual,
  no-op, invalid/ambiguous input и отсутствие мутации исходного снимка.
- Service/transport: sole writer, explicit schema2, revision/SHA до записи,
  одна history/revision, no-op без записи, строгие HTTP/CLI payload и ошибки.
- Production Cobalt SceneView/UiSession/CPU raster вместе с настоящим
  EXE/Mithril/Enerit/HTTP/CLI: draft, отмена, один commit, conflict/reload/reopen.
- На изолированной synthetic scene сравнить участок/тайминг/звук после trim
  с независимыми decode и действующим CPU DraftRenderer. Schema2 не открывается
  старым Python API: renderer получает отдельный ожидаемый fixture request.
- Очищать все принадлежащие тесту процессы, ресурсы и listeners; сохранять
  version/input/tool/probe/PNG evidence и точные границы медиапроверки.

| Требование | Evidence |
|---|---|
| «давай следующий шаг»: trim через общий backend | `RoutedBothEdges_SaveOnceEach_ReopenAndMatchExistingCpuMp4` — два routed drag, два отдельных commit, повторное открытие, play/seek и CPU MP4 |
| Draft, отмена и конфликт | `RoutedTrim_CancelAndNoopNeverWrite`, `CliCommitDuringUiTrim_RetainsConflictingDraftAndReloadsExplicitly`, `TrimDraft_ConflictOrWrongEchoRetainsDraftUntilCancelAndExplicitReload` |
| Fixed window, protected gaps/anchors, planned≠actual | `ScenarioTrim_ShowsFollowingReflowBeforeCommit_AndPreservesPlannedDuration`, `ScenarioTrim_ReflowsActualDurationsAndPreservesPlanGapsAnchorsAndOtherWindow`, `CompleteSharedActionInTwoWindows_IsReadableButRefusesTrimWithoutChangingEitherWindow` |
| Source capacity и отсутствие preview race | `Trim_FractionalSourceFpsUsesMeasuredExtentInsteadOfAssumingProjectCadence`, `BackendTrim_UsesFractionalSourceCapacity_WithoutDroppingFinalMaterial`, `Trim_WaitsForPreviewFrameAdmission_ThenProbesAndCommitsExactlyOnce`, `SuccessfulTrim_RetiresOldBaselineSeek_AndIgnoresItsLateConflict` |
| Sole writer, exact SHA, atomic commit/history/no-op | `Trim_UsesOneCommitExactHistoryAndOpaquePreservation_ThenNoopAvoidsProbeAndWrite`, `Trim_RequiresExistingSoleWriterAndExplicitMigration_WithoutDecoderForNoop`, `Trim_RejectsExactBytesChangedDuringProbe_AndReapsOwnedProbeWithoutCommit` |
| HTTP/CLI | `HttpTrim_UsesOneWriterAndObservedBaseline_CommitsOnceThenNoop_RefusesStaleAndOutOfMedia`, `RealCliTrim_UsesSameBackendAndReturnsCommittedClip_StaleRetryIsNotApplied`; Python `test_backend_trim_cli.py` |
| Plan/Status/версия | [Plan](../../Plan.md#backend-trim), [Status](../../Status.md#backend-trim), [Directory.Build.props](../Directory.Build.props): UTC17.11, rev6/alpha, выше working17.5 и main d613e87/17.4 |

Финальные Debug/Release: каждый **Passed:710, Failed:0, Skipped:5**;
published EXE: **Passed:94, Failed:0, Skipped:0**. Новые87C#cases, включая
10 raster/IPC и16 HTTP/CLI; старые5 skips относятся к отдельным copy/legacy HTTP/
native/audio-device/linked-root профилям, новые сценарии исполнялись.
Python: **Ran323 tests, OK**, exit0/45.698s, включая11 новых CLI tests.

Обе полные non-incremental сборки с EmitCompilerGeneratedFiles и
WarningsAsErrors=CS1591:0errors/9generatedCS8669 закреплённого Enerit.
Новых пакетов, suppressions или изменений TrueMoon нет.
231 зафиксированный input SHA повторно сверён после проверок: изменений0.
Финальный inventory Studio/FFmpeg/ffprobe/testhost/Python —0 процессов;
cleanup.json и acceptance-summary.json сохранены вместе с evidence.

CPU fixture: source30fps, project24fps/speed1.25; после двух обрезок clip start20,
source[14,94), duration64. MP4 содержит96 кадров/24fps (4s), H.264/AAC stereo48kHz.
Проверяются local0/20/63 (source17/48/115), black lead/tail, source hashes и PTS.
Правильный frame имеет MAE<=8 и ближе обоих соседних source frames минимум на5;
для128000 stereo sample frames AAC/PCM RMS<=.004. Это проверенные границы,
максимальные фактические MAE/RMS не сериализовались и не утверждаются численно.
Acceptance.json хранит исходные probes/SHA, версии tools, render commands,
request SHA, source PTS и output probe. Полный audio/video decode завершён успешно.
Отдельно просмотрен Release PNG после trim/seek: материал, инспектор64 кадров/
2.667s, курсор и ревизия14 отображаются в production View на scale1.5.

## Команды и evidence

Финальные результаты: `src/.local/backend6-20261117-final2/` — commands-final.ps1,
final-inputs.json/final-freeze.json, build/test/publish logs/binlogs,
full-debug.trx/full-release.trx/published.trx, frames-debug/frames-release/
frames-published и CPU Acceptance.json. Зависимости не менялись, использован
прежний закреплённый package cache; финальный запуск — без нового restore.

```powershell
# Из src. Скрипт задаёт/восстанавливает tools/EXE/evidence env и проверяет exit codes.
& "$env:SystemRoot/System32/WindowsPowerShell/v1.0/powershell.exe" -NoProfile -ExecutionPolicy Bypass -File .local/backend6-20261117-final2/commands-final.ps1 -SkipRestore
```

Промежуточные результаты сохранены в `.local/backend6-20261117/`:120/120 initial,
3/3 CPU/raster; первый полный Debug выявил три старых ожидания недоступного trim
и новый тест, вызывавший Cancel до owner Poll завершившегося seek. Исправлены
тесты; production-код после этого не менялся. Failed logs/первый input freeze
сохранены отдельно и не заменены финальной приёмкой.

## Что остаётся отдельно

Backend export endpoint/worker, полный montage из нескольких клипов, VFR,
переходы/титры, M:N authoring и split, physical input/Windows DPI/native present
и аппаратная AV-задержка. CPU fixture не является полной приёмкой авторского
монтажа или нового backend-export. Авторские проекты, GPU/ComfyUI и соседний
TrueMoon не изменяются.
