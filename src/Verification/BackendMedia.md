# STUDIO-BACKEND4 · media resolver и одиночный кадр

Проверено 17.11.2026 UTC, Windows x64. Studio **1.2611.17.4-alpha**, TrueMoon
**1.2611.17.1-alpha**, SDK11.0.100/net11.0/runtime-async=on. Version выше рабочей
и origin/main dcba204 /1.2611.17.3-alpha по NuGetVersion.CompareTo; входы
зафиксированы до финальных build/test/publish.

## Контракт и владение

Backend того же EXE через Mithril/Enerit читает только видео, зарегистрированное
в project.Properties.assets и используемое source/take.VideoAssetId. Запрос
выбирает asset_id и исходные expected_revision/fingerprint, без файлового пути,
ffmpeg options или прав доступа. Поле root проекта остаётся данными.

**Enerit protocol3** добавляет MediaInfoAsync и FrameAsync; Hello проверяет роль,
product/protocol/project-contract и PID принадлежащего сессии backend. Storage
schema2, ProjectContractv1 и HTTP APIv1 сохраняются. Чтение не захватывает writer,
не мигрирует, не восстанавливает, не создаёт revision/history/cache и не повторяется
автоматически. Baseline проверяется до и после декодирования: commit во время
операции приводит к revision_conflict, без выдачи устаревших пикселей.

BackendMediaResolver получает неизменяемую trusted configuration через отложенную
DI-owned IFactory. Поддерживаются mounts, reserved names и path_aliases: относительные
legacy имена, абсолютные прежние пути и @mount prefixes; longest-prefix chain,
cycle detection и не более16перенаправлений. Aliases применяются лексически до IO
отключённого legacy. Protected/автоматический TrueMoon deny, allowlist, traversal,
URL/UNC/device/ADS, ambiguous Windows paths и reparse ancestors проверяются до
открытия файла. Доверенные config roots могут содержать относительный '..';
metadata references не могут. Missing file отличается от policy denial.

Одна media-операция на backend, перегрузка media_busy. Операция ограничена10s,
каждый owned FFmpeg/ffprobe —8s + bounded cleanup; transport/client12s, HTTP12s.
Source file удерживается read lease без share-write/delete во время операции;
resolver повторно проверяется перед ответом. Нет сохранённого frame cache; UI
удерживает текущий кадр и coalesces seek до одного активного и последнего запроса.

Видео: локальные MOV/MP4/M4V/3GP, Matroska/WebM и AVI, forced demuxer,
protocol_whitelist=file,pipe, MOV external drefs выключены, explicit0:v:0,
-an/-sn/-dn и CPU threads1. Метаданные ограничены64KiB, stderr16KiB,
fps(0,240], frames<=10M, source<=8192пооси/16Mpx. Поддерживается CFR;
явное avg/r-frame-rate несоответствие отклоняется, VFR timing не реализован.
Выход фиксирован: **480×270 BGRA**, letterbox, **518400bytes**, pixel SHA256.
Процессы завершаются/ожидаются по сохранённым handles в finally, также при
отмене, EOF, ошибке, overflow и stop сервиса.

Для wire используется штатный Memory<byte> codec. byte[] collection codec имеет
предел65536элементов и для такого кадра не подходит; лимиты фреймворка не менялись.
DTO копирует входной rental в принадлежащие ему managed pixels и возвращает rental;
ownership проверен отдельно. Transport frame остаётся меньше штатного1MiB.

## HTTP и CLI

POST /api/backend/v1/media-info:
```json
{"asset_id":"video-a","expected_revision":12,"fingerprint":"<64 hex SHA проекта>"}
```
POST /api/backend/v1/frame дополнительно требует неотрицательный frame.

Ответ: api_version1/schema_version2/media. Media содержит asset_id, revision,
fingerprint, fps/frame_count/source_width/source_height, width480/height270,
frame_index(-1дляmetadata), pixels(base64BGRA, пусто дляmetadata), pixel_sha256
и пустой error_code. Физические пути не возвращаются. Authority/token/JSON fields,
duplicate keys и limits — те же, что в BACKEND3. Stable ошибки:409baseline/recovery/
migration,403access,404media,422invalid_frame/invalid_media,429media_busy,
503decoder_unavailable/transport,504media_timeout. Никаких automatic retry.

```powershell
# После --serve-project; URL/token получены из ready JSON этой сессии.
python studio_agent.py --url http://127.0.0.1:<port> --token <token> backend-media --asset video-a --revision 12 --sha256 <sha>
python studio_agent.py --url http://127.0.0.1:<port> --token <token> backend-frame --asset video-a --frame 20 --revision 12 --sha256 <sha> --output <новый.png>
```

CLI проверяет exact shape/types/bounds, baseline echo, raw length и pixel SHA
до создания PNG. PNG пишется stdlib через exclusive xb; существующие файлы,
Windows ADS/devices/ambiguous names не перезаписываются. Output authority остаётся
на стороне вызывающего CLI; сервер не выбирает output path. Proxy/redirect/bootstrap
не используются. Standalone studio_agent.py остаётся в корне Studio, не внутри publish.

