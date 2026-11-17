# STUDIO-UIQA1 · CPU raster и тестовый host

Проверено 17.11.2026 UTC. Studio **1.2611.16.3-alpha**, TrueMoon
**1.2611.16.1-alpha**, SDK **11.0.100**, net11.0, runtime-async=on; Windows x64,
SkiaSharp **4.156.1**. Изменены только тесты, тестовые assets и документация;
production-входы предыдущего backend-этапа совпали по всем 56 SHA. Версия сохранена
по правилу AGENTS. Коммит/push этим этапом не выполнялись.

## Что реализовано

[RasterWindow](../TrueMoon.Tales.Studio.Tests/Support/RasterWindow.cs) создаёт
UiSession с SkiaRasterRenderBackend/RasterUiTarget, подключает Window.AttachHost
и публикует состояние через Synchronize. Один owner thread обслуживает layout,
routed input, frame participants и завершения async close. Тестовый контроллер
поддерживает логический размер/scale, focus, minimize/restore, состояние maximize,
Application-close с последовательными callbacks, veto, ошибкой и отменой ожидания.
Принятый close завершает Task после освобождения session/tree; явный Dispose
отменяет незавершённый close. Failed startup также освобождает принятое дерево.

SceneView и ProjectWindow создаются через production Cobalt/IFactory и стили.
App-граф не запускается: native presentation не создаётся. Scene использует
существующие FakeStudio/FakeFrames; project window — управляемый project reader
и синтетический ProjectStore fixture. Обновления применяются на owner thread,
ожидание готовности ограничено дедлайном. Нет фонового UI-потока или sleep для
угадывания готовности.

Шрифты Noto Sans и Noto Sans Symbols включены только в тестовый проект с OFL1.1,
[upstream revision, SHA и лицензией](../TrueMoon.Tales.Studio.Tests/Assets/Fonts/README.md).
Перед загрузкой проверяются SHA; системные шрифты/fallback не используются.
Manifest фиксирует SHA фактически загруженной libSkiaSharp.dll:
`7CAD4A4AA72DFEC19F05C686088BCF38D6585C984E19C4884985FF4C03CA8FCE`.

## Матрица требований и доказательств

Полные имена ниже имеют префикс `TrueMoon.Tales.Studio.Tests.Raster.`.

| Требование | Тест | Проверяемый результат |
|---|---|---|
| Черновик и отмена | `RasterSceneTests.Drag_RendersDraft_ThenCancellationRestoresPixelsAndDataWithoutWriting` (5 cases) | 1×/1.5×/2× Escape; отдельно focus и resize; grip меняет пиксели при drag, возвращается при отмене без resize; capture освобождён, POST нет, revision=5, исходный Clip неизменен |
| Одна запись и конфликт | `RasterSceneTests.Drag_ReleaseCommitsOnceOrKeepsConflictDraft_WithRealRaster` (4) | 3 scale для success, 1 conflict; ровно один POST, payload только id/start_frame, expected_revision=5; success даёт revision=6, conflict сохраняет draft и запрещает save; повторный frame не отправляет команду |
| Состояния окна проекта | `RasterProjectWindowTests.BackendView_RendersLoadingLoadedFailureAndReload_UsingPinnedFonts` (3) | loading/disabled, revision11, routed reload, previous snapshot после ошибки без retry, revision12 после перечитывания; resize1000×720→760×600; точные исходные JSON-байты/пустая history |
| Resize/focus/suspend | `RasterHostTests.Host_PublishesResizeFocusAndSuspension_AndRejectsInvalidResize` | Физический viewport600×360 при logical400×240/scale1.5; invalid scale не меняет состояние, minimize даёт empty viewport, restore/maximize публикуют состояние |
| Async close/veto | `RasterHostTests.Close_AwaitsEachCallback_VetoesThenRetries_AndDisposesBeforeCompletion` | Порядок callbacks на owner, veto без dispose, повторная попытка закрывает и освобождает tree/session |
| Close error/cancel | `RasterHostTests.Close_CallbackFailureKeepsHostAlive_AndCancellationOnlyCancelsWait` | Ошибка наблюдаема, host жив; отмена одного waiter сохраняет общую попытку, второй получает успешное закрытие |
| Явный dispose/evidence | `RasterHostTests.Dispose_CancelsPendingClose_AndRejectsFurtherInput` | Pending close отменён, input/resize после disposal запрещены; manifest подтверждает cleanup либо временный каталог удалён |
| Ошибка старта | `RasterHostTests.StartupFailure_DisposesAdoptedTreeAndDisconnectsHost` | Throwing host notification не оставляет принятое дерево или host connection |
| Шрифты/PNG | `RasterHostTests.Capture_RejectsPathTraversalAndOverwrite_AndFontsContainCyrillic` | В шрифте есть все глифы контрольной русской строки; имя не выходит из evidence-root, существующий PNG не перезаписан |

