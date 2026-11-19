using System.Text.Json;
using TrueMoon.Tales.Studio.Contracts.Projects;

namespace TrueMoon.Tales.Studio.Backend;

internal sealed record SceneJoinWindowPair(SegmentWindowContract Left, SegmentWindowContract Right)
{
    internal int ExitFrame => checked((int)((long)Left.StartFrame + Left.DurationFrames - 1));
    internal int EntryFrame => Right.StartFrame;
}

internal sealed record SceneJoinNotes(string LeftWindowId, string RightWindowId, string ExitState, string EntryState, string Bridge)
{
    internal string Summary => (ExitState.Length > 0, EntryState.Length > 0) switch
    {
        (false, false) => "Оба состояния не заполнены",
        (false, _) => "Не заполнен выход",
        (_, false) => "Не заполнен вход",
        _ when ExitState == EntryState => "Записи совпадают",
        _ when Bridge.Length > 0 => "Переход описан",
        _ => "Записи различаются · нужна связка или сверка"
    };
}

internal static class BackendSceneJoins
{
    private const string Key = "studio_scene_joins_v1";
    internal const int MaxNoteLength = 1600;

    internal static IEnumerable<SceneJoinWindowPair> Pairs(ProjectContract project)
    {
        var windows = project.Montage.Windows.OrderBy(row => row.StartFrame).ToArray();
        for (var index = 1; index < windows.Length; index++)
        {
            var left = windows[index - 1];
            var right = windows[index];
            if (left.SegmentId is not null && right.SegmentId is not null && left.SegmentId != right.SegmentId &&
                left.DurationFrames > 0 && right.DurationFrames > 0 &&
                (long)left.StartFrame + left.DurationFrames <= right.StartFrame)
                yield return new(left, right);
        }
    }

    internal static Dictionary<(string Left, string Right), SceneJoinNotes> Read(ProjectContract project)
    {
        var notes = new Dictionary<(string, string), SceneJoinNotes>();
        if (!project.Properties.TryGetValue(Key, out var raw)) return notes;
        var rows = raw.Deserialize<SceneJoinNotes[]>(BackendAdaptation.Json) ?? throw new InvalidDataException("Invalid scene joins.");
        foreach (var row in rows)
            if (row is not { LeftWindowId.Length: > 0, RightWindowId.Length: > 0, ExitState: not null, EntryState: not null, Bridge: not null } ||
                !notes.TryAdd((row.LeftWindowId, row.RightWindowId), row))
                throw new InvalidDataException("Invalid scene join.");
        return notes;
    }

    internal static ProjectContract Apply(ProjectContract project, SaveSceneJoin edit)
    {
        if (!Pairs(project).Any(pair => pair.Left.Id == edit.LeftWindowId && pair.Right.Id == edit.RightWindowId))
            throw new ScenarioEditException("scene_join_unavailable");
        var entry = Normalize(edit.EntryState);
        var exit = Normalize(edit.ExitState);
        var bridge = Normalize(edit.Bridge);
        if (bridge.Length > 0 && (entry.Length == 0 || exit.Length == 0))
            throw new ScenarioEditException("invalid_scene_join");
        var notes = Read(project);
        var key = (edit.LeftWindowId, edit.RightWindowId);
        if (entry.Length == 0 && exit.Length == 0)
        {
            if (!notes.Remove(key)) return project;
        }
        else
        {
            var record = new SceneJoinNotes(key.LeftWindowId, key.RightWindowId, exit, entry, bridge);
            if (notes.GetValueOrDefault(key) == record) return project;
            notes[key] = record;
        }
        var properties = new Dictionary<string, JsonElement>(project.Properties);
        if (notes.Count == 0) properties.Remove(Key);
        else properties[Key] = JsonSerializer.SerializeToElement(notes.Values.ToArray(), BackendAdaptation.Json);
        return project with { Properties = properties };
    }

    private static string Normalize(string? value)
    {
        if (value is null || value.Length > MaxNoteLength ||
            value.Any(character => char.IsControl(character) && character is not ('\r' or '\n' or '\t')))
            throw new ScenarioEditException("invalid_scene_join");
        return value.ReplaceLineEndings("\n").Trim();
    }
}
