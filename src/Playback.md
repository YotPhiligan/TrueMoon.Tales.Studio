# Непрерывный просмотр и звуковая волна

07.11.2026. Следующий срез после [drag/trim](TimelineDrag.md): воспроизведение
выбранного монтажного клипа, пауза/продолжение и волна его фактического микса.

## Что работает

- «Старт»/«Пауза» и команда меню «Клип». Продолжение сохраняет позицию;
  конец удерживает последний включённый кадр, следующий старт повторяет клип.
  Пробел предусмотрен в области предпросмотра/таймлайна. Enter активирует кнопку.
- Один FFmpeg-процесс декодирует последовательность вместо запуска на каждый
  кадр. Очередь ограничена тремя кадрами. Скорость клипа и сетка проекта
  определяют выбор исходного CFR-кадра, включая повторы при замедлении.
- Native-аудио учитывает общий `clip_audio`, `mute_native_audio`, вход/выход
  и скорость. Музыка и события используют прежние абсолютные позиции,
  `source_in_frame`, stream, gain/mute; события клипа ограничены его длиной,
  воспроизводятся при 1× независимо от скорости видео. Раскладка берётся
  из действующего проекта через существующий HTTP API.
- Микс — stereo float PCM, 48 kHz. Waveform использует максимум абсолютной
  амплитуды обоих каналов того же микса. Тишина показана линией; сумма
  ограничена полной шкалой без нормализации. Ревизия/клип/сохранение trim
  заменяют подготовку, поздний результат прежней подготовки игнорируется.
- Windows-вывод через закреплённый `NAudio.WinMM 3.3.0`; видео следует
  счётчику реально воспроизведённых samples. Без доступного устройства
  продолжается просмотр без звука с явным сообщением. Тишина использует
  монотонные часы. Системная громкость не меняется.
- Seek, trim, смена клипа/дубля, покадровый шаг, потеря фокуса и закрытие
  останавливают текущий поток и звуковой вывод. Ручной черновик не запускает
  playback; существующие правила записи/конфликтов trim сохранены.
- Ошибка декодера/устройства останавливает оба ресурса; успешный повтор
  очищает ошибку. Зависшие звуковые часы обнаруживаются после 3 секунд
  без прогресса. Время передаётся из UI frame callback, тесты не спят.

Подготовка выполняется вне UI-потока, максимум 45 секунд. Клип ограничен
10 минутами: PCM занимает не более примерно 230 MB, wave — до 1024 bins,
очередь видео — три кадра 480×270. Чтение кадра имеет 10-секундный таймаут.
Отмена завершает только собственный FFmpeg через сохранённый Process handle;
завершение producer доступно через `Completion`. GPU-генерация не запускается.

## Автоматические проверки

```powershell
dotnet test TrueMoon.Tales.Studio.slnx --no-restore --artifacts-path .local/studio-build --configuration Debug '-bl:.local/playback-tests-debug-{}.binlog'
dotnet test TrueMoon.Tales.Studio.slnx --no-restore --artifacts-path .local/studio-build --configuration Release '-bl:.local/playback-tests-release-{}.binlog'
```

Debug/Release: **67 passed, 0 failed**, без предупреждений; одна опциональная
интеграция пропускается без явного QA endpoint. Она отдельно прошла на живой
сцене. [Файлы тестов](TrueMoon.Tales.Studio.Tests/Presentation/Scene/PlaybackModelTests.cs).
Проверки жизненного цикла QA после расширения fixture: **10/10**.
Полная Release-сборка с `--no-incremental -m` также прошла без предупреждений
и ошибок (`playback-final-build-20261107-153451--58996--Ue+CxV.binlog`).

| Требование | Проверки |
|---|---|
| Native/music/event, mute, stream, границы и 23.976 | `AudioTimeline_CombinesNativeMusicAndEventOnOneAbsoluteWindow`, `SoundEvents_UseOwnerOffsetsAndClipBoundariesWithoutInheritingVideoSpeed`, `FractionalFps_UsesAbsoluteSampleRoundingAtBothWindowEdges` |
| Волна обоих каналов и тишина | `Peaks_UseBothChannelsAbsoluteMagnitudeAndClipToFullScale`, `Peaks_SilencePreservesBinsAndEmptyHasNoWave` |
| Первое видео перед звуком, sample clock, пауза/конец | `Playback_WaitsForFirstVideoFrameBeforeStartingAudioClock`, `Playback_SampleClockSelectsDueFrameAndRetainsFutureFrame`, `Playback_PauseDisposesMediaAndResumeStartsAtPausedFrame`, `Playback_EndRetainsLastFrameAndReplayRestartsAtBeginning` |
| Seek/trim/switch/закрытие/поздняя подготовка | `Playback_SeekStopsMediaAndShowsRequestedFrameWithoutWriting`, `Preparation_ReplacedSnapshotCancelsOldWorkAndIgnoresItsLateResult`, `Dispose_CancelsPreparationAndStopsLiveMediaWithoutApplyingLateResults` |
| Ошибка, повтор и зависание часов | `Playback_DecoderOrOutputErrorStopsBothResourcesWithoutWriting`, `Playback_SuccessfulRetryClearsPreviousPlaybackError`, `Playback_StalledAudioClockStopsAfterBoundedGracePeriod`, `Playback_AudioClockProgressResetsStallBudget` |
| Реальный FFmpeg, PCM, устройство, cancel/drain | `Ffmpeg_AudibleMixAndEveryFrame_MatchFixtureAndReleaseProcesses` |