PNG декодируются с точными размерами. В ProjectTitle отдельно проверяется
наличие контрастных растеризованных глифов внутри геометрии текста. Для loaded
и результата drag повторный кадр того же состояния сравнивается побайтово.
Это проверка детерминизма внутри прогона: принятых межверсионных golden images
и автоматической visual-regression приёмки здесь ещё нет. Обычный тест никогда
не обновляет эталон и не перезаписывает существующий кадр.

## Результаты и evidence

Финальный каталог: `src/.local/uiqa1-37dd9c8530e64c91aeb7e30b93140bf3/`.

| Проверка | Результат |
|---|---|
| Debug build, non-incremental | 0 errors, 20 прежних generated warnings Enerit |
| Release build, non-incremental | 0 errors, те же 20 warnings: 15 CS1591, 5 CS8669 |
| `full-debug.trx` | 304 passed, 0 failed, 5 skipped; raster18/18 |
| `full-release.trx` | 304 passed, 0 failed, 5 skipped; raster18/18 |
| `host-temp-cleanup.trx`, Release без evidence env | 6 passed, 0 failed; default temp cleanup проверен |

Пять прежних opt-in/platform skips: native smoke, ffmpeg/media, Python HTTP,
копия внешнего проекта и symlink policy. Их прежние результаты не объявляются
повторно проверенными. Существующие process/IPC backend tests вошли в полный
прогон; они не соединены с новым raster-host одним сценарием.

`frames-debug/` и `frames-release/` содержат уникальный каталог на каждый fixture:
PNG и `manifest.json` (версии/runtime, viewport/scale, hashes, state, routed input,
root/session disposed). `acceptance.json` сводит TRX, frame manifests, source SHA
и результаты сравнения повторяемых кадров Debug/Release. Binlog каждого
build/test сохранён отдельно в `src/.local/uiqa1-*.binlog`.

Просмотрены реальные CPU PNG окна проекта loaded/error и сцены before/draft
при 1× и 1.5×. Русские названия и Ё отображаются. Некоторые стрелки/треугольники
в scene-кнопках показывают tofu: Noto Sans не покрывает эти символы, а catalog
не выполняет автоматический fallback по codepoint. Алиас Segoe UI Symbol не
меняет тексты, использующие default family. Длинный текст inspector также
обрезается его существующей шириной. Это наблюдения тестового изображения;
полная визуальная приёмка и исправления typography/layout этим этапом не заявлены.

Промежуточные запуски выявили две ошибки самого стенда: неверное предположение
о пути native DLL и избыточный порог числа цветов для простого текста. Путь
берётся у загруженного модуля; порог заменён содержательными проверками
геометрии/глифов/изменения пикселей. После исправлений выполнены полные D/R выше.

## Запуск

Из `src`, после restore по [README](../README.md). Для сохранения PNG задайте
свежий evidence-root; каждый fixture всё равно выделяет уникальный подкаталог.

```powershell
$run = Join-Path (Get-Location) ('.local/ui-raster-' + [guid]::NewGuid().ToString('N'))
$previous = $env:STUDIO_RASTER_EVIDENCE
try {
    $env:STUDIO_RASTER_EVIDENCE = Join-Path $run 'frames'
    dotnet test TrueMoon.Tales.Studio.Tests/TrueMoon.Tales.Studio.Tests.csproj --no-restore --configuration Debug --artifacts-path .local/studio-build --filter 'FullyQualifiedName~TrueMoon.Tales.Studio.Tests.Raster' '-bl:.local/uiqa1-raster-{}.binlog' --logger 'trx;LogFileName=raster.trx' --results-directory $run
    if ($LASTEXITCODE -ne 0) { throw 'Raster tests failed' }
} finally {
    $env:STUDIO_RASTER_EVIDENCE = $previous
}
```

Без переменной PNG пишутся в собственный уникальный OS temp-каталог и удаляются
в Dispose/failure cleanup. При retention файлы остаются и при падении assertions,
их не требуется пересоздавать для диагностики. Для полного прогона убрать filter;
финальные D/R выполнялись после `dotnet build --no-incremental` с теми же
configuration/artifacts-path и отдельным binlog, затем test `--no-build`.

## Границы и следующий этап

Новых native окон/QA-сессий, HTTP-сервера, ffmpeg/audio или GPU/ComfyUI не
запускали. Full regression создаёт только свои ограниченные backend child
процессы; процессные tests завершают их. Авторские проекты/медиа и TrueMoon-код
не менялись. Синтетический UiInput(Escape) не доказывает физический Escape,
OS capture, Windows DPI, IME, OpenGL/present или соответствие CPU MP4.

Далее — STUDIO-BACKEND2: одна команда переноса через общий сервис с
expected_revision, согласованной передачей writer и изолированной schema2-копией.
Подключить raster-host к настоящему Enerit/Mithril/ProjectStore отдельным
сквозным сценарием. Long timeline, native/physical и эталоны остаются открытыми.