Backend optional config v1: **ffmpeg_executable**, **ffprobe_executable** —
доверенные пути к уже подготовленным tools; относительные считаются от config.
Без полей используются ffmpeg/ffprobe из PATH. Python strict config принимает
эти optional keys, но legacy runner не меняет способ выбора своих tools.
В этой приёмке использован переносимый FFmpeg/ffprobe9.0.2 в .local, без установки
в систему/PATH. [Источник Windows builds](https://ffmpeg.org/download.html#build-windows),
[релиз издателя](https://github.com/GyanD/codexffmpeg/releases/tag/9.0.2).
Полный SHA архива60F467265B1E312373DBCD92200C2618A74850F98D3D078E94296BB3FA2047BA
совпал с опубликованным checksum и asset digest; hashes executables сохранены.

## UI и проверки

BackendSceneService и BackendFrameDecoder подключены в существующий Cobalt-граф.
Сохраняются startup/reload, выбор клипа/дубля, кадр по линейке/кнопкам и перенос.
Частоты источника и проекта, source-in/out и speed учитываются прежней монтажной
математикой. Отдельная SupportsTrim capability сохраняет запрет trim; непрерывное
воспроизведение/звук/export не включены. Missing/denied/unsupported media не мешает
открыть метаданные и отменить перенос. При переходе на отсутствующее видео
предыдущий Image.Source очищается.

| Требование | Конкретное evidence |
|---|---|
| media resolver с aliases | BackendMediaResolverTests.Resolve_LongestPrefixAndChainedAliases_RelativeAbsoluteAndNamedRoutes_ReadSameUnicodeFile; Resolve_JunctionIsDeniedBeforeFollowingTarget_WhileDisabledLegacyAliasesStayLexical |
| получение кадра через общий backend | BackendMediaReadOnlyTests.ReadOnlyConnection_ReadsRealFrameWhileAnotherSessionOwnsWriter_WithoutClaimingOrMutating; BackendMediaOwnershipTests.WireBuffer_CopiesManagedInputAndReturnsDeserializedRentalExactlyOnce |
| Добавь сквозные проверки UI/CLI | BackendMediaUiCliTests.UiAndCli_ResolveAliasesAndReadExactSourceFrames_ThroughOneBackendWithoutWriting (scale1/1.5/2); DistinctAssetIds_SelectTheirOwnFile_AndCliPngMatchesIndependentSecondAssetDecode |
| Изменение baseline во время decode | BackendMediaServiceTests.MediaQuery_RejectsACommitDuringDecode_WithoutAddingAWriteOrReturningStaleData |
| Ограничение очереди и cleanup | BackendMediaServiceTests.MediaQuery_BoundsAdmission_AndDisposalCancelsAndReapsItsActiveDecoderWithoutWriting; BackendMediaDecoderTests.Read_Cancellation_ReapsOnlyItsOwnedChild_AndPreservesOtherProcess |
| Missing media и прежнее изображение | BackendMediaUiCliTests.MissingMediaAfterRoutedClipSelection_ClearsPreviousImageAndRenderedFrame_WithoutWriting |
| CLI PNG/коррупция/путь вывода | test_frame_exports_png_whose_rgba_rows_crc_and_hash_match_validated_bgra_without_base64_stdout; test_corrupt_bytes_sha_dimensions_frame_and_baseline_never_create_output_or_retry; test_windows_ads_and_ambiguous_output_names_are_rejected_before_http_preserving_existing_media |

Новые153 C# cases и17 Python tests. Финальные результаты:
- Debug/Release: **каждый Passed:523, Failed:0, Skipped:5**.
- Published Release EXE: **Passed:54, Failed:0, Skipped:0**.
- Python: **312 tests,OK**.
- Strict XML/emit non-incremental builds:0errors/7generatedCS8669, CS1591 как error.
  Пять предупреждений прежние, два добавлены новым DTO; это та же nullable-context
  категория закреплённого Enerit. Подавления/правки framework не добавлялись.

Синтетический MP4:90frames/30fps,320×240; проект24fps, source-in4/source-out52,
speed2. UI/CLI native source frames5/20/64 сверены с независимым full decode
select(n), без повторения seek-алгоритма. Проверяется настоящий rendered preview
crop каждого кадра, не только model.Pixels. Второй действительно отличный MP4
проверяет выбор файла по asset ID. Byte-identical JSON/history и тот же writer
проверяются после всех read-запросов. Backend позволяет отдельному read-only
процессу читать кадр, пока другая сессия удерживает writer.

99PNG Debug/Release и48published/Release совпали; root/session disposed,
font/native SHA проверены. PNG просмотрен. Extended Skia SHA
7CAD4A4AA72DFEC19F05C686088BCF38D6585C984E19C4884985FF4C03CA8FCE сохранён.

## Evidence и границы

src/.local/backend4-20261117-40bc43ff/ содержит commands-final.ps1 (точные команды),
207frozen inputs, versions/NuGet comparison, final TRX/logs/binlogs, generated sources,
PNG/manifests, publish, acceptance.json и cleanup.json.
```powershell
# Из src; скрипт задаёт Python/tools/EXE/evidence env и возвращает их в finally.
pwsh -NoProfile -File .local/backend4-20261117-40bc43ff/commands-final.ps1
```
Media ledger: media-evidence/media-intake.json, synthetic30fps.mp4, raw probe и
frames5/20/64BGRA. Source SHA
EF8471EC241576658BADD37E13BF768FA91840925CE2A298C195E05ABABD7381.
Этот повтор источника дал те же pixel SHA трёх масштабов финальной приёмки.

Только синтетические изолированные данные/CPU. Авторские проекты, GPU/ComfyUI,
TrueMoon-код и native окна не затронуты. Проверочные процессы/listeners завершены.
Пять прежних skips перечислены в acceptance.json; physical Escape/keyboard/DPI,
длинный таймлайн, прежний native AV и типографические ограничения не закрыты.
Это одиночные CFR-кадры и указанная sampled MP4 parity; не непрерывное аудио/
воспроизведение, не backend trim/export и не полная монтажная CPU MP4 acceptance.
Legacy Web UI остаётся schema1. Следующий этап — непрерывное воспроизведение и звук.