## Воспроизведение QA-проверки

Из `src`, Python с уже установленными PyAV/Pillow/NumPy/imageio_ffmpeg:

```powershell
& 'E:/ComfyUI_windows_portable/python_embeded/python.exe' -s -B Verification/PlaybackQA.py --seconds 900
```

Скрипт печатает ID сессии и свободный loopback endpoint. Для короткой сцены
без окна, пока её сервер жив, явно запустить интеграцию и CPU-сверку:

```powershell
& 'E:/ComfyUI_windows_portable/python_embeded/python.exe' -s -B Verification/CheckPlaybackQA.py <ID> --integration
& 'E:/ComfyUI_windows_portable/python_embeded/python.exe' -s -B Verification/CheckPlaybackQA.py <ID> --cpu
& 'E:/ComfyUI_windows_portable/python_embeded/python.exe' -s -B Verification/CheckPlaybackQA.py <ID>
& 'E:/ComfyUI_windows_portable/python_embeded/python.exe' -s -B Verification/PlaybackQA.py --stop <ID>
```

`--integration` использует реальные FFmpeg/короткий тестовый звук и ограниченную
группу процессов; `--cpu` явно создаёт CPU MP4 или проверяет уже готовый экспорт
этой ревизии. Обычный вызов checker только сверяет проект/медиа/отсутствие POST
и Jobs. Все данные остаются в `src/.local/qa-sessions/<ID>/`.

Для настоящего окна:

```powershell
& 'E:/ComfyUI_windows_portable/python_embeded/python.exe' -s -B Verification/PlaybackQA.py --long --ui .local/studio-build/bin/TrueMoon.Tales.Studio/release/TrueMoon.Tales.Studio.exe --seconds 300
```

`--long` создаёт 25-секундный участок для проверки паузы. Применяются
[владение, дедлайны и очистка QA](QASessions.md); чужие процессы не затрагиваются.

## Фактический результат

- Короткая сессия `c5ae0b707a2f499cb9097c40c0b6be40`, порт 51171, ревизия 3:
  native 220 Hz, музыка 440 Hz и событие 880 Hz. PCM — 288000 stereo sample frames.
  При 1×/0.5×/2× получены 144/288/72 кадра; первый, второй, средний и последний
  побайтно совпали с покадровым декодером. Cancel при заполненной очереди
  завершил producer. Звуковое устройство продвинулось на 29377 samples,
  fallback не использовался. Проект API остался прежним.
- Штатный CPU MP4: 216 кадров при 24 fps. Контрольные кадры соответствуют
  источнику 24/25/96/167; независимый PyAV-декодер отличил их от соседних.
  RMSE PCM preview против AAC MP4 — 0.005588. Компонента 880 Hz присутствует
  только в локальном интервале `[2,3)` секунд. Сохранены `playback-integration.json`,
  `playback-cpu-checks.json`, PCM, контрольные BGRA и экспорт. Проект не изменился.
- Native Release, `b1b00f0658e6460e8e35ea0e9320a063`, порт 56702:
  видна волна, просмотр дошёл до FRAME 623, пауза кнопкой удержала FRAME 435,
  Enter продолжил с этой позиции (FRAME 448). Сессия завершилась по своему
  300-секундному дедлайну и очистила группу. Данные/4 SHA исходников сохранились,
  POST и Jobs отсутствуют.
- Последняя Release-сборка, `083cbcfd0d034677b9b8ac792d8a42fa`, порт 55525:
  FRAME 024 → 036 → 167, оба статуса показали «Конец клипа». Штатное закрытие
  завершило сервер/UI (`stopped/ui-exit`, код 0); аудит данных прошёл.
- Повтор через сохранённый `CheckPlaybackQA.py --integration`, сессия
  `e097ad9dfc1843118582cb1b91f6bfe1`, порт 52206: 1/1 passed, устройство без
  fallback, очередь отменена и завершена. Аудит проекта/4 SHA/POST/Jobs прошёл;
  явный `--stop` завершил сервер (`stopped/requested`, код 0).

## Границы и следующий шаг

Проверен выбранный CFR-клип, микс при 1× и кадровая последовательность при
0.5×/1×/2×. VFR, непрерывный переход между клипами, композиция переходов/титров,
звуковой микс при всех скоростях, полная DPI/клавиатурная матрица и измеренная
AV-задержка устройства остаются приёмкой. Физический Space в этой автоматизации
не подтвердил паузу; routed-тест прошёл, Enter и кнопка подтверждены. Это не
доказательство дефекта TrueMoon. Новых заявок движку для этого среза нет.

Следующий этап — перемещение целых клипов и проверка длинной шкалы, затем
двухпроцессный C#-сервис/Enerit. Исходники движка и Python production API не менялись.
