namespace OppoPodsManager.Communication;

// At most one running operation and one pending value. Repeated notifications replace
// the pending value instead of retaining a task and a snapshot for every event.
internal sealed class LatestAsyncWorker<T> : IDisposable, IAsyncDisposable
{
    private readonly object _gate = new();
    private readonly Func<T, CancellationToken, Task> _process;
    private readonly Action<Exception> _onError;
    private readonly CancellationTokenSource _lifetime = new();
    private readonly CancellationToken _token;
    private Task _runner = Task.CompletedTask;
    private T? _pending;
    private bool _hasPending;
    private bool _running;
    private bool _disposed;

    public LatestAsyncWorker(Func<T, CancellationToken, Task> process, Action<Exception> onError)
    {
        _process = process;
        _onError = onError;
        _token = _lifetime.Token;
    }

    public Task Completion { get { lock (_gate) return _runner; } }

    public bool Post(T value)
    {
        lock (_gate)
        {
            if (_disposed)
                return false;
            _pending = value;
            _hasPending = true;
            if (!_running)
            {
                _running = true;
                _runner = Task.Run(RunAsync);
            }
            return true;
        }
    }

    private async Task RunAsync()
    {
        while (true)
        {
            T value;
            lock (_gate)
            {
                if (_disposed || !_hasPending)
                {
                    _running = false;
                    return;
                }
                value = _pending!;
                _pending = default;
                _hasPending = false;
            }
            try { await _process(value, _token).ConfigureAwait(false); }
            catch (OperationCanceledException) when (_token.IsCancellationRequested) { }
            catch (Exception exception)
            {
                // A failing log sink must not strand the worker in the running state.
                try { _onError(exception); } catch { }
            }
        }
    }

    public void Dispose()
    {
        Task runner;
        lock (_gate)
        {
            if (_disposed)
                return;
            _disposed = true;
            _pending = default;
            _hasPending = false;
            runner = _runner;
        }
        // Cancellation callbacks may re-enter Post, so do not cancel under _gate.
        _lifetime.Cancel();
        _ = runner.ContinueWith(_ => _lifetime.Dispose(), CancellationToken.None,
            TaskContinuationOptions.ExecuteSynchronously, TaskScheduler.Default);
    }

    public async ValueTask DisposeAsync()
    {
        Dispose();
        await Completion.ConfigureAwait(false);
    }
}
