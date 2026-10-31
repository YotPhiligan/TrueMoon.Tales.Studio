# UI-04F — титры / Studio 0.14

31.10.2026. Реализованы кириллические титры поверх видео и на чёрном фоне: время, шрифты, оформление, плавность, отдельная дорожка, сохранение/undo и MP4. [Инструкция](../Titles.md).

Проверка выполнена на отдельном проекте `.local/titles-qa-20261031-r1`, собственный сервер 8801. Рабочий сервер 8787 не затрагивался, GPU/ComfyUI не запускались. Авторский Studio.project.json Клэр проверен по SHA: сохранён.

## UI → PNG → MP4

Через интерфейс созданы три титра: `Клэр / Сумеречный двор` 0,5–2,5 с, Arial 52 px, нижнее положение поверх видео; `Сказки истинной луны / Nero — Satisfy` 4–5 с, Georgia 52 px, чёрный фон; `Продолжение следует` 7–8 с, чёрный хвост. У всех появление и исчезновение по 0,25 с. Общая длина выросла с 7 до 8 с без сдвига монтажа.

Проверены live style preview, кириллические переносы, смена шрифта/положения/фона, редактирование с дорожки, блокировка fade 0,8+0,8 с внутри окна 1 с, удаление/undo. После перезапуска сохранены 3 титра, revision 53 и один завершённый экспорт. Список MP4 показывает исходный снимок v51 и изменение revision после remove/undo; содержимое восстановленных титров соответствует снимку.

Экспорт из UI: **8,000 с / 192 кадра / 24 fps / 896×516**, H.264/AAC, **2,844 с** CPU. Все 192 декодированных кадра сверены с независимым оракулом: JS placement/transition/title opacity + декодированный источник + frozen PNG глифов. Production TitleCompositor не использовался для расчёта ожидаемых RGB. Проверены порядок после blackout, наложение прозрачного текста и групповая прозрачность чёрной карточки.

- MAE всех кадров на 128×74: среднее **0,865**, максимум **1,621** из 255.
- MAE полного разрешения: среднее **1,953**, максимум **3,527** из 255; различия включают сжатие H.264.
- 9 paused browser samples: DOM opacity/hidden, абсолютное время и URL PNG совпали с frontend-расчётом; SHA PNG совпали со снимком MP4.
- Музыка/звуки проверены PCM-сравнением в трёх окнах: корреляция **0,9979–0,9985**, RMS **0,9889–1,0008**. Финальный хвост без звука остался тишиной.
- Клиповые времена/скорости, сегменты, переходы, музыка, метки/закрепления, sound_events, библиотека источников сохранены.

Общий просмотр прошёл до 8 с, без UI-ошибок, все три клипа/паузы посещены. Это не гарантия покадрового realtime-декодирования браузером: в фоне отмечались buffering/resync; paused scrub и выходной MP4 проверены отдельно. Служебные badge/message не входят в MP4; сообщение «Пауза» скрывается при активном титре на пустом участке.

## Регрессии и границы

**249 Python — PASS (29,750 с); 149 Node runner entries — PASS (220,199 мс, включая базовый JS script).** Новые проверки: 13 Python / 9 Node. Они включают реальный title-only MP4 48 кадров без видеоклипов, PNG/font SHA, layout/16M budget/revision до commit, token/HTTP, диапазоны/четыре перекрытия, async guards/decode/preload и сохранение черновика при 409.

| Requirement | Evidence — точные тесты |
|---|---|
| Кириллица, переносы, метрики и шрифт | `test_cyrillic_multiline_wrap_rgba_canonical_alignment_font_hash_and_cache_exclusions` |
| Время, появление и исчезновение | `test_opacity_midpoint_fades_half_open_and_exact_sum_duration` |
| Чёрный фон как единая группа | `test_compositor_group_black_opacity_transparent_and_layer_order_are_numeric_and_source_immutable` |
| MP4 после перехода, звук | `test_real_cpu_title_group_fades_draw_after_black_transition_without_audio_change` |
| Титры без видео и замена PNG | `test_title_only_extent_and_frozen_layers_hash_detect_changed_raster_without_project_write` |
| Старые проекты, undo, сохранность монтажа | `test_legacy_reads_add_update_remove_and_undo_keep_other_montage_source_and_later_notes` |
| Загрузка глифов и поздние ответы | `player stale PNG decode cannot replace new project or stage and paused preload cannot autoplay`; `late font catalogue never opens editor after source modal is replaced or closed` |

Успешные команды из корня книги:

```powershell
& 'E:/ComfyUI_windows_portable/python_embeded/python.exe' -s -B -m unittest discover -s TrueMoonTales/AMV/UI/tests -p 'test_*.py' -q
node --test TrueMoonTales/AMV/UI/tests/test_*.mjs
node TrueMoonTales/AMV/UI/.local/titles-qa-20261031-r1/PreviewSamples.mjs
& 'E:/ComfyUI_windows_portable/python_embeded/python.exe' -s -B TrueMoonTales/AMV/UI/.local/titles-qa-20261031-r1/VerifyParity.py
```

Остались расширениями: сохранённые стили, другие подложки/тени, drag титров и бегущая строка. Чтение старых проектов не записывает titles автоматически. Предпросмотр и MP4 имеют общие глифы/переносы/размещение/фазы, но не обещают битовое тождество браузерного масштабирования и H.264.

[Машинные результаты](Titles.v1.Checks.json), [редактор](Titles.Editor.v1.png), [поверх видео](Titles.Montage.v1.png), [чёрная карточка](Titles.BlackCard.v1.png). Локальный контрольный MP4: `.local/titles-qa-20261031-r1/project/Production/Exports/export-e3b44a6bc8114bb1/Draft.mp4`.
