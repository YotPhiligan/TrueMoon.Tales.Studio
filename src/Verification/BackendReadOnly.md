# STUDIO-BACKEND1 — чтение проекта через owned backend

16.11.2026. Studio **1.2611.16.3-alpha**, SDK 11.0.100 / net11.0 /
runtime-async=on. Версия выбрана по UTC после сверки рабочей копии и удалённого
main=3fb891a; TrueMoon остаётся **1.2611.16.1-alpha**. Добавлены Enerit,
Enerit.Generator (PrivateAssets=all), Mithril из той же ENG7B3-поставки.
Update-TrueMoonPackages.ps1 проверяет SHA/nuspec всех 12 архивов и сохраняет
отдельный immutable studio-backend manifest, не переписывая прежний manifest.

## Запуск и контракт

Из src после сборки Release:

```powershell
./.local/studio-build/bin/TrueMoon.Tales.Studio/release/TrueMoon.Tales.Studio.exe --project <физический-каталог-проекта> --config ../Studio.config.local.json
./.local/studio-build/bin/TrueMoon.Tales.Studio/release/TrueMoon.Tales.Studio.exe --inspect-project <физический-каталог-проекта> --config ../Studio.config.local.json
```

Первый режим открывает отдельное read-only окно: название, ревизия, схема,
состав источников/дублей/монтажа и сценарные сегменты; кнопка перечитывает проект.
Второй выводит JSON снимка и закрывает принадлежащий ему backend. Python-сервер
для этих режимов не нужен. Существующий HTTP-запуск сцены остаётся отдельным
рабочим режимом с прежними trim/move и владением HTTP-клиентом.

Один поставляемый EXE исполняет обе роли. Parent использует App/AddUnit и
ChildProcessUnitHandle Mithril; служебный `--backend` без окна принимается только
с unit=1, parent и control pipe. Фреймворк передаёт защищённый bootstrap snapshot,
наблюдает смерть владельца, останавливает и reap-ит только свой процесс.
Автоматический restart отключён. Диагностика Started/ProcessReleased/Stopped
выводит PID, но не per-launch capability или endpoint.

Enerit генерирует клиент/handler IStudioBackend. До чтения проверяются роль,
protocol=1, project contract=1, точная версия продукта, parent PID и принадлежность
serving PID текущему handle Mithril. У запуска отдельные случайные endpoint и
capability. Hello допускает ограниченное ожидание готовности; общий бюджет открытия
12 s, бюджет транспортного вызова 3 s, graceful stop Mithril 3 s с kill/reap при
необходимости. Пользовательская отмена сокращает ожидание. Сам OpenProject не
повторяется автоматически. После разрыва следующий явный read читает файл заново.

BackendProjectReply содержит schema, SHA256 и JSON нормализованного ProjectContract,
а не storage envelope/legacy_document. DTO ответа mutable по требованиям generator;
неизменяемые доменные records кодируются существующим ProjectContractJson.
Лимит JSON — 768 KiB в UTF-16, оставляющий запас до 1 MiB frame Enerit. Превышение
возвращает project_too_large без усечения. Кадры, звук и файлы медиа не передаются.

## Права и границы

Физический каталог и config выбираются при старте, IPC не принимает пути.
Каталог должен быть внутри amv_root/projects_root; protected_roots и отключённый
legacy_root запрещены. Относительные корни считаются от config; UTF-8 BOM допустим.
Сохраняется автоматическое исключение соседнего TrueMoon при запуске из Studio.
ProjectStore дополнительно запрещает reparse points. AllowedWriteRoots всегда пуст.
project_write_roots не превращаются в разрешение записи каталога.

Это узкий адаптер чтения физического проекта, не замена полного Python resolver:
aliases, реестр, выбор медиа и выдача write grants остаются следующим этапом.
Открытие schema 1/2 не мигрирует, не создаёт lock/history и не восстанавливает journal.
recovery_required, invalid_project, project_not_found, access_denied и IO failure
возвращаются явно. При ошибке UI сохраняет предыдущий снимок с отметкой и предлагает
перечитать; черновиков редактирования в новом режиме нет. Запись и смена её владельца
не подключены; Python API сохраняет schema 1.

## Проверки и evidence

