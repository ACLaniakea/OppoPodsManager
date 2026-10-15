using System.IO.Pipes;
using System.Security.Cryptography;
using System.Text;

namespace OppoPodsManager.Infrastructure;

// The identity is independent of the executable's directory and version.
// A second launch signals the existing process before initializing UI or Bluetooth.
internal sealed class SingleInstance : IDisposable
{
    private readonly Mutex _mutex;
    private readonly string _pipeName;
    private readonly CancellationTokenSource _stop = new();
    private readonly object _gate = new();
    private readonly Task _listener;
    private Action? _activate;
    private bool _pending;
    private bool _disposed;
    public bool IsPrimary { get; }

    public SingleInstance(string applicationId)
    {
        var user = Environment.UserDomainName + "\\" + Environment.UserName;
        var suffix = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(applicationId + "|" + user)))[..32];
        _pipeName = "OppoPodsManager-" + suffix;
        _mutex = new Mutex(false, _pipeName);
        try { IsPrimary = _mutex.WaitOne(0); }
        catch (AbandonedMutexException) { IsPrimary = true; }
        _listener = IsPrimary ? ListenAsync() : Task.CompletedTask;
    }

    public void SetActivationHandler(Action handler)
    {
        bool pending;
        lock (_gate)
        {
            if (_disposed) return;
            _activate = handler;
            pending = _pending;
            _pending = false;
        }
        if (pending) handler();
    }

    public async Task<bool> ActivateExistingAsync()
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(4));
        try
        {
            using var client = new NamedPipeClientStream(".", _pipeName, PipeDirection.Out,
                PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly);
            await client.ConnectAsync(timeout.Token).ConfigureAwait(false);
            await client.WriteAsync(new byte[] { 1 }, timeout.Token).ConfigureAwait(false);
            return true;
        }
        catch (Exception error) when (error is IOException or OperationCanceledException or UnauthorizedAccessException)
        {
            return false;
        }
    }

    private async Task ListenAsync()
    {
        while (!_stop.IsCancellationRequested)
        {
            try
            {
                using var server = new NamedPipeServerStream(_pipeName, PipeDirection.In, 1,
                    PipeTransmissionMode.Byte, PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly);
                await server.WaitForConnectionAsync(_stop.Token).ConfigureAwait(false);
                using var timeout = CancellationTokenSource.CreateLinkedTokenSource(_stop.Token);
                timeout.CancelAfter(TimeSpan.FromSeconds(2));
                var message = new byte[1];
                if (await server.ReadAsync(message, timeout.Token).ConfigureAwait(false) != 1 || message[0] != 1)
                    continue;
                Action? handler;
                lock (_gate)
                {
                    if (_disposed) return;
                    handler = _activate;
                    if (handler is null) _pending = true;
                }
                handler?.Invoke();
            }
            catch (OperationCanceledException) when (_stop.IsCancellationRequested) { return; }
            catch (Exception error) when (error is IOException or OperationCanceledException)
            {
                // A launch that exits during the handshake must not stop activation.
            }
        }
    }

    public void Dispose()
    {
        lock (_gate)
        {
            if (_disposed) return;
            _disposed = true;
            _activate = null;
        }
        _stop.Cancel();
        _listener.GetAwaiter().GetResult();
        _stop.Dispose();
        // Mutex ownership stays on the synchronous entry-point thread.
        if (IsPrimary) _mutex.ReleaseMutex();
        _mutex.Dispose();
    }
}
