# ProjectStore: запись, миграция и восстановление

10.11.2026. `TrueMoon.Tales.Studio.Persistence` — DLL над
[контрактами проекта](ProjectContracts.md), без нового EXE и сторонних пакетов.
Текущий Python UI/API не переключён на это хранилище.

16.11.2026: [STUDIO-BACKEND1](Verification/BackendReadOnly.md) подключает только
OpenAsync через Enerit к backend того же EXE. Запись, миграция и recovery через IPC
ещё не включены; write grants этого режима пусты.

17.11.2026: [STUDIO-BACKEND2](Verification/BackendMove.md) добавляет явный writer
режим с AcquireWriter/MoveClip/MigrateProject/RecoverProject через Enerit.
ProjectWriterLease удерживается backend-сессией; Python commit использует тот же
OS lease, а schema 2 отклоняет старый Python-черновик после освобождения lease.
Read-only режим и OpenAsync по-прежнему не выполняют миграцию или recovery.

18.11.2026: [этап 2Б](../Plan.md#studio-project-create) добавляет создание
schema2. `CreateChildAsync` принимает разрешённый родительский каталог через
`ProjectStoreOptions`, записывает полный JSON в отдельный соседний каталог
и одним переносом публикует новый каталог. Имя каталога валидируется,
занятый путь отклоняется, прерванная подготовка убирает свои файлы.
Начальная ревизия 1 предшествует первой записи истории. Новые документы
содержат `schema_version` и `project`, а снимок `legacy_document` добавляется
при миграции schema1.

Стартовый экран считывает ID и SHA ожидающего журнала через
`ReadRecoveryIdentityAsync`. `RecoverObservedAsync` сравнивает SHA после
получения lease и продолжает прежнюю валидацию журнала и файлов.

## Формат и адаптер записи

`ProjectFileCodec` читает UTF8 JSON с BOM или без него:

- `schema_version=1` адаптируется через LegacyProjectReader без записи;
- `schema_version=2` хранит `project` с `contract_version=1`, полный исходный
  `legacy_document` при миграции и аудит `last_write`;
- неизвестные версии отклоняются. Транспортный DTO не записывается поверх
  старого файла как будто он имеет прежнюю схему.

`SerializeMigration` — явное преобразование в памяти. `Serialize` работает
только со схемой 2. Ревизию при реальной записи меняет ProjectStore.

Неизвестные поля старого проекта целиком сохраняются в `legacy_document`.
Неизвестные поля нового envelope и вложенных сущностей сохраняются на своих
местах; при перестановке сущностей они сопоставляются по стабильным `id` или
`source_id`. Отсутствующие ключи объектов сохраняются, явный JSON null сбрасывает
значение. Состав массивов задаёт новый снимок; элементы без стабильных ID
рассматриваются как целое значение. Архив исходного legacy не перезаписывается.

## API и права

`ProjectStoreOptions` принимает доверенный физический `ProjectDirectory`,
явные каталоги `AllowedWriteRoots` и запрещённые пути `ProtectedRoots`.
Пустой список записи означает read-only. Пути из содержимого проекта не дают
прав и не используются как адреса файловых операций.

```csharp
var store = new ProjectStore(new ProjectStoreOptions
{
    ProjectDirectory = approvedCopyDirectory,
    AllowedWriteRoots = [approvedCopyDirectory],
    ProtectedRoots = protectedWorkspacePaths
});
var original = await store.OpenAsync(cancellationToken);
var migrated = await store.MigrateAsync(original, original.Project.Revision, "author", cancellationToken);
var draft = migrated.Project with { Name = "Проверочная копия" };
var saved = await store.SaveAsync(migrated, draft, migrated.Project.Revision, "author", cancellationToken);
```

Каталоги должны поступать из доверенной конфигурации. Файловые разрешения
существующей Studio.config нельзя автоматически расширять до разрешения всего
каталога. Адаптер чтения backend/IPC выполнен; выдача прав записи ещё предстоит.
Store ограничивает операции выбранным корнем, проверяет allowlist, protected
roots и reparse points. Символические ссылки/junction не разрешаются.

Открывается ровно один файл: `Studio.project.json` в корне либо прежний
`Production/Studio.project.json`. Наличие обоих считается конфликтом.
Миграция версии не перемещает файл между этими расположениями.
`OpenAsync` не создаёт файлы, не мигрирует и не восстанавливает транзакции.

## Сохранение и история

- `MigrateAsync` отдельно переводит схему 1 в 2; обычный Save старого документа
  возвращает ProjectMigrationRequiredException.
- Базовый документ должен быть открыт из той же физической локации. Проверяются
  `expected_revision`, ревизия черновика и SHA256 точных исходных байтов.
  Изменение содержимого без смены ревизии тоже вызывает конфликт.
- При успехе ревизия увеличивается ровно на один. ID проекта менять нельзя;
  ссылки нового состояния проходят структурную проверку контрактов.
- `RestoreSnapshotAsync` принимает исторический снимок schema2 того же
  проекта и физического файла. Восстанавливает его содержимое вместе с
  непрозрачными полями, назначает следующую ревизию и фиксирует операцию
  `restore`. Текущий файл сохраняется в истории тем же путём commit.
  Команды undo/redo используют этот метод через одного writer.
- Предыдущие точные байты, включая BOM и форматирование, сохраняются новым файлом
  `Production/History/r<revision>-<transaction>.json`. Store не перезаписывает
  существующие исторические снимки. В `last_write` записываются actor, UTC-время,
  операция, ревизия и ID транзакции.
- Между сотрудничающими экземплярами действует файловый lease
  `.Studio.project.lock`. Оставшийся файл не означает владение: оно определяется
  открытым эксклюзивным handle. Store не держит фоновых задач или процессов.

Переход реального проекта требует остановить прежнего владельца записи.
Python-сервис ещё не использует новый lease; его одновременная запись вместе
с C# не поддерживается. Авторские файлы на этом этапе не мигрировались.

## Commit и явное recovery

Данные проекта, снимок истории и журнал сначала записываются во временные файлы
с flush. Журнал `.Studio.project.transaction.json` публикуется отдельным rename;
это граница принятого commit. До неё отмена/ошибка очищает созданные Store
pending-файлы и сохраняет прежний проект. После неё операция завершается либо
оставляет данные для явного восстановления, включая случай отмены IPC-запроса.

История публикуется без перезаписи. Проект заменяется через File.Replace из
соседнего файла; fallback с обнулением или копированием поверх проекта отсутствует.
Чтение при наличии журнала возвращает ProjectRecoveryRequiredException.

`RecoverAsync` получает lease и сверяет SHA текущего файла, pending-данных и
истории. Если остался прежний файл — завершает принятую запись. Если новый уже
установлен — завершает очистку без повторной ревизии/истории. Повторное recovery
не создаёт новую запись. Внешнее изменение или подмена доказательств сохраняют
файлы и журнал для разбора; произвольные пути из журнала не принимаются.

Проверки моделируют прерывание commit исключением IO до и после замены файла.
Принудительное завершение процесса, аппаратное обесточивание и семантика
сетевых файловых систем не проверялись.

## Проверки

10.11.2026: 34 синтетические проверки прошли, проверка symbolic links пропущена
из-за отсутствия привилегии Windows (1314). Изолированная копия существующего
проекта проверена отдельно: 1 passed. Итоговые Debug/Release — по 190 passed,
0 failed, 4 skipped: две аппаратные интеграции, symbolic links и opt-in тест копии,
который в обычном запуске не включён. Полная пересборка Release — 0 ошибок,
0 предупреждений.

Копия Клэр прошла schema 1 → 2 и ревизии 55 → 56 → 57; сохранены 55 источников,
62 дубля и 2 окна. В исходнике нет действий/клипов: A–B–A и сохранность их связей
подтверждены синтетическим проектом. Проверены два точных снимка истории,
неизменность seed и SHA авторского исходника. Только копия JSON изменялась;
медиа не читались и не копировались.
Артефакты: `.local/store-copy-d082c1c41aee4de9be5bf4a435aade52/`,
`store-acceptance.json`, seed, результат и история.

Тесты используют собственные временные каталоги и синтетические проекты:
миграцию обоих расположений, A–B–A, точные исторические байты, неизвестные поля
при перестановке, конфликты ревизий/SHA, чужой baseline, allowlist/protected roots,
отмену, lease и сбои до/после атомарной замены. Символическая ссылка требует
отдельной привилегии Windows; отсутствие права отмечается явным skip.

| Требование | Тест |
|---|---|
| Явная миграция, ID и точная история | `Migrate_BothFileLayouts_PreserveExactLegacyBytesIdsAndAudit` |
| Несколько источников/клипов, сохранение и открытие | `Save_ReopenMultiSourceAction_PreservesOpaqueMetadataAndImmutableHistory` |
| Неизвестные поля по ID | `Serialize_ReorderedEntities_PreservesUnknownFieldsByStableIdentity` |
| expected_revision и fingerprint | `Save_StaleRevision_DoesNotOverwriteProjectOrHistory`, `Save_SameRevisionFingerprintTamper_DoesNotOverwriteExternalEdit` |
| Атомарная замена и recovery | `ReplacementFailure_RequiresExplicitRecoveryAndRollsForwardOnce`, `InstalledReplacementThenFailure_RecoveryDoesNotRepeatRevisionOrHistory` |
| Изолированная копия зарегистрированного проекта | `Copy_OpenMigrateSaveAndReopen_PreservesIdsLinksAndExactHistory` |

Команды итоговых проверок из `src`:

```powershell
dotnet test TrueMoon.Tales.Studio.Tests/TrueMoon.Tales.Studio.Tests.csproj --no-restore --artifacts-path .local/studio-build --configuration Debug --filter 'FullyQualifiedName~TrueMoon.Tales.Studio.Tests.Persistence' '-bl:.local/store-tests-final-{}.binlog'
dotnet test TrueMoon.Tales.Studio.slnx --no-restore --artifacts-path .local/studio-build --configuration Debug '-bl:.local/store-final-debug-{}.binlog'
dotnet test TrueMoon.Tales.Studio.slnx --no-restore --artifacts-path .local/studio-build --configuration Release '-bl:.local/store-final-release-{}.binlog'
```

Opt-in проверка требует свежей пары `src/.local/store-copy-<32hex>/original/` и
`project/` с побайтовыми копиями одного legacy JSON. В процесс теста передаётся
`STUDIO_PROJECT_STORE_COPY` с абсолютным путём только к `project/`; guard проверяет
расположение внутри исходников Studio. Тест не читает авторский путь из manifest
и оставляет отчёт с ревизиями/числами/SHA без творческого содержимого.
Отдельно прошедшая команда с таким process-local окружением:

```powershell
dotnet test TrueMoon.Tales.Studio.Tests/TrueMoon.Tales.Studio.Tests.csproj --no-restore --artifacts-path .local/studio-build --configuration Debug --filter 'FullyQualifiedName~TrueMoon.Tales.Studio.Tests.Persistence.ProjectStoreCopyIntegrationTests' '-bl:.local/store-copy-final-{}.binlog'
```
