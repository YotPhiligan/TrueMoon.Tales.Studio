using TrueMoon.Tales.Studio.Backend;

namespace TrueMoon.Tales.Studio.Presentation.Project;

internal sealed class ProjectViewModel(IBackendProjectReader connection, string? initialFingerprint = null, Action<string?>? initialOpen = null) : IDisposable
{
    private readonly CancellationTokenSource _closing = new();
    private Task<OpenedBackendProject>? _pending;
    internal OpenedBackendProject? Opened { get; private set; }
    internal string Status { get; private set; } = "Открытие проекта…";
    internal bool Loading => _pending is not null;
    internal bool Failed { get; private set; }

    internal void Reload()
    {
        if (Loading || _closing.IsCancellationRequested) return;
        Failed = false;
        Status = "Открытие проекта…";
        _pending = connection.OpenAsync(_closing.Token);
    }

    internal bool Poll()
    {
        if (_pending is not { IsCompleted: true } pending) return false;
        _pending = null;
        try
        {
            var snapshot = pending.GetAwaiter().GetResult();
            if (Opened is null && initialFingerprint is not null &&
                !string.Equals(snapshot.Fingerprint, initialFingerprint, StringComparison.OrdinalIgnoreCase))
                throw new InvalidDataException("Проект изменился после выбора. Обновите список проектов.");
            Opened = snapshot;
            Status = "Проект открыт · только чтение";
        }
        catch (Exception error)
        {
            Failed = true;
            Status = error is OperationCanceledException ? "Истекло время ожидания backend. Повторите чтение проекта."
                : $"Не удалось прочитать проект: {error.Message}";
        }
        var notify = initialOpen;
        initialOpen = null;
        notify?.Invoke(Failed ? Status : null);
        return true;
    }

    public void Dispose()
    {
        _closing.Cancel();
        if (_pending is { } pending) _ = pending.ContinueWith(task => _ = task.Exception,
            CancellationToken.None, TaskContinuationOptions.OnlyOnFaulted | TaskContinuationOptions.ExecuteSynchronously, TaskScheduler.Default);
        _closing.Dispose();
    }
}
