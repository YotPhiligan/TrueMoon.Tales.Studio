# STUDIO-BACKEND2 · перенос через Enerit и единственный writer

Проверено **17.11.2026 UTC**. Studio **1.2611.17.1-alpha**; предыдущая рабочая
версия 1.2611.16.3-alpha, актуальная main на старте —
`3fb891a0a905b531ad9b5af9d651f8bb83d0c6d6` / 1.2611.16.2-alpha.
Новая UTC-дата начинает rev=1; поколение и alpha сохранены. Общая Version обновлена
до финальных сборок/тестов/publish. SDK11.0.100/net11.0/runtime-async=on,
TrueMoon **1.2611.16.1-alpha** и SkiaSharp4.156.1 сохранены.

## Реализация и владение

`IStudioBackend` использует **protocol2**, ProjectContract остаётся v1, storage —
schema1/2. Новые вызовы: AcquireWriter, MoveClip, MigrateProject, RecoverProject.
Каждый требует launch capability; requests не выбирают путь и не выдают себе
права. Parent по-прежнему проверяет build/роль/протокол/parent и serving owned PID.
Read-only `--project` / `--inspect-project` не получают write grants автоматически.

Writer включают только новые явные launcher-команды и доверенные
`project_write_roots`. Каталог должен находиться в разрешённом amv/projects root;
protected/disabled legacy/reparse deny сохраняются. Для записи нужны права на
сам каталог проекта: JSON-only grant не разрешает lock/history/journal.
Политика сервиса устанавливается при запуске; изменения config требуют перезапуска.

[ProjectWriterLease](../TrueMoon.Tales.Studio.Persistence/ProjectWriterLease.cs)
удерживает `.Studio.project.writer.lock` до завершения backend-сессии. FileShare.None
и byte-range Lock совместимы с Python msvcrt.locking/POSIX lockf. Lock-файл остаётся
на месте; наличие файла/PID не является владением. Python `Store.commit` удерживает
тот же lease на время проверки и записи. Второй backend/Python writer получает
отказ; OS освобождает lease и при аварии. Shutdown дожидается завершения команды
перед освобождением lease. Внутри backend команды сериализует SemaphoreSlim.
Это протокол сотрудничающих writers приложения, не запрет ручного внешнего IO.
Низкоуровневые ProjectStore API по-прежнему требуют владения со стороны их host.

Перед claim/migrate/move сверяются **expected_revision и SHA точных байтов**.
Claim сам не мигрирует; schema1 не принимает Move. Явный Migrate сохраняет точный
оригинал в истории и legacy_document, повышая ревизию один раз. После schema2
Python get/commit возвращают 409 даже после остановки backend: старый Python
черновик не возвращает прежний writer. Автоматического downgrade нет.

Move передаёт clip ID и запрошенный start. Сервер рассчитывает раскладку тем же
TimelineMove, что рисует drag-черновик: границы/пересечения, anchors, порядок и gaps.
При активном сценарии одновременно меняются порядок Actions и StartFrame клипов;
остальные поля/источники/окна не меняются. Неоднозначная проекция многих клипов
одного действия либо конфликтный сценарий запрещают такой reorder.
Для чистого schema2 без legacy_document режим сценария задаёт boolean
`project.properties.scenario_active`; по умолчанию монтаж свободный.

Изменённый draft сохраняется один раз через ProjectStore.SaveAsync с actor
`studio:enerit`. No-op не повышает ревизию и не создаёт историю. Save сохраняет
atomic replacement, exact SHA, immutable history и durable intent предыдущего
этапа. Recovery выполняется только явным вызовом и завершает валидированный
журнал; повторный Recover не повторяет move/revision/history.

BackendConnection не повторяет команды при timeout/разрыве. Ошибки Enerit
нормализуются в наблюдаемую IO-ошибку; UI сохраняет stale draft и требует
Cancel → Reload. Только readiness чтения имеет ограниченное ожидание.
Два одновременных Move с одним baseline дают один commit и один revision_conflict.

## UI и границы совместимости

