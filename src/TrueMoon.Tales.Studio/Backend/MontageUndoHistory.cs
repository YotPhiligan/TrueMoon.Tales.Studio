using TrueMoon.Tales.Studio.Persistence;

namespace TrueMoon.Tales.Studio.Backend;

// Held by one backend writer. The exact head binds inverse operations to the current stored state.
internal sealed class MontageUndoHistory
{
    private const int Capacity = 16;
    private readonly object _gate = new();
    private readonly List<Entry> _undo = [], _redo = [];
    private string? _head;
    private sealed record Entry(ProjectFileDocument Before, ProjectFileDocument After);

    internal (bool Undo, bool Redo) Available(string fingerprint)
    {
        lock (_gate) return (_head == fingerprint && _undo.Count != 0, _head == fingerprint && _redo.Count != 0);
    }

    internal void Record(ProjectFileDocument before, ProjectFileDocument after)
    {
        lock (_gate)
        {
            if (_head != before.Fingerprint) _undo.Clear();
            _redo.Clear();
            if (_undo.Count == Capacity) _undo.RemoveAt(0);
            _undo.Add(new(before, after));
            _head = after.Fingerprint;
        }
    }

    // Both halves of the transition execute inside the service's serialized writer command.
    internal ProjectFileDocument Target(string fingerprint, bool undo)
    {
        lock (_gate)
        {
            var from = undo ? _undo : _redo;
            if (_head != fingerprint || from.Count == 0) throw new ScenarioEditException("montage_history_unavailable");
            return undo ? from[^1].Before : from[^1].After;
        }
    }

    internal void Complete(string fingerprint, bool undo)
    {
        lock (_gate)
        {
            var from = undo ? _undo : _redo;
            var to = undo ? _redo : _undo;
            to.Add(from[^1]);
            from.RemoveAt(from.Count - 1);
            _head = fingerprint;
        }
    }
}
