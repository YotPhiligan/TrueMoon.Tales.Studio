# STUDIO-NET11: .NET 11 и TrueMoon

16.11.2026. Studio **1.2611.16.1-alpha**, TrueMoon **1.2611.16.1-alpha**.
SDK 11.0.100, TF/TestsTF=net11.0, LangVersion=latest,
Features включает runtime-async=on. Проверенный профиль: Windows x64,
framework-dependent, Cobalt, xUnit 2/VSTest.

## Изменения и поставка

Общие Version/TF/TestsTF/Features находятся в `src/Directory.Build.props`, SDK —
в `global.json`, версия зависимости — в `Directory.Packages.props`.
Принятое 15 ноября правило Studio уже совпадало с TrueMoon: x.yyMM.d.rev[-postfix],
дата UTC, независимый дневной счётчик каждого репозитория, одно повышение на
production-набор до финальных проверок. Условное переопределение Version сохранено;
автоматического повышения при каждой сборке нет. Стадия остаётся alpha.
Перед выбором номера проверен remote main Studio:
`bf40a5c5409e67fa883fec09b49599f78e985921`, Version=0.1.0.1-alpha.

Python launch/bootstrap/health/Server header получают продуктовую версию из того
же props через `studio/version.py`. При распространении Python/Web части рядом
с ней сохраняется `src/Directory.Build.props`. HTTP/IPC и schema versions не менялись.
Точка входа C# теперь возвращает результат RunStudioAsync: ранее неверные аргументы
печатали usage, но EXE возвращал 0 вместо предусмотренного 2.
Microsoft.NET.Test.Sdk обновлён до 18.12.0, xUnit 2.9.3 и runner 4.0.1 сохранены;
устаревший DotNetCliToolReference dotnet-xunit удалён. NAudio.WinMM остаётся 3.3.0.

Девять пакетов приняты из точных архивов
[ENG7B3 manifest](../../../TrueMoon/src/docs/engine/ENG7B3_BASELINE.json),
с проверкой SHA-256, nuspec id/version и полноты набора до публикации в feed.
Повторный импорт принимает только совпадающие immutable архивы.
Отрицательные проверки неверной версии, SHA, nuspec identity и дублированной
записи завершились отказом; feed и его manifests не изменились. Новая версия
сравнена с main через NuGet.Versioning, а не строковым сравнением.
Исходники TrueMoon не менялись и не пересобирались; более новая версия его
рабочей копии не подменяет запрошенную зависимость Studio.

```powershell
./Update-TrueMoonPackages.ps1 -PackageManifest ../../TrueMoon/src/docs/engine/ENG7B3_BASELINE.json
```

Источник для режима `-FrameworkRoot` должен объявлять ровно закреплённую в Studio
версию. При несовпадении скрипт останавливается; обхода через подмену Version нет.
Передавать manifest вместе с доступными архивами; локальные пути в исходном
manifest не являются публичным NuGet feed. NuGet.Config сохраняет source mapping.

## Проверки

Evidence: `src/.local/dotnet11-1.2611.16.1-alpha/`, binlog/TRX/логи,
`build-profile.json`, `acceptance.json`. Команды выполняются из `src`.

```powershell
dotnet restore TrueMoon.Tales.Studio.slnx --configfile NuGet.Config --packages .local/dotnet11-1.2611.16.1-alpha/packages --artifacts-path .local/studio-build '-bl:.local/dotnet11-1.2611.16.1-alpha/restore-{}.binlog'
dotnet test TrueMoon.Tales.Studio.slnx --no-restore --artifacts-path .local/studio-build -c Debug --logger trx --results-directory .local/dotnet11-1.2611.16.1-alpha/tests-Debug '-bl:.local/dotnet11-1.2611.16.1-alpha/tests-Debug-{}.binlog'
dotnet test TrueMoon.Tales.Studio.slnx --no-restore --artifacts-path .local/studio-build -c Release --logger trx --results-directory .local/dotnet11-1.2611.16.1-alpha/tests-Release '-bl:.local/dotnet11-1.2611.16.1-alpha/tests-Release-{}.binlog'
```

Полные пересборки, по одной на Debug/Release (`$configuration`), выполнены так:

