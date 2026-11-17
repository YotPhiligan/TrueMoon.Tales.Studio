# Проверки UI Studio без Computer Use

Принято 16.11.2026. Общая инструкция Alloy/Argentis находится в
[TrueMoon: UI_TESTING.md](../../TrueMoon/src/docs/alloy/UI_TESTING.md).
Этот документ задаёт прикладные сценарии Studio и сохраняет минимальные правила
для отдельного клона, в котором исходники фреймворка недоступны.

## Как выбирать проверку

По умолчанию проверять UI через настоящее дерево Argentis и UiSession, подавая
UiInput программно. Для изображения использовать CPU raster Skia с фиксированными
шрифтами. Computer Use нужен только когда задача требует взаимодействия с внешним
приложением или ручного исследования; он не является обязательным условием UI-тестов.
Явно запрошенную пользователем проверку настоящего окна выполнять отдельно.

| Уровень | Средства | Что подтверждает |
|---|---|---|
| Поведение | UiSession, настоящий View из Cobalt/IFactory, тестовые сервисы | Layout, focus/capture, маршрутизация ввода, команды и состояние UI |
| Изображение | SkiaRasterRenderBackend, RasterUiTarget, SnapshotRasterImage, фиксированные fonts | Реальную растеризацию текста/контролов и кадры для сравнения |
| Окружение окна | Тестовый IWindowController, Window.AttachHost и Synchronize; UiInput для ввода | Реакции UI на опубликованные состояния хоста, resize и закрытие |
| Сквозной сценарий | Тестовый UI-host и настоящий backend по Enerit | Открытие/команды/ошибки и владение реальными процессами/хранилищем |
| Native | Собственное управляемое окно, поддержанный OpenGL/Vulkan backend | Создание, фактический present/resize/teardown на указанной конфигурации |

Тестовый host реализуется в fixture приложения; готовый виртуальный SilkWindowHost
этой инструкцией не объявляется. HostedUiWindow в проверенной реализации создаёт
SilkWindowHost напрямую. Замена поверхности или добавление RasterUiTarget не
заменяет native event loop. Синтетический UiInput не доказывает доставку Win32-ввода.

## Сценарии Studio

1. Получить свежий View через ту же Cobalt-регистрацию/IFactory, которую использует
   приложение. Подключить его к UiSession на выделенном UI owner thread. Не запускать
   native presentation из общего App-графа при проверке только дерева.
2. Подготовить начальное состояние и вызвать Update. Ввод направлять через
   HandleInput, а не прямо в обработчик кнопки. Координаты получать из проверенной
   геометрии нужного контрола; после resize заново проверить layout.
3. Для drag выполнить PointerDown → PointerMove → PointerUp. Для отмены —
   PointerDown → PointerMove → KeyDown(Escape) → PointerUp. Потерю фокуса и захвата
   проверять отдельными сценариями: они не подменяют доказательство Escape.
4. Проверить UI и данные: локальный черновик во время drag, отсутствие записи до
   отпускания, одну команду с исходным expected_revision, IDs/связи/тайминги,
   revision/history, no-op и отмену без записи. При конфликте сохранить черновик;
   повторная отправка без перечитывания недопустима.
5. Для backend-окна проверить loading/loaded/error, перечитывание, предыдущий снимок
   после ошибки, отмену ожидания и отсутствие команд записи. В сквозном тесте
   сохранить настоящий Enerit/Mithril/ProjectStore и использовать изолированный
   проект; подменять только необходимое окружение UI.
6. Для raster-кадров задать ожидаемое состояние, фиксированные font snapshots и
   версии native/пакетов. В матрице менять размер и scale, проверять кириллицу,
   длинные названия, пустые списки, ошибки и доступность действий. Сначала проверять
   layout/состояние, затем PNG. Логический scale не равен физическому Windows DPI.

Async-результаты применять на owner thread через штатный механизм ViewModel/Post/
InvokeAsync и очередной Update. Ожидать наблюдаемое условие с дедлайном; произвольный
sleep не доказывает готовность. Session владеет принятым деревом и поверхностью,
View заимствует App-owned ViewModel; snapshot изображения освобождает вызывающий.
Очищать ресурсы и процессы также при failed startup, timeout и отмене.

## Что уже есть и что ещё нужно сделать

Состояние на 17.11.2026; результаты нового raster-прогона — в
[STUDIO-UIQA1](Verification/UIRaster.md):

- [ProjectWindowTests](TrueMoon.Tales.Studio.Tests/Backend/ProjectWindowTests.cs):
  Cobalt создаёт окно, UiSession применяет снимок, routed click перечитывает проект.
- [TimelineMoveInteractionTests](TrueMoon.Tales.Studio.Tests/Presentation/Scene/TimelineMoveInteractionTests.cs):
  черновик, Escape/другие причины отмены, одна запись, no-op и конфликт.