Общий ISceneService выделен IDE extract_interface (одна декларация); изменение
конструктора SceneViewModel затронуло 4 файла/3 call sites. HTTP остаётся его
реализацией для schema1. BackendSceneService подключает тот же Cobalt/IFactory
SceneView в move-only режиме: drag/Escape/Cancel/Reload, без декодирования видео,
аудио, trim и export. Кадровый extent для UI берётся из сохранённого использования,
он не объявляется результатом probe медиа. Текущий scene UI ограничен int revision/
source extent; неподдерживаемые значения отклоняются перед записью.

Python HTTP/CLI пока не адаптированы к schema2 и общему backend-сервису: после
передачи они отклоняют проект. Поэтому проверять переход на копии. Полный media
resolver/aliases, preview/export и HTTP-адаптер тех же команд — следующие этапы.
PNG не доказывают CPU MP4 parity; перенос source in/out/speed/take/windows
проверяется контрактными инвариантами. Native/physical Escape, DPI и GPU не входят
в приёмку. Артефакты CPU raster сохраняют прежние ограничения тестовых шрифтов.

## Матрица требований и проверки

Префикс классов, кроме двух raster-строк: `TrueMoon.Tales.Studio.Tests.Backend.`.

| Требование | Точный тест / доказательство |
|---|---|
| Перенос с expected_revision, одна запись/no-op, exact history/opaque data | `BackendWriteTests.Handoff_IsExplicit_MigrationAndMovePreserveExactHistory_AndNoopDoesNotWrite` |
| Stale revision и те же revision/другие байты; неверный ID/границы | `BackendWriteTests.Move_RejectsStaleOrInvalidRequestsWithoutWriting` (5 cases) |
| Единственный writer/read-only отказ | `BackendWriteTests.Writers_AreExclusive_UntilOwnerDisposes_AndReadOnlyCannotClaim` |
| Неверный claim, права и capability | `BackendWriteTests.Acquire_RejectsStaleBaselineAndMissingGrants_AndDoesNotKeepFailedClaim` |
| Явный Recover до/после replacement, без повторения | `BackendWriteTests.Recovery_IsExplicit_AndFinishesDurableIntentOnceWithoutReplayingMove` (2) |
| Границы и сохранность остальных данных | `BackendMoveTests.PlainMove_UsesExactWindowBounds_AndPreservesOtherData` (5) |
| Scenario order, gaps и материал | `BackendMoveTests.Scenario_ReordersActionsAndReflowsClips_PreservingGapMaterialAndOtherWindow` |
| Anchor/overflow/inconsistent/overlap | `BackendMoveTests.InvalidLayouts_DoNotProduceAProjectDraft` (4) |
| Только явное включение writer | `BackendWriteOptionsTests.Launcher_EnablesWritesOnlyForExplicitCommands` (5) |
| Настоящая CLI-миграция и её no-op повтор | `BackendWriteProcessTests.Migrate_CliUsesOwnedBackend_AndRepeatedExplicitMigrationDoesNotWriteAgain` |
| Параллельные IPC-команды | `BackendWriteProcessTests.ConcurrentIpcMoves_WithSameBaseline_CommitOnlyOneAndReturnConflictForTheOther` |
| Авария owned backend освобождает lease | `BackendWriteProcessTests.BackendCrash_ReleasesOnlyItsWriter_WithoutAutomaticRestartOrMigration` |
| Python ↔ backend и schema2 barrier | `BackendWriteProcessTests.PythonAndBackend_ExcludeEachOther_AndSchema2RejectsOldPythonDraftAfterRelease` |
| Сквозной raster/IPC: Escape, один commit или конфликт, cleanup | `TrueMoon.Tales.Studio.Tests.Raster.BackendRasterIpcTests.Drag_RealMithrilEneritStore_CancelsWithoutWriting_ThenCommitsOnceOrRetainsConflict` (scale1/1.5/2 success, scale1 conflict) |
| Реальный commit и потеря ответа без replay | `TrueMoon.Tales.Studio.Tests.Raster.BackendLostReplyTests.LostReply_AfterRealCommit_KeepsDraftAndDoesNotReplay_UntilExplicitReload` |

34 новых cases. Полные **Debug/Release: каждый 338 passed, 0 failed, 5 прежних
skipped**. Новый Python interop case включён явным STUDIO_TEST_PYTHON, он прошёл.
Старые opt-in skips: внешняя копия, HTTP, ffmpeg, native и symlink permission.
Полный Python regression: **288 tests, OK**. Published Release EXE:
**16 passed, 0 failed, 0 skipped** (старый lifecycle7 и новые process/raster9).

