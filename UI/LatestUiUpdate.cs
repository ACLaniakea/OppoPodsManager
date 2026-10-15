namespace OppoPodsManager.UI;

// Keep one queued dispatcher callback and the latest value. A slow UI must not
// retain a snapshot/closure for every device notification.
internal sealed class LatestUiUpdate<T>(Action<Action> schedule, Action<T> apply) : IDisposable
{
    private readonly object _gate = new();
    private Action<T>? _apply = apply;
    private T? _pending;
    private bool _queued;
    private bool _disposed;

    public void Post(T value)
    {
        lock (_gate)
        {
            if (_disposed)
                return;
            _pending = value;
            if (_queued)
                return;
            _queued = true;
        }
        try { schedule(Drain); }
        catch
        {
            lock (_gate) { _queued = false; _pending = default; }
            throw;
        }
    }

    private void Drain()
    {
        T value;
        Action<T>? callback;
        lock (_gate)
        {
            _queued = false;
            if (_disposed)
                return;
            value = _pending!;
            _pending = default;
            callback = _apply;
        }
        callback?.Invoke(value);
    }

    public void Dispose()
    {
        lock (_gate)
        {
            _disposed = true;
            _pending = default;
            // A dispatcher callback may still be queued. Release its window now.
            _apply = null;
        }
    }
}