- [SplitViewLayoutTests](TrueMoon.Tales.Studio.Tests/Presentation/Shell/SplitViewLayoutTests.cs):
  расположение и изменение панелей через routed input.
- [BackendProcessIntegrationTests](TrueMoon.Tales.Studio.Tests/Backend/BackendProcessIntegrationTests.cs):
  настоящий EXE/Mithril/Enerit, cleanup, смерть владельца и переподключение.
- [HeadlessSurface](TrueMoon.Tales.Studio.Tests/Support/InteractionFixture.cs)
  сейчас не рисует и приближённо измеряет текст. Его проверки не являются pixel/
  font acceptance.
- [RasterWindow](TrueMoon.Tales.Studio.Tests/Support/RasterWindow.cs): тестовый
  IWindowController, настоящий UiSession/CPU Skia, AttachHost/Synchronize, resize,
  focus, minimize/restore, Application-close с async veto/error/cancellation,
  PNG и manifest с версиями, font/native SHA и cleanup. Это ограниченный тестовый
  контроллер; native event loop, Shutdown и геометрия OS maximize не эмулируются.
- [RasterSceneTests](TrueMoon.Tales.Studio.Tests/Raster/RasterSceneTests.cs) и
  [RasterProjectWindowTests](TrueMoon.Tales.Studio.Tests/Raster/RasterProjectWindowTests.cs):
  production-фабрики Cobalt, масштабы 1/1.5/2, routed drag/Escape/focus/resize,
  одна команда/конфликт и loading/error/reload. Ответы HTTP и project reader —
  управляемые doubles. Настоящее соединение добавлено отдельно в STUDIO-BACKEND2 ниже.
- [RasterHostTests](TrueMoon.Tales.Studio.Tests/Raster/RasterHostTests.cs):
  lifecycle, failed startup, освобождение дерева/поверхности, отмена close wait,
  запрет перезаписи PNG и кириллица в фиксированном шрифте.
- [BackendRasterIpcTests](TrueMoon.Tales.Studio.Tests/Raster/BackendRasterIpcTests.cs) и
  [BackendLostReplyTests](TrueMoon.Tales.Studio.Tests/Raster/BackendLostReplyTests.cs):
  настоящий EXE через Mithril, Enerit и ProjectStore вместе с production SceneView
  и CPU raster; Escape, одна запись, конфликт точных байтов и явное перечитывание.
  Потеря ответа инъецируется после настоящего commit; остальная цепочка настоящая.
  [Матрица, запуск и evidence](Verification/BackendMove.md).
- [BackendUiCliTests](TrueMoon.Tales.Studio.Tests/Raster/BackendUiCliTests.cs):
  настоящий Python CLI через HTTP/Kestrel в сессии production SceneView и того же
  owned Enerit backend. Проверяются общий writer, CLI commit во время drag,
  конфликтный draft, Cancel/Reload, последующий UI commit и чтение/no-op CLI.
  [STUDIO-BACKEND3](Verification/BackendHttp.md); STUDIO_TEST_PYTHON включает эти checks.

Переиспользовать RasterWindow и RasterSceneFixture; запуск и evidence описаны в
[отчёте](Verification/UIRaster.md#запуск). Не добавлять новый тестовый режим в
поставляемый EXE только ради обхода фабрик; сначала использовать публичные seams.
В процессном тесте явно указать consumer EXE: Mithril ChildProcess по умолчанию
запускает текущий host, которым у теста может оказаться testhost.

## Evidence и native-проверки

Фиксировать сценарий, ожидаемое/наблюдаемое состояние, версии SDK/TFM/Features и
пакетов, размер/scale, font/native SHA, backend, PNG/diff и результат cleanup.
Эталон не перезаписывать в обычном прогоне. Правила допуска выбирать по сцене и
сверять с [общим golden guide](../../TrueMoon/src/docs/alloy/GOLDEN_TESTS.md).
Отчёт различает headless/raster, собственное native-окно и ручной физический ввод.

Настоящие окна запускать по [QASessions.md](QASessions.md): registry, deadline,
владелец, Job Object и остановка своих сессий по ID. Прямой бесконтрольный запуск
окна/сервера запрещён. Computer Use не обязателен для собственного instrumented
native harness, однако routed input внутри него не закрывает физическую клавиатуру,
OS capture, IME, переход DPI между мониторами или поведение compositor/драйвера.

Матрица preview/CPU MP4 остаётся самостоятельной проверкой монтажа; UI PNG не
доказывает соответствие экспортируемого видео. Авторские проекты и GPU/ComfyUI
не использовать для упрощения fixture.

Текущие результаты этапов: [raster](Verification/UIRaster.md),
[backend](Verification/BackendReadOnly.md) и [перенос клипа](Verification/ClipMove.md).
Команды брать из README/отчёта текущей поставки. Версия 1.2611.16.3-alpha сохраняется:
STUDIO-UIQA1 добавляет только тесты/тестовые assets и документацию.