Non-incremental builds: **0 errors, 24 generated warnings Enerit** (19 CS1591,
5 CS8669). На четыре новых RPC-метода приходится ещё четыре CS1591 прежнего
генератора. Они не подавлены; XML docs покрыты существующей
[задачей ENE6e](../../../TrueMoon/src/docs/enerit/PLAN.md#ene6e-generated-xml-docs).
Новых предупреждений в handwritten-коде нет.

## Запуск

После restore/build по [README](../README.md), из `src`:

```powershell
# Только явно выбранная изолированная копия с directory grant в доверенном config.
./.local/studio-build/bin/TrueMoon.Tales.Studio/release/TrueMoon.Tales.Studio.exe --migrate-project <copy> --config <config.json>
./.local/studio-build/bin/TrueMoon.Tales.Studio/release/TrueMoon.Tales.Studio.exe --edit-project <copy> --config <config.json>
# Только когда чтение вернуло recovery_required:
./.local/studio-build/bin/TrueMoon.Tales.Studio/release/TrueMoon.Tales.Studio.exe --recover-project <copy> --config <config.json>
```

Проверки не требуют native окна. Пример целевого прогона:

```powershell
$priorPython = $env:STUDIO_TEST_PYTHON
$priorRaster = $env:STUDIO_RASTER_EVIDENCE
try {
    $run = Join-Path (Get-Location) ('.local/backend2-' + [guid]::NewGuid().ToString('N'))
    $env:STUDIO_TEST_PYTHON = 'E:/ComfyUI_windows_portable/python_embeded/python.exe'
    $env:STUDIO_RASTER_EVIDENCE = Join-Path $run 'frames'
    dotnet test TrueMoon.Tales.Studio.Tests/TrueMoon.Tales.Studio.Tests.csproj --no-restore --configuration Debug --artifacts-path .local/studio-build --filter 'FullyQualifiedName~BackendWrite|FullyQualifiedName~BackendMoveTests|FullyQualifiedName~BackendRasterIpcTests|FullyQualifiedName~BackendLostReplyTests' '-bl:.local/backend2-target-{}.binlog' --logger 'trx;LogFileName=backend2.trx' --results-directory $run
    if ($LASTEXITCODE -ne 0) { throw 'Backend2 tests failed' }
} finally {
    $env:STUDIO_TEST_PYTHON = $priorPython
    $env:STUDIO_RASTER_EVIDENCE = $priorRaster
}
```

Выбрать существующий Python executable этой машины; при отсутствии переменной
только межъязыковой case становится opt-in skipped. Для всех остальных checks
Python-сервер/ComfyUI не нужны. STUDIO_BACKEND_EXE позволяет повторить процессные
cases на published EXE вместо build-tree consumer. Во всех fixture путь задаётся
доверенным launcher, тесты используют свежие уникальные temp-проекты.

## Evidence и завершение

Финальный каталог:
`src/.local/backend2-final-e0a77aa165534e9e8b8df3e1bf7132d4/`:
full-debug.trx, full-release.trx, published.trx, build/test/publish logs,
frames-debug/release/publish, publish, acceptance.json. Binlog каждого MSBuild
запуска — отдельный `src/.local/backend2-*.binlog`; полный Python log —
`src/.local/backend2-python.log`. Manifest каждого raster-fixture сохраняет
версии, font/native SHA, inputs, frame state и root/session disposal.
Среда проверки — Windows x64. Совпали 75 кадров Debug/Release и 24 кадра
published consumer с Release; extended native проверен по фактическому пути
`publish/truemoon-native/win-x64/libSkiaSharp.dll`. `cleanup.json` фиксирует
0 оставшихся backend-процессов из build/publish и 0 Python writer helpers.
PNG result/conflict просмотрены: реальная CPU-растеризация кириллицы и состояния
команд. Межверсионные golden/native present этим не объявляются проверенными.

Все UI fixtures освобождены до async Stop; управляемые backend и Python helpers
остановлены, аварийный тест завершает только свой подтверждённый child process.
Новых native QA-сессий, GPU/ComfyUI и авторских данных не использовали. TrueMoon
не изменялся и не пересобирался. Коммит/push этим этапом не выполнялись.