Новые backend-проверки: **39 passed, 0 failed, 0 skipped**. Полные Debug/Release:
**286 passed, 0 failed, 5 skipped** в каждом. Пять прежних skips: native/device
интеграции, копия ProjectStore opt-in, symbolic links без привилегии Windows и
отдельная opt-in HTTP move. Процессные проверки повторены на publish: **7 passed**.
Полные non-incremental D/R builds: 0 errors, по **20 warnings** только из Enerit
generated-кода: 15 CS1591, 5 CS8669. Подавления не добавлены. XML docs направлены
[владельцу Enerit](../../../TrueMoon/src/docs/requests/enerit/20261116-studio-generated-xml-docs-f2ad173c/Request.md);
nullable-context уже находится в [quality backlog](../../../TrueMoon/src/docs/enerit/PLAN.md).

Изолированная прежняя копия Клэр открыта реальным EXE через IPC: schema 1,
revision=55, 55 sources, 62 takes, 2 windows; SHA256 до/после совпал. Медиа и
авторский проект не читались/не менялись. Копия не содержит действий/клипов;
их связи проверены синтетическими проектами.

QA lifecycle: 13 Python checks passed, включая backend-ui argv и Job Object.
Native QA session `0abc7b377d504d60a4eaaebc11a5662c` создала окно и backend, но
Computer Use не смог активировать его: `failed to activate captured window`;
снимок не показывал содержимое. Визуальная приёмка не заявляется. Сессия остановлена
по ID, tool session 43000 завершена, Job Object подтвердил отсутствие потомков.
Расположение видимого текста и кнопка перечитывания проверены через UiSession и
routed input; аппаратные DPI/клавиатура и визуальная native-приёмка остаются открытыми.

| Требование | Тест |
|---|---|
| backend-роль того же EXE | BackendRole_RequiresOwnedMithrilIdentityAndValidEndpoint; BackendRole_RejectsUnmanagedLaunchWithoutCreatingFiles |
| управляемый запуск через Mithril и открытие проекта через Enerit | SameExe_MithrilLaunchesBackend_EneritOpensProject_AndShutdownReapsChild |
| Ошибка старта/чтения и cleanup | FailedStartupOrRead_ReturnsFailure_AndReapsOnlyOwnedChild |
| Падение владельца, чужой одноимённый процесс и reconnect с новой ревизией | ParentExit_BackendObservesBorrowedOwnerAndExitsWithoutTouchingProject |
| Deadline и отмена | Open_UnresponsiveBackendHonorsDeadlineAndCallerCancellation_WithoutOpeningProject |
| Read-only schema 1/2, bytes/history/ID/timing | Open_Schema1And2_PreservesBytesRevisionHistoryAndLinks |
| Политика путей и запрет записи | Access_RejectsOutsideProtectedLegacyAndPrefixSiblingRoots; Access_DoesNotConvertExistingWriteGrantsIntoBackendWritePermission |
| Отказы роли/версии/владельца | Handshake_RejectsWrongRoleVersionParentAndUnownedProcess |
| Ограничение UTF-16 payload | Open_Utf16PayloadBoundary_AcceptsLimitAndRejectsNextCharacterWithoutWriting |
| Окно и перечитывание | Cobalt_CreatesReadOnlyWindow_AndFrameAppliesSnapshotAndRoutedReload |

Итоговые команды из src (логгеры и binlog в evidence):

```powershell
dotnet build TrueMoon.Tales.Studio.slnx --no-restore --no-incremental --configuration Debug --artifacts-path .local/studio-build '-bl:.local/backend-final-debug-build-{}.binlog'
dotnet build TrueMoon.Tales.Studio.slnx --no-restore --no-incremental --configuration Release --artifacts-path .local/studio-build '-bl:.local/backend-final-release-build-{}.binlog'
dotnet test TrueMoon.Tales.Studio.slnx --no-build --no-restore --configuration Debug --artifacts-path .local/studio-build '-bl:.local/backend-final-debug-tests-{}.binlog'
dotnet test TrueMoon.Tales.Studio.slnx --no-build --no-restore --configuration Release --artifacts-path .local/studio-build '-bl:.local/backend-final-release-tests-{}.binlog'
dotnet publish TrueMoon.Tales.Studio/TrueMoon.Tales.Studio.csproj --no-restore --configuration Release --artifacts-path .local/studio-build --output .local/backend-1.2611.16.3-alpha/publish '-bl:.local/backend-final-publish-{}.binlog'
```

Артефакты: `.local/backend-1.2611.16.3-alpha/`: TRX, журналы, publish,
copy-acceptance.json и acceptance.json. Промежуточные неуспешные прогоны исправлены;
passing заявляется только по финальным отчётам. GPU/ComfyUI не запускались.
