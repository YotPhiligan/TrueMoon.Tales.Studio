# C#-контракты проекта и чтение старого формата

Первый этап реализации [модели проекта](../ProjectModel.md), 10.11.2026.
`TrueMoon.Tales.Studio.Contracts` — библиотека DLL без сторонних зависимостей.
UI и будущий backend-экземпляр одного EXE могут использовать общий контракт;
отдельная исполняемая программа не добавлена.

## Контракт снимка

Пространство имён DTO — `TrueMoon.Tales.Studio.Contracts.Projects`.

| Тип | Содержание |
|---|---|
| `ProjectContract` | `contract_version=1`, ID, ревизия, fps, сценарные сегменты, источники и сохранённый монтаж |
| `ScenarioSegmentContract` / `StoryActionContract` | Смысловая структура, плановое окно, порядок действий, ссылки на источники и авторские заметки |
| `SourceContract` / `TakeContract` | Библиотечные источники и варианты; Properties сохраняет настройки, промпт, референсы и остальные данные |
| `MontageContract` / `SegmentWindowContract` / `ClipContract` | Сохранённые окна и клипы с отдельными ID, связью с действием, источником, дублем и временными параметрами |

Действие допускает пустой список источников или несколько ссылок. Один источник
может использоваться несколькими действиями; несколько клипов могут ссылаться на
одно действие, в том числе A → B → другой участок A. Идентичность действия и
клипа независима. ID дубля рассматривается вместе с ID его источника.

`ProjectContractJson` из `.Contracts.Serialization` кодирует снимок для транспорта
с именами snake_case. Проверяются версия, уникальность ID внутри соответствующих
видов сущностей, ссылки и числовая структура. Неизвестное плановое время допустимо;
сценарный бюджет не подгоняется под последний допустимый монтаж.

## Адаптер schema_version=1

`LegacyProjectReader` из `.Contracts.Compatibility` принимает строку JSON или
`JsonElement`. Это чистая операция в памяти: пути не разрешаются, файлы не
открываются, задания и генерация не запускаются.

```csharp
var adapted = LegacyProjectReader.Read(json);
var snapshot = adapted.Project;
var transportJson = ProjectContractJson.Serialize(snapshot);
var received = ProjectContractJson.Deserialize(transportJson);
```

- `project.blocks[]` становится библиотекой источников; `block_id` сохраняет
  значение ID источника. Старые `items[].id` и `clips[].id` сохраняются, даже если
  совпадают; равенство не требуется для новых контрактов.
- При активном `timeline.scenario` сценарные намерения читаются отдельно от
  сохранённых `timeline.segments/clips`. Неизвестное время и переполнение намерения
  не заменяют последний допустимый монтаж пересчитанной раскладкой.
- Без активного сценария действия выводятся из размещённых клипов по времени
  внутри каждого окна с сохранением пауз. Порядок сохранённых монтажных клипов
  не меняется. Верхнеуровневые производственные `project.segments` не становятся
  сценарными сегментами; проект без timeline даёт пустой сценарий и монтаж.
- Если черновик уже убрал/перенёс действие или сменил его источник, старый клип
  сохраняется с `ActionId=null` и issue `unlinked_clip_action`. У оставшегося
  монтажного окна без сценарного сегмента сохраняется ID, `SegmentId=null` и
  issue `unlinked_window_segment`. Сценарные сущности для них не выдумываются.
- Неизвестные версии и некорректные структуры/ссылки отклоняются. Возможные
  конфликты сценарного времени остаются намерениями, а не ошибкой чтения JSON.

`LegacyProjectReadResult.OriginalDocument` — независимый от входного JsonDocument
клон всего исходного JSON: неизвестные поля, assets, пути, история, генерационные
снимки и неизложенные в DTO свойства сохраняются. Сохранение оригинала не означает
применение к нему изменений `ProjectContract`.

## Границы этапа

`contract_version` описывает новый DTO, `schema_version` — существующий файл.
JSON транспорта нельзя записывать поверх `Studio.project.json`.
[Адаптер записи, явная миграция и ProjectStore](ProjectStore.md) реализованы
отдельной DLL Persistence; библиотека контрактов остаётся независимой от IO.
Текущий UI/API и авторские проекты продолжают работать в прежнем формате.
Монтажные команды split/переноса и раскрываемые группы действий не менялись.

Проверки используют только синтетический JSON в памяти: новый монтаж A–B–A,
повторное использование источника, текст без материала, старые проекты с/без
сценария и timeline, конфликтный черновик, отсоединённые сохранённые связи,
точность исходного JSON и ошибки версий/ссылок. Команды и итоговые результаты
этапа указаны ниже и в [Status.md](../Status.md).

## Проверки

При завершении этапа контрактов 10.11.2026: узкий набор — 60 passed, 0 failed; полное решение в Debug/Release —
по 156 passed, 0 failed, 2 skipped. Пропущены опциональные native и FFmpeg/audio
интеграции. Полная пересборка Release — 0 ошибок, 0 предупреждений.
Более поздние результаты этапа хранения — в [ProjectStore.md](ProjectStore.md#проверки).

| Требование | Свидетельство |
|---|---|
| Контракты действий, источников и клипов | `Json_RoundTrip_PreservesMultipleSourcesReusableBindingsAndIndependentClipIds`, `Json_TextAction_PreservesUnknownPlanWithoutMaterialOrMontage` |
| Чтение текущего формата | `Read_ActiveScenario_KeepsIntendedActionAndSavedMontageIndependent`, `Read_AbsentScenario_AdoptsOnlyPlacedClipsInTimeOrder`, `Read_MissingTimeline_KeepsProductionGroupsOutOfScenario` |
| Сохранность исходных данных | `Read_OriginalDocument_PreservesUnknownHistoryAndPathsAfterInputIsDisposed` |
| Разрыв связей последнего допустимого монтажа | `Read_StaleMontageAction_IsDetachedWithDiagnostic`, `Read_StaleMontageWindow_PreservesWindowWithoutInventingStorySegment` |

Из `src`, после восстановления зависимостей:

```powershell
dotnet test TrueMoon.Tales.Studio.Tests/TrueMoon.Tales.Studio.Tests.csproj --no-restore --artifacts-path .local/studio-build --configuration Debug --filter 'FullyQualifiedName~TrueMoon.Tales.Studio.Tests.Contracts' '-bl:.local/contracts-tests-final-{}.binlog'
dotnet test TrueMoon.Tales.Studio.slnx --no-restore --artifacts-path .local/studio-build --configuration Debug '-bl:.local/contracts-final-debug-{}.binlog'
dotnet test TrueMoon.Tales.Studio.slnx --no-restore --artifacts-path .local/studio-build --configuration Release '-bl:.local/contracts-final-release-{}.binlog'
```
