using TrueMoon.Tales.Studio.Backend;

namespace TrueMoon.Tales.Studio.Presentation.Scene;

internal static class ReadinessMessage
{
    internal static string Explain(BackendReadinessIssue issue) => issue.Reason switch
    {
        "clip_take_missing" => "Клип без видеодубля. Назначьте дубль в сценарии.",
        "clip_timing" => "Проверьте позицию, длительность и скорость клипа.",
        "clip_source_range" => "Участок клипа выходит за границы дубля.",
        "asset_missing" => "Файл материала не найден.",
        "asset_access" => "Конфигурация запрещает доступ к материалу.",
        "asset_invalid" => "Проверьте формат, потоки и доступную длительность материала.",
        "asset_tools" => "Для проверки материала настройте FFmpeg и ffprobe.",
        "legacy_root_overlap" => "Отключённый legacy_root пересекается с корнем чтения. Разведите эти пути в конфигурации Studio.",
        "montage_empty" => "Добавьте клип или титр.",
        "montage_invalid" => "Проверьте сохранённые окна, тайминг и параметры монтажа.",
        "title_font" => "Шрифт титра недоступен.",
        "speech_source_changed" => "Запись отличается от сохранённого SHA. Верните исходный файл или добавьте версию.",
        "subtitle_review" => "Сверьте субтитр после изменения записи или тайминга речи.",
        "episode_duration" => "Хронометраж вне ориентира 3–5 минут.",
        "episode_window_without_video" => "Нет клипа с выбранным видеодублем.",
        "episode_join_states_missing" => "Заполните вход и выход сцен.",
        "episode_join_bridge_missing" => "Опишите связку между сценами.",
        "episode_join_data_invalid" => "Сохранённые записи о стыках не удалось прочитать.",
        "episode_voice_missing" => "На шкале нет включённой версии речи.",
        _ => "Проверьте материал сохранённого монтажа."
    };
}
