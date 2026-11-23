# Десктопный прототип Studio

C# / net11.0: Argentis поверх Python HTTP API и backend того же EXE через
Mithril/Enerit с schema2, переносом клипа и медиапредпросмотром. Рабочий веб-интерфейс
и сервер находятся в корне Studio. [План перехода](../Plan.md#исследование-архитектуры-studio)
и [проверенная сцена](../Verification/Argentis.Prototype.md) описывают границы прототипа.
Первое окно оформлено по [общему дизайн-коду TrueMoon](../../TrueMoon/src/docs/alloy/DESIGN.md).
[Доступные действия, проверка и ограничения окна](WindowDesign.md).
[Текущее расположение панелей, меню тайтлбара и рабочие сценарии](WindowMenus.md).
[Запуск проверок и обязательная очистка фоновых процессов](QASessions.md).
[Переиспользуемые правила UI-проверок: UiSession, raster и тестовый host](UITesting.md).
[CPU raster-стенд, PNG и результаты STUDIO-UIQA1](Verification/UIRaster.md).
[STUDIO-BACKEND2: перенос через Enerit и передача writer](Verification/BackendMove.md).
[STUDIO-BACKEND3: HTTP/CLI и совместная сессия UI](Verification/BackendHttp.md).
[STUDIO-REQUESTS1: пакеты и приёмка закрытых заявок](Verification/FrameworkRequests.md).
[STUDIO-BACKEND4: media resolver/aliases, кадры и CLI PNG](Verification/BackendMedia.md).
[STUDIO-BACKEND5: непрерывное видео/звук, play/pause и seek](Verification/BackendPlayback.md).
[STUDIO-BACKEND6: обрезка через общий backend, UI/CLI и CPU MP4](Verification/BackendTrim.md).
[STUDIO-BACKEND7: CPU MP4 через HTTP/CLI, статус, отмена и recovery](Verification/BackendExport.md).
[Shift+F10, перетаскивание курсора и краёв клипа](TimelineDrag.md).
[Непрерывный просмотр, звук и waveform](Playback.md).
[Диагностика native-падения и повторные проверки](NativeCrash.md).

Версия Studio указана в [Directory.Build.props](Directory.Build.props). SDK **11.0.100**,
`runtime-async=on`, TrueMoon **1.2611.22.7-alpha**, SkiaSharp **4.157.0**.
[Переход на .NET 11](Verification/DotNet11.md) · [перенос целого клипа](Verification/ClipMove.md). Старые датированные
проверки ниже относятся к своим версиям.

<a id="projects"></a>
## Стартовый экран «Проекты»

```powershell
.\TrueMoon.Tales.Studio.exe
.\TrueMoon.Tales.Studio.exe --projects --config "E:\path\Studio.config.json"
```

При запуске без аргументов Studio проверяет `TRUE_MOON_STUDIO_CONFIG`, затем
ищет профиль по пути `%LOCALAPPDATA%\TrueMoon.Tales\Studio\config.json`.
При отсутствии этого файла Studio ищет профили по прежним путям в AppData.
Если профилей ещё нет, Studio создаёт `config.json`.
[Настройка и перенос](../Workspace.md#настройка-и-запуск).
В пустом доступном для записи пространстве Studio создаёт проект «Без названия»
с размером 1280 × 736 при 24 fps. Если каталог уже содержит проекты, открывается
список. Явная команда `--projects --config` сразу показывает список.

Экран читает записи `state_root/Registry.json` и непосредственные каталоги
`projects_root`. Кнопка «Открыть» показывает schema1 и проекты без разрешения
на запись в окне просмотра. Для редактирования нужен schema2 и разрешение
в `project_write_roots` на файл проекта, writer lease, временные файлы
транзакции и историю. Пути реестра и недавних проходят проверку разрешённых
корней и ссылок. Редактор проекта без видеодубля показывает пустой таймлайн.

«Открыть существующий проект» принимает полный путь к папке или файлу `.studio`
внутри `projects_root` или `amv_root`. В папке Studio ищет `Project.studio`,
именной `.studio` в корне или `Studio.project.json` в корне либо `Production`.
При нескольких файлах Studio сообщает о конфликте. Успешное открытие
добавляет проект в недавние.

«Создать проект» запрашивает название, чётные ширину и высоту от 2 до 4096
и целый FPS от 1 до 120. В доверенном файле конфигурации укажите существующий
`projects_root` и добавьте этот каталог в `project_write_roots`. Новый проект
появится в каталоге и откроется с ревизией 1. Создание регистрируется
публикацией каталога, а `Registry.json` остаётся реестром Python.

Выберите строку и нажмите «Открыть» или Enter на строке. «К проектам» закрывает
редактор и возвращает список после завершения его backend. Порядок недавних
хранится в `state_root` отдельно для каждой конфигурации. При разрешённой записи
для schema1 доступна «Миграция…», для найденного журнала — «Восстановление…».
Обе команды показывают путь и выполняются после подтверждения.
[Сценарии экрана](WindowDesign.md#project-picker-design).

## Прямой запуск редактора и backend

Запись через новый backend включается явно: сначала `--migrate-project <каталог>
--config <config.json>` на изолированной копии, затем `--edit-project <каталог>
--config <config.json>`. В доверенном config нужны отдельные `project_write_roots`
для каталога этой копии. `--recover-project` — отдельное явное восстановление.
Backend-редактор выполняет перенос, обрезку и просмотр выбранного CFR-клипа со
звуком, play/pause и seek. CPU-экспорт schema2 запускается из окна или по HTTP/CLI
при настроенном runtime. [Рабочее окно и экспорт](WindowDesign.md#workspace-export).
Python HTTP продолжает обслуживать schema 1 и отклоняет schema 2.
Команды и правила передачи writer — в [отчёте](Verification/BackendMove.md#запуск).

HTTP/CLI schema2: `--serve-project <каталог> --config <config.json>` запускает
loopback API, `--with-ui` подключает окно к тому же writer. Возьмите URL и токен
из ready JSON. Тот же EXE поддерживает `backend-project`, `backend-move`,
`backend-trim`, `backend-media` и `backend-frame`. Правки и запросы
медиа используют ожидаемую ревизию и SHA.
[Примеры команд C# CLI](Verification/BackendHttp.md#studio-native-cli).
Запуск, статус, отмена, восстановление и скачивание CPU-экспорта MP4 доступны
из того же EXE.
[Настройка и команды экспорта](Verification/BackendExport.md#native-export-cli).
Все роли C# EXE требуют .NET11 **и ASP.NET Core11 runtime**; новых NuGet-пакетов нет.
[Контракт, запуск и проверки](Verification/BackendHttp.md).

## Сценарий и материалы

Откройте «Сценарий и материалы» в левой панели монтажного окна. Создайте
сегмент с фиксированным окном и добавьте действия. Библиотека ищет по названию
источника и дубля. «Новый источник» или «Добавить дубль» регистрируют видео
по разрешённой ссылке, например `@videos/Fixtures/plan.mp4`. «Кадр A» и
«Кадр B» сравнивают выбранные кадры. Для назначения в монтаж создайте
связь с действием, затем разместите клип с его началом, длительностью и входом
в дубле. Существующий клип позволяет выбрать другой дубль своего источника.
[Устройство окна и работа с конфликтами](WindowDesign.md#scenario-library).

## Модель проекта

Нормативная [модель действий, источников и монтажа](../ProjectModel.md) согласована
09.11.2026. Одно действие допускает несколько источников и клипов; источник
переиспользуется, монтажное окно ссылается на смысловой сегмент. Текущий
C#-редактор поддерживает schema2 через общий backend. Экран сценария работает
с несколькими источниками и клипами одного действия. [Модель и совместимость](../ProjectModel.md#совместимость-с-текущими-проектами).

## Структура C#-решения

`TrueMoon.Tales.Studio.Persistence/` — адаптер записи schema 2 и ProjectStore:
явная миграция, expected_revision/SHA, история, атомарная замена и recovery.
[API, права, формат и проверки на копии](ProjectStore.md).

`TrueMoon.Tales.Studio.Contracts/` — общие DTO действий, источников и клипов,
JSON-кодек транспорта и read-only адаптер старого `schema_version=1`.
Это DLL; Studio сохраняет один исполняемый проект.
[Контракты, совместимость и границы первого этапа](ProjectContracts.md).

```text
TrueMoon.Tales.Studio/
  Program.cs                  — только fluent-композиция App
  Configuration/              — параметры запуска и запуск App
  Models/                     — снимок сцены, клипы, дубли и звуковые данные
  Editing/                    — расчёт trim и размещения звука на шкале
  Infrastructure/             — подключение сервисов и клиент Python HTTP API
  Media/                      — FFmpeg, кадры, PCM, waveform и звуковой вывод
  Presentation/
    StudioPresentation.cs     — конфигурация окна через UseStudioWindow
    StudioCommands.cs         — общие команды и состояние панелей
    Projects/                 — стартовый экран, каталог и недавние
    Scenario/                 — сценарий, библиотека и сравнение кадров
    Scene/                    — окно, модель состояния, стиль, фабрика и шкала
    Shell/                    — тайтлбар и компоновка панелей
TrueMoon.Tales.Studio.Tests/
  Infrastructure/             — граф Cobalt, фабрики и владение ресурсами
  Editing/                    — правила trim и размещения звука
  Media/                      — waveform
  Presentation/Scene/         — меню, жесты и жизненный цикл playback
  Integration/                — явная проверка FFmpeg и звукового устройства
  Support/                    — общая тестовая сцена и подмены
```

Пространства имён следуют каталогам от `TrueMoon.Tales.Studio` и
`TrueMoon.Tales.Studio.Tests`. Части одного типа лежат рядом:
`SceneViewModel.cs` и `SceneViewModel.Playback.cs`. Новые файлы добавляются
в каталог соответствующей ответственности; SDK автоматически включает
вложенные `.cs`, отдельные `Compile Include` не нужны.

Проверка переноса 07.11.2026: тела типов/методов сохранены; Debug и Release —
`Passed: 67, Failed: 0, Skipped: 1`, без предупреждений сборки. Интеграция с реальным
устройством пропущена без явного QA endpoint. Команды из `src`:

```powershell
dotnet test TrueMoon.Tales.Studio.slnx --no-restore --artifacts-path .local/studio-build --configuration Debug '-bl:.local/structure-tests-debug-{}.binlog'
dotnet test TrueMoon.Tales.Studio.slnx --no-restore --artifacts-path .local/studio-build --configuration Release '-bl:.local/structure-tests-release-{}.binlog'
```

## Композиция App и владение HTTP

[Program.cs](TrueMoon.Tales.Studio/Program.cs) содержит только композицию:

```csharp
return await App.Builder(_ => { })
    .Setup(app => app
        .UseStudioServices()
        .UseStudioWindow())
    .RunStudioAsync(args);
```

[StudioOptions](TrueMoon.Tales.Studio/Configuration/StudioOptions.cs) сохраняет
прежний CLI: локальный HTTP endpoint, проект, существующий FFmpeg и необязательный
сегмент. [RunStudioAsync](TrueMoon.Tales.Studio/Configuration/StudioApplicationExtensions.cs)
проверяет аргументы до конфигурации App, возвращает 2 с прежней подсказкой при
ошибке и добавляет immutable options в DI. Исключения запуска App не подавляются.
[UseStudioWindow](TrueMoon.Tales.Studio/Presentation/StudioPresentation.cs) настраивает окно.

[StudioHttpClient](TrueMoon.Tales.Studio/Infrastructure/StudioHttpClient.cs)
создаётся Cobalt через конструктор с options и владеет своим HttpClient/handler.
Сохранены BaseAddress, таймаут 10 секунд и выключенные proxy/redirects. App
освобождает сервис после зависимой модели; HTTP отменяет оставшиеся запросы.
Ошибка конструктора также освобождает созданный транспорт. Тестовый конструктор
принимает handler с тем же владением, без открытия сокета.

07.11.2026 Debug/Release: `Passed: 90, Failed: 0, Skipped: 1`. Полная Release-сборка
прошла с двумя прежними CS1591 генератора. Пропущена явная медиаинтеграция.

| Требование | Проверка |
|---|---|
| Sync/async освобождение транспорта и отмена запросов | `Cobalt_DisposalCancelsModelWorkAndDisposesOwnedHttpOnce` |
| Ошибка создания не оставляет handler | `HttpClient_ConstructorFailureDisposesOwnedHandler` |
| Успешный App и ошибка старта освобождают HTTP | `RunStudio_OwnsHttpOnSuccessAndStartupFailure` |
| CLI и отказ до конфигурации App | `Options_ParsePreservesProjectSegmentAndFullMediaPath`, `RunStudio_InvalidArgumentsReturnsTwoWithoutConfiguringApp` |

```powershell
dotnet test TrueMoon.Tales.Studio.slnx --no-restore --artifacts-path .local/studio-build --configuration Debug '-bl:.local/http-owner-tests-debug-{}.binlog'
dotnet test TrueMoon.Tales.Studio.slnx --no-restore --artifacts-path .local/studio-build --configuration Release '-bl:.local/http-owner-tests-release-{}.binlog'
```

Проверка Release-exe без аргументов и с неподдержанным endpoint вернула 2 и usage.
Исторический native smoke **не пройден**: сессия `67873a7bab504dca94ca16cd2e68f265`, порт 64290,
завершилась с кодом 1; ui.log содержит `0xC0000005` в `HostedUiWindow.Tick`.
Причина не установлена. Computer Use затем остановлен физическим Esc, повторного
UI-запуска не было. Сессия stopped/ui-exit, владелец и все её процессы завершены;
проект и SHA двух MP4 прежние, POST/Jobs нет. Полная медиаматрица не повторялась.
Позднейшая диагностика и успешные повторы приведены в [NativeCrash.md](NativeCrash.md);
причина исходного AV пока не установлена, исправление не заявляется.

## DI на Cobalt

07.11.2026 Studio использует штатный Cobalt через `App.RunAsync`. Регистрации
собраны в [UseStudioServices](TrueMoon.Tales.Studio/Infrastructure/StudioServices.cs).
Модель окна, стили и типизированные фабрики создаются сгенерированными resolver;
HTTP получает StudioOptions через DI; FFmpeg — через registration factories.
В приложении включён `EnableCobaltGenerator`; matching analyzer приходит из
`TrueMoon.Cobalt 0.2.5.1-alpha`, отдельный пакет generator не требуется.

Cobalt владеет singleton-сервисами, включая `SceneViewModel` и `StudioHttpClient`.
HTTP-сервис владеет транспортом. Окнами и тайтлбарами, созданными
через `IFactory`, владеет UI host; закрытие View не освобождает заимствованную
модель. Тестовый проект использует уже скомпилированный граф приложения.

Debug/Release: `Passed: 90, Failed: 0, Skipped: 1`. Опциональная медиаинтеграция
пропускается без QA endpoint. [Новые тесты](TrueMoon.Tales.Studio.Tests/Infrastructure/StudioServicesTests.cs):

| Требование | Проверка |
|---|---|
| Штатный Cobalt, generated-конструкторы, singleton и start/stop | `Cobalt_ConstructsGeneratedStudioGraphAsSingletons` |
| Разные View/тайтлбары с общей моделью и владение моделью | `Cobalt_ViewFactoriesShareModelAndLeaveItsLifetimeToApp` |
| Sync/async disposal отменяет работу модели и освобождает HTTP | `Cobalt_DisposalCancelsModelWorkAndDisposesOwnedHttpOnce` |

```powershell
dotnet test TrueMoon.Tales.Studio.slnx --no-restore --artifacts-path .local/studio-build --configuration Debug '-bl:.local/cobalt-tests-debug-{}.binlog'
dotnet test TrueMoon.Tales.Studio.slnx --no-restore --artifacts-path .local/studio-build --configuration Release '-bl:.local/cobalt-tests-release-{}.binlog'
```

Полная Release-сборка `--no-incremental -m` прошла с 0 ошибок и двумя CS1591
в generated `CobaltResolversRegistration` / `Init`. Предупреждения не подавляются.
Диагностический `EmitCompilerGeneratedFiles=true` отдельно выявил CS0016 на Windows:
три имени generated-файлов имеют 265/275/372 символа, обычная сборка работает.
Потребности опубликованы как новые заявки владельцу Cobalt:
[ограниченные hint names](../../TrueMoon/src/docs/cobalt/PLAN.md#cob6e-generator-consumer-fixes)
и [XML docs служебной регистрации](../../TrueMoon/src/docs/cobalt/PLAN.md#cob6e-generator-consumer-fixes).

Обновление статуса 09.11.2026: обе заявки завершены владельцем Cobalt и проверены
на независимом локальном потребителе `0.2.5.2-alpha`. Studio пока использует
`0.2.5.2-alpha`; обновление пакета и повторная приёмка выполнены 09.11.2026.
[Decision и границы результата](../Status.md#cobalt-generator-requests) ·
[следующий шаг Studio](../Plan.md#cobalt-generator-requests).
Приведённые выше результаты сборок/ограничения относятся к проверке 07.11.2026;
новых запусков Studio эта документационная правка не добавляет.

Историческая проверка при переходе DI, до переноса HTTP: Release-окно на Cobalt
открыло сцену и FRAME 00; Alt+F4 завершил App
и QA-сервер с кодом 0 (`01e5543800ba450899a1dd663bd2fd92`, порт 54630,
`stopped/ui-exit`). Проект и SHA двух MP4 сохранились, POST/Jobs отсутствуют.
При этом переходе проверены DI и запуск/закрытие; полная медиаматрица из
[Playback.md](Playback.md) повторно не запускалась.

## Пакеты TrueMoon

Текущий локальный набор состоит из двенадцати пакетов **1.2611.22.7-alpha**,
собранных из коммита фреймворка `ce150a4`.
[Проверка текущего набора](../Status.md#studio-truemoon-20261122) ·
[Приёмка предыдущего ENE6e](Verification/FrameworkRequests.md).

Source-режим строит четыре root: TrueMoon, Alloy.Hosting, Enerit и Mithril, затем
упаковывает полный набор. При новой подготовке использовать подтверждённый
checkout/snapshot нужной версии, все build/pack outputs сохранять вне framework:

```powershell
./Update-TrueMoonPackages.ps1 -FrameworkRoot ../../TrueMoon
```

Более новый рабочий checkout не пересобирать под старым номером: скрипт требует
точного совпадения версий и отказывает при уже существующих архивах.
Для передачи готового полного комплекта доступен режим -PackageManifest:
проверяются12 ID/version/SHA и nuspec до импорта; отличающиеся архивы не
перезаписываются. NuGet.Config сохраняет source mapping. Историческая поставка
ENG7B3/1.2611.16.1-alpha и её manifest не являются новым комплектом ENE6e.

### История приёмки 07–09.11.2026


Версия `0.2.5.2-alpha` закреплена общей `StudioTrueMoonVersion` в
[Directory.Packages.props](Directory.Packages.props). Все девять пакетов TrueMoon,
включая транзитивные UI/Core/Cobalt, используют одну версию; SkiaSharp — `4.155.0`.
Приёмка 09.11.2026: набор пересобран из текущего TrueMoon; Debug/Release — по
90 passed, Release-сборка — 0 предупреждений (прежние 2 CS1591 generated-регистрации
закрыты заявками Cobalt), `EmitCompilerGeneratedFiles=true` + `WarningsAsErrors=CS1591`
проходят без ошибок, `Init` с XML summary; типы `SplitView`/`SplitOrientation`
доступны в Argentis. Границы — в [Status](../Status.md#cobalt-generator-requests).
На 07.11.2026 запрошенные пакеты отсутствуют на nuget.org. Используется локальная
сборка текущих исходников движка, а не опубликованный релиз.

Из этого каталога один раз подготовить feed, явно указав подтверждённый корень
исходников TrueMoon. Для данной рабочей области:

```powershell
./Update-TrueMoonPackages.ps1 -FrameworkRoot ../../TrueMoon
```

[Скрипт](../tools/Update-TrueMoonPackages.ps1) сверяет версии Studio и движка, собирает только
необходимые проекты и упаковывает полный набор. Исходники движка читаются на месте;
build/obj, кэш зависимостей, журналы и binlog направлены в `src/.local/truemoon-build/`.
Готовые пакеты и SHA-256 manifest находятся в `src/.local/truemoon-feed/` и не входят
в Git. Существующую версию пакета скрипт не перезаписывает. Для следующего обновления
сначала согласовать новую версию в движке и `Directory.Packages.props` Studio.

[NuGet.Config](NuGet.Config) разрешает `TrueMoon` / `TrueMoon.*` из этого локального
feed; остальные зависимости — с nuget.org. Новый клон требует предварительной
подготовки feed из совместимых исходников либо предоставленного набора пакетов.
При публикации TrueMoon отдельно обновить источник и проверить чистое восстановление.

## Сборка

Из `src`, .NET SDK выбирается по `global.json`:

```powershell
dotnet restore TrueMoon.Tales.Studio.slnx --configfile NuGet.Config --packages .local/studio-packages --artifacts-path .local/studio-build '-bl:.local/studio-restore-{}.binlog'
dotnet build TrueMoon.Tales.Studio.slnx --no-restore --configuration Debug --artifacts-path .local/studio-build '-bl:.local/studio-debug-{}.binlog'
dotnet build TrueMoon.Tales.Studio.slnx --no-restore --configuration Release --artifacts-path .local/studio-build '-bl:.local/studio-release-{}.binlog'
```

07.11.2026 восстановлен новый локальный набор; обе сборки Studio завершились
без предупреждений и ошибок. Проверены единая версия десяти пакетов, доставка
native Skia по SHA-256 и валидация CLI без открытия окна. Последующая проверка
окна и сохранения через HTTP описана в [WindowDesign.md](WindowDesign.md).
