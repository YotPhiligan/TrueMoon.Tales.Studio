# STUDIO-REQUESTS1 · приёмка закрытых заявок TrueMoon

Проверено 17.11.2026 UTC, Windows x64. Studio **1.2611.17.3-alpha**,
TrueMoon **1.2611.17.1-alpha**, SDK11.0.100/net11.0/runtime-async=on.
Версия зафиксирована до финальных сборок, проверок и publish; исходная main —
c78ab18 /1.2611.17.2-alpha (STUDIO-BACKEND3 закоммичен и отправлен).

## Принятый результат

| Заявка | Framework | Studio |
|---|---|---|
| [Cobalt: bounded hint names](../../../TrueMoon/src/docs/cobalt/HISTORY.md#cob6e-generator-2026-11-09) | COB6e completed | Принята 09.11; повторные emit/graph/lifecycle проверки на новом комплекте прошли |
| [Cobalt: XML registration docs](../../../TrueMoon/src/docs/cobalt/HISTORY.md#cob6e-generator-2026-11-09) | COB6e completed | Принята 09.11; strict XML Debug/Release снова прошли |
| [Alloy: регулируемые панели](../../../TrueMoon/src/docs/alloy/HISTORY.md#split-view-2026-11-09) | UIA3e completed | SplitView уже подключён; повторные layout/routed/raster проверки прошли, физическая приёмка отдельно |
| [Enerit: generated XML docs](../../../TrueMoon/src/docs/enerit/HISTORY.md#ene6e-2026-11-17) | ENE6e completed | Matching runtime/generator обновлены; strict XML, initializer и настоящий IPC прошли |

В Directory.Packages.props обновлена общая StudioTrueMoonVersion, все12 пакетов
и bundled Cobalt generator согласованы. NoWarn не добавлен, protocol2, HTTPv1 и
ProjectContractv1 сохранены. Прежние19CS1591 generated-кода исчезли; остаются5CS8669
из nullable context Enerit, который явно исключён из ENE6e и остаётся backlog.

## Источник и локальная поставка

Рабочий TrueMoon развивается параллельно и уже изменил общую версию. Для этой
приёмки использован **зафиксированный коммит ENE6e
22d7f111d70a51e944464085a62a9dca7e115677**, а не текущие незавершённые исходники.
git archive экспортировал только closure необходимых проектов/общие build-файлы
и package assets в изолированный каталог Studio/.local. ZIP comment подтверждает
commit; SHA502 файлов экспортированного набора сохранены/проверены, native SHA прежний.
Рабочий checkout TrueMoon не изменялся; его параллельные изменения не включались.

Скрипт Update-TrueMoonPackages.ps1 исправлен: Source-режим явно строит также
Enerit и Mithril перед pack --no-build. Ранее два build root не покрывали эти
проекты полного12-package списка. Режим импорта и защита immutable архивов сохранены.
Комплект впервые создан в локальном feed; старые пакеты/manifests не переписаны.
Framework owner's исходный6-package proof отдельно проверен по SHA; он не выдаётся
за полный комплект Studio.

Проверены SHA12nupkg, nuspec ID/version и внутренние зависимости, bundled Cobalt/
Enerit generators и фактический project.assets.json нового отдельного cache.
Общая версия пакетов1.2611.17.1-alpha. Их informational build metadata содержит SHA
упаковывающего Studio-host c78ab18; реальный framework source revision22d7f11
зафиксирован отдельно через archive и acceptance manifest. External publication
фреймворка не выполнялась. Это локальная consumer-приёмка.

Extended Skia: 7CAD4A4AA72DFEC19F05C686088BCF38D6585C984E19C4884985FF4C03CA8FCE —
сверены source/package, publish и manifest фактически загруженной библиотеки raster.

## Проверки

| Проверка | Passed | Failed | Skipped |
|---|---:|---:|---:|
| Полный Debug | 370 | 0 | 5 |
| Полный Release | 370 | 0 | 5 |
| Published Release EXE: IPC/writer/raster/UI/CLI | 37 | 0 | 0 |
| Python unittest | 295 | 0 | 0 |

Non-incremental build D/R с GenerateDocumentationFile, EmitCompilerGeneratedFiles
и WarningsAsErrors=CS1591:0errors/5warnings(CS8669). По20 generated files, maximum
basename107characters. Два отрицательных контроля через временный Compile item:
собственный undocumented public class даёт только ожидаемый CS1591 в D/R.
Production-файлы для отрицательного контроля не менялись; последующие финальные
сборки выполнены без injection. Подавлений предупреждений нет.

Полные suites включают Cobalt graph/factory/lifetime, SplitView layout/input,
настоящие owned child Mithril/Enerit, единственный writer, migration/recovery,
revision/SHA conflicts, lost reply без retry и CPU raster UI/CLI. Published
проверки используют настоящий EXE; Python CLI берётся из этой рабочей копии.

PNG Debug/Release совпали87/87, published/Release36/36. Все87Release-кадров совпали
с приёмкой STUDIO-BACKEND3 на прежних пакетах. Manifest подтверждает disposal
root/session, native/font SHA; просмотрен conflict-frame. Это проверка отсутствия
визуальной регрессии на указанной fixture, не принятие универсальных golden images.
Прежние ограничения test fonts и длинных надписей сохраняются.

## Команды и evidence

Из src выполнены следующие фазы PowerShell7 (полные аргументы каждого dotnet
вызова сохранены в commands.ps1; каждый MSBuild вызов имеет отдельный binlog):

```powershell
pwsh -NoProfile -File .local/requests-acceptance-20261117-5a9c9c62/commands.ps1 -Phase Restore
pwsh -NoProfile -File .local/requests-acceptance-20261117-5a9c9c62/commands.ps1 -Phase Negative
pwsh -NoProfile -File .local/requests-acceptance-20261117-5a9c9c62/commands.ps1 -Phase Final
& 'E:/ComfyUI_windows_portable/python_embeded/python.exe' -s -B .local/requests-acceptance-20261117-5a9c9c62/verify_acceptance.py
```

commands.ps1 запускает dotnet restore, strict build D/R, test --no-build D/R,
publish и filtered published consumer, затем Python unittest discover. Runner —
существующий xUnit2/VSTest; STUDIO_TEST_PYTHON задан явно, STUDIO_BACKEND_EXE
выбирает соответствующий Debug/Release/published EXE, STUDIO_RASTER_EVIDENCE —
свежий каталог. Env восстанавливаются в finally. Фазы имеют собственные журналы.

Evidence: src/.local/requests-acceptance-20261117-5a9c9c62/ — acceptance.json,
85 frozen production inputs, source archive/профиль, strict/negative logs,9binlogs,
full-debug.trx/full-release.trx/published.trx, Python log, PNG/manifests, publish,
cleanup.json. Package build/pack logs — .local/truemoon-build/20261117-191831-511/;
SHA manifest12packages — .local/truemoon-feed/TrueMoon.1.2611.17.1-alpha.manifest.json.

## Границы

Все проверки используют синтетические изолированные проекты; авторские данные,
GPU/ComfyUI и native окна не запускались. Проверочные listeners, backend/CLI
процессы завершены; cleanup зафиксирован отдельно. Пять прежних skips сохранены,
они перечислены в acceptance.json и не считаются успешной native/IO приёмкой.
Physical Escape/keyboard, Windows DPI, визуальная тема в настоящем окне, long
timeline, прежний native AV и media/trim/export/CPU MP4 parity остаются открытыми.
Заявки владельцев не переписаны и новые дубли не создавались.
