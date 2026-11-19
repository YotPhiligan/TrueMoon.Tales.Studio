using TrueMoon.Argentis;
using TrueMoon.Tales.Studio.Backend;

namespace TrueMoon.Tales.Studio.Presentation.Scene;

internal sealed partial class StoryboardWorkspace
{
    private static void ShowReferences(VStack rows, StoryboardShot? shot)
    {
        foreach (var row in rows.Items.ToArray()) { rows.Items.Remove(row); row.Dispose(); }
        if (shot is not null)
        {
            foreach (var (entry, version, action) in shot.AssignedReferences)
                Add(action ? "Действие" : "Сцена", entry.Name, version);
            foreach (var input in shot.TakeReferences)
                Add("Дубль", input.Name, input.Version);
            if (shot.Video is not null && shot.AssignedReferences.Length > 0 && shot.TakeReferences.Length == 0)
                rows.Add(Text.Plain("Опора дубля не записана"));
        }
        rows.IsVisible = rows.Items.Count > 0;

        void Add(string scope, string name, ReferenceVersion version)
        {
            rows.Add(Text.Plain($"{scope} · {name} · v{version.Number}"));
            var identity = Text.Plain($"{version.Label} · {version.Sha256[..8]}");
            identity.AccessibleName = $"{version.Label} · {version.ImageReference} · SHA-256 {version.Sha256}";
            rows.Add(identity);
        }
    }
}
