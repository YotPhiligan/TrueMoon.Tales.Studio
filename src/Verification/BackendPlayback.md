# STUDIO-BACKEND5 · непрерывное воспроизведение через backend

Поставка Studio **1.2611.17.5-alpha**, TrueMoon **1.2611.17.1-alpha**,
SDK11.0.100/net11.0/runtime-async=on. Итоги финальной приёмки приведены ниже.

## Контракт

UI использует тот же owned backend EXE/Mithril/Enerit, который обслуживает
проект, перенос и HTTP/CLI. Отдельный writer или Python-сервер не создаётся.
Enerit protocol4 добавляет OpenPlayback/ReadPlayback/ClosePlayback; HTTPv1,
storage schema2 и ProjectContractv1 сохраняются.

Запрос открытия содержит clip ID, audio/video, начальный локальный кадр и
наблюдаемые revision/SHA. Backend выбирает сохранённый take и источники звука,
разрешает пути только через trusted resolver. DTO не предоставляет права на IO.
Baseline проверяется до и после выдачи данных; конфликт останавливает просмотр
и требует явного Reload. Просмотр не изменяет JSON, историю или writer lease.

Видео — выбранный CFR-клип с source-in/out, постоянной скоростью и разными
project/source FPS; выход480×270BGRA. Один FFmpeg декодирует непрерывную
последовательность, bounded queue обеспечивает backpressure. Это не цикл
отдельных FrameAsync/FFmpeg на каждый кадр.

Звук — native, музыка и события на абсолютной монтажной сетке, с gain/mute и
выбранным audio stream. Backend потоково смешивает48kHz stereo f32le, передаёт
порции до32768bytes с sample offset и SHA. UI хранит подготовленный PCM и waveform
выбранного клипа; длина ограничена600s (до230.4MB PCM). Backend не удерживает
полную копию микса. Никакой frame/audio cache на диске не создаётся.

На backend допускается одна audio **или** video-сессия, очередь3; до64 звуковых
событий. Idle lease20s действует между Read, hard lifetime12min, probe/chunk8s,
операция10s, ожидание cleanup7s. Клиент ограничивает Open12s, video Read12s,
audio Read50s, весь Prepare55s и Close8s. При потере ответа Open неизвестный UI
session ID освобождается по idle lease; автоматического повтора запроса нет.

UI запускает audio clock после первого видео-кадра, выбирает показанный кадр
по числу проигранных samples. Play/pause освобождают текущую video-сессию;
продолжение и seek открывают новую с точным локальным кадром проекта. Потеря
фокуса, Reload, смена клипа и закрытие прекращают текущий просмотр. При
недоступном звуковом устройстве сохраняется существующий silent-clock fallback
с предупреждением.

## Приёмка

Сценарии используют synthetic CFR30fps/ALAC+WAV, project24fps, source-in4,
speed1.25, native220Hz/music440Hz/event880Hz. CPU raster проверяет production
Cobalt SceneView/UiSession на scale1/1.5/2; только physical sound device заменён
управляемыми sample clocks. Кадры сверяются с независимым full decode select(n),
PCM — с отдельно декодированными и вручную размещёнными дорожками.