```powershell
dotnet build TrueMoon.Tales.Studio.slnx --no-restore --no-incremental --artifacts-path .local/studio-build -c $configuration -m -p:EmitCompilerGeneratedFiles=true "-p:CompilerGeneratedFilesOutputPath=$PWD/.local/dotnet11-1.2611.16.1-alpha/generated-$configuration/`$(MSBuildProjectName)" -p:WarningsAsErrors=CS1591 "-bl:.local/dotnet11-1.2611.16.1-alpha/build-$configuration-{}.binlog"
```

Debug/Release: Passed: 190, Failed: 0, Skipped: 4 каждый. Skips: две opt-in
native/media интеграции, JSON-копия и symbolic links без Windows-привилегии 1314.
Первые три выполнены отдельно; link-проверка остаётся явно пропущенной.
Полные non-incremental D/R builds с EmitCompilerGeneratedFiles=true и
WarningsAsErrors=CS1591: 0 warnings / 0 errors, по 7 Cobalt generated sources.
Bundled generator приходит из matching пакета Cobalt, отдельный analyzer package
не добавлялся. Фактически вычисленные SDK/TFM/Features сохранены через MSBuild.

Python: `python -s -B -m unittest discover -s ../tests` — 288 tests, OK.
Узкий `test_server.py` — 10 tests, OK, включая
`test_product_version_matches_build_bootstrap_health_and_server_header`:
значения продукта совпадают с props, чтение не пишет проект и не вызывает prepare/launch.

Свежая копия JSON Клэр: `store-copy-b9526e4d25d04379b347b3f2bef47e82`,
отдельный ProjectStoreCopyIntegrationTests — Passed: 1, Failed: 0, Skipped: 0.
Ревизии 55 → 56 → 57, schema 1 → 2; 55 sources, 62 takes, 2 windows.
Два точных снимка истории и SHA исходника сохранены. Исходник не содержит
actions/clips; A–B–A покрывается синтетическими тестами общего набора.

Native lifecycle: QA `b31e84a4757841618ab38903114dbc6a`, порт 64915,
Passed: 1, Failed: 0, Skipped: 0. 11 окон, в длительном resize/seek 882 кадра;
все окна закрылись. Проверка через CheckNativeQA/Job Object, без GPU и
авторского проекта. Это не подтверждение полной physical input/DPI матрицы.

Playback: QA `074ed365f8844367a8285f797c45c94a`, порт 61381,
Passed: 1, Failed: 0, Skipped: 0. 144/288/72 кадра при 1×/0.5×/2×,
288000 stereo samples, реальное устройство без fallback, producer отменён/drained.
CPU MP4 — 216 кадров при 24 fps; контроль источника 24/25/96/167,
PCM/AAC RMSE=0.0055880039, событие 880 Hz только в своём интервале.
Проект и четыре SHA медиа сохранены, POST=0, Jobs нет.
Обе QA-сессии остановлены по ID, tool sessions завершены.

Независимый снимок четырёх C#-проектов восстановлен с пустым cache и отдельным
feed ровно из девяти архивов; Release publish прошёл. runtimeconfig — net11.0 /
Microsoft.NETCore.App 11.0.0; deps содержит единые TrueMoon 1.2611.16.1-alpha,
SkiaSharp 4.156.1 и RichTextKit 0.6.173. SHA extended native DLL:
`7CAD4A4AA72DFEC19F05C686088BCF38D6585C984E19C4884985FF4C03CA8FCE`.
Это проверка чистого снимка рабочей копии, включая ранее добавленные Contracts/
Persistence, а не утверждение о публикации этих незакоммиченных файлов в Git.
После исправления Program повторены D/R suites с generated sources/CS1591 и
publish; фактический опубликованный EXE при пустых и неверных аргументах печатает
usage и возвращает 2 без открытия окна. `acceptance.json` содержит окончательные
TRX, source hashes, профиль, CLI результаты, native SHA и очистку QA.

## Границы

Python HTTP API остаётся владельцем рабочих проектов schema 1. ProjectStore не
подключён к backend/IPC; переход не мигрирует авторские проекты при чтении.
Полные media/physical keyboard/DPI, VFR, несколько клипов, AV-задержка, длительная
стабильность и причина прежнего 0xC0000005 остаются отдельными критериями.
Наличие успешных native повторов не закрывает исходную причину AV.
Другие OS/RID, self-contained, AOT/trimming и внешний NuGet release не заявляются.
Существующие изменения модели/контрактов/Persistence и соседний TrueMoon сохранены.