| Требование | Проверка |
|---|---|
| непрерывное воспроизведение клипа со звуком через общий backend, play/pause и seek | `BackendPlaybackRasterTests.RoutedPlayPauseSeekAndEnd_UseRealEneritVideoAndMixedPcm_WithoutWriting` (scale1/1.5/2): pixels/source frames5/14/42/153 и384000 PCM samples сверяются с независимыми источниками |
| Добавь сквозные проверки | `AudioPreparation_UsesSavedMuteFlagsAndAbsoluteMusicAndClipEventPlacement`, `Lifecycle_StopsOwnedPlaybackAndReleasesVideo_WithoutWriting`, `RepeatedFocusChanges_StopAndDrainEveryOwnedDecoder_WithoutWrites` (10cycles), `OwnedBackendDisconnect_DuringPlaybackStopsSoundAndReleasesDecoder_WithoutReplayOrWrites` |
| Конфликт и неизменность данных | `ChangedExactProjectBytes_DuringPlaybackStopWithConflict_AndRequireExplicitReload`, `StaleExactBytesRejectNextPayloadAndReapDecoder_WithoutAddingHistory`, `BackendPlaybackStateTests.Playback_RevisionConflictStopsAndRequiresExplicitReload` |
| Ограничения, отмена и cleanup | `ContinuousSession_BoundsAdmissionAndPreservesBytes_ExplicitCloseReapsOneOwnedDecoder`, `CloseInterruptsBlockedRead_AndDisposalReapsItsOnlyChild`, `CancelledReadClosesResource_WithoutReplayOrWrite`, `IdleAbandonmentReapsDecoder_AndAllowsFreshSession`, `Video_DisposeInterruptsKnownRemoteSession_BeforePendingReadCanReturn` |
| актуализируй Plan.md и Status.md | [Plan](../../Plan.md#backend-playback), [Status](../../Status.md#backend-playback): текущие возможности отделены от Python/Web и исторических результатов |
| подними версию по принятому правилу | [Directory.Build.props](../Directory.Build.props): независимая версия Studio1.2611.17.5-alpha выше main d613e87/1.2611.17.4-alpha, UTC17.11, стадияalpha сохранена |

Финальная приёмка 17.11.2026:

- Debug и Release: каждый **Passed:623, Failed:0, Skipped:5**; новые100C#cases,
  из них14 сквозных raster/IPC. Все новые сценарии выполнялись, без skips.
- Опубликованный EXE: **Passed:68, Failed:0, Skipped:0**, включая14 playback cases.
- Python: **Ran312 tests, OK**, exit0,46.978s. Production Python-код не менялся.
- Обе полные non-incremental сборки с EmitCompilerGeneratedFiles и
  WarningsAsErrors=CS1591:0errors/9generatedCS8669 закреплённого Enerit
  (7прежних+2дляновогоDTO); подавления и изменения framework не добавлялись.
- Зафиксированы221 input SHA; после проверок изменённых входов0. Отдельно
  просмотрен Release PNG: кадр/панели/playback status отображаются корректно.
  Сравнение rendered preview выполняется в тестах по строкам pixels с sRGB.
- Pause/seek/close/disconnect,20s idle abandonment и десять последовательных
  focus cycles проходят с реальными backend/FFmpeg. Fixture проверяет bytes,
  revision/history, отсутствие journal, освобождение video и owned backend.

Пять прежних skipped: отдельные fixtures для author-copy/legacy HTTP/реального
audio-device/native-window и linked-root probe, требующий явного допуска в текущем
окружении. Они не заменяют исполненные synthetic media/raster/IPC-проверки.

В промежуточном focused-прогоне был timeout эксклюзивного доступа к video после
FocusLost. Клиент усилен немедленным idempotent Close известной сессии, независимо
от завершения локального Read; добавлена детерминированная регрессия blocked Read.
Одна промежуточная задержка наблюдалась и после этой правки; её причина не
установлена. Fixture сохраняет реальные video Completion/Error для диагностики.
В финальных Debug/Release/published, включая30 суммарных focus cycles, задержка
не повторилась; причинное устранение именно этого редкого timeout не заявляется.

## Evidence и команды

`src/.local/backend5-20261117/`: final-inputs.json/final-freeze.json,
acceptance-summary.json, full-debug.trx/full-release.trx/published.trx,
build/test/publish binlogs и logs, frames-debug/frames-release/frames-published,
python-final.log. Промежуточные failed logs сохранены отдельно. FFmpeg/ffprobe9.0.2
из прежнего portable QA-каталога; новая установка, глобальный PATH и загрузки не нужны.

В текущем окружении restore потребовал read-доступ SDK к пользовательскому
NuGet.Config вне sandbox; затем build/test/publish выполнялись в sandbox.
Команды из src (полные аргументы build/test/publish — в сохранённом скрипте):

```powershell
$cache=(Resolve-Path '.local/requests-acceptance-20261117-5a9c9c62/packages').Path
dotnet restore TrueMoon.Tales.Studio.slnx --artifacts-path .local/studio-build --packages $cache --configfile NuGet.Config -p:NuGetAudit=false '-bl:.local/backend5-20261117/restore-final-{}.binlog'
& "$env:SystemRoot/System32/WindowsPowerShell/v1.0/powershell.exe" -NoProfile -ExecutionPolicy Bypass -File .local/backend5-20261117/commands-final.ps1 -SkipRestore
# Python запущен отдельно после корректировки обработки stderr в Windows PowerShell5:
# из корня Studio, exit0:
& 'E:/ComfyUI_windows_portable/python_embeded/python.exe' -s -B -m unittest discover -s tests
```

Финальная проверка фиксированных входов:
`E:/ComfyUI_windows_portable/python_embeded/python.exe -s -B .local/backend5-20261117/collect.py`.

## Границы

Это просмотр одного выбранного клипа. Backend trim/export, переходы/титры,
непрерывный монтаж нескольких клипов, VFR, физические Space/Escape, Windows DPI
и аппаратная AV-задержка остаются отдельными этапами. Raster/IPC и PCM acceptance
не доказывают физическое воспроизведение динамиками или native present.
Авторские проекты, GPU/ComfyUI и код соседнего TrueMoon не используются.
