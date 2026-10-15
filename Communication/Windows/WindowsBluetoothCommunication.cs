using System.Runtime.Versioning;
using OppoPodsManager.Communication.Abstractions;

namespace OppoPodsManager.Communication.Windows;

// Windows 多品牌设备发现入口；Win32/WinRT 细节留在本平台文件中，不泄漏到控制层。
[SupportedOSPlatform("windows10.0.19041.0")]
public sealed class WindowsBluetoothDiscovery : IDeviceDiscoveryMonitor
{
    private readonly object _gate = new();
    private global::Windows.Devices.Enumeration.DeviceWatcher? _watcher;
    private IReadOnlyList<DeviceCandidate> _lastSnapshot = [];
    private readonly LatestAsyncWorker<bool> _refreshWorker;
    private long _snapshotGeneration;
    private bool _disposed;

    public event EventHandler<DeviceCandidatesChangedEventArgs>? DevicesChanged;

    public WindowsBluetoothDiscovery()
    {
        _refreshWorker = new LatestAsyncWorker<bool>(RefreshSnapshotAsync, exception =>
            global::OppoPodsManager.Control.Subsystems.Logging.ApplicationLog.Current?.Error(
                "Bluetooth", "Windows 蓝牙设备变化刷新失败。", exception));
    }

    public async Task<IReadOnlyList<DeviceCandidate>> DiscoverAsync(CancellationToken cancellationToken)
    {
        var devices = await Task.Run(() => WindowsBluetoothDiscoveryCore.ListConnectedAsync(cancellationToken), cancellationToken);
        return devices
            .Where(device => device.Address != 0)
            .Select(device => new DeviceCandidate(
                device.Address.ToString("X12"),
                device.Address.ToString("X12"),
                device.Address.ToString("X12"),
                device.Name,
                device.ServiceIds,
                [WindowsRfcommConnectionFactory.TransportName]))
            .ToArray();
    }

    public void Start()
    {
        lock (_gate)
        {
            if (_disposed || _watcher is not null)
                return;

            var selector = global::Windows.Devices.Bluetooth.BluetoothDevice.GetDeviceSelectorFromConnectionStatus(
                global::Windows.Devices.Bluetooth.BluetoothConnectionStatus.Connected);
            _watcher = global::Windows.Devices.Enumeration.DeviceInformation.CreateWatcher(selector);
            _watcher.Added += OnDeviceChanged;
            _watcher.Updated += OnDeviceChanged;
            _watcher.Removed += OnDeviceChanged;
            _watcher.EnumerationCompleted += OnDeviceChanged;
            _watcher.Start();
        }
        _refreshWorker.Post(true);
    }

    public void Dispose()
    {
        global::Windows.Devices.Enumeration.DeviceWatcher? watcher;
        lock (_gate)
        {
            if (_disposed)
                return;
            _disposed = true;
            watcher = _watcher;
            _watcher = null;
            _lastSnapshot = [];
        }

        _refreshWorker.Dispose();
        if (watcher is null)
            return;
        watcher.Added -= OnDeviceChanged;
        watcher.Updated -= OnDeviceChanged;
        watcher.Removed -= OnDeviceChanged;
        watcher.EnumerationCompleted -= OnDeviceChanged;
        try { watcher.Stop(); } catch { }
    }

    private void OnDeviceChanged(global::Windows.Devices.Enumeration.DeviceWatcher sender, object args)
        => _refreshWorker.Post(true);

    private async Task RefreshSnapshotAsync(bool _, CancellationToken cancellationToken)
    {
        // DeviceWatcher often reports a burst while properties are being populated.
        await Task.Delay(100, cancellationToken);
        var snapshot = await DiscoverAsync(cancellationToken);
        IReadOnlyList<DeviceCandidate> previous;
        lock (_gate)
            previous = _lastSnapshot;
        if (previous.Count > 0 && snapshot.Count == 0)
        {
            // Recheck transient empty enumerations before dropping an active session.
            await Task.Delay(500, cancellationToken);
            snapshot = await DiscoverAsync(cancellationToken);
        }
        cancellationToken.ThrowIfCancellationRequested();
        long generation;
        lock (_gate)
        {
            if (_disposed || SameDevices(_lastSnapshot, snapshot))
                return;
            _lastSnapshot = snapshot;
            generation = ++_snapshotGeneration;
        }
        DevicesChanged?.Invoke(this, new DeviceCandidatesChangedEventArgs(snapshot, generation));
    }

    private static bool SameDevices(IReadOnlyList<DeviceCandidate> left, IReadOnlyList<DeviceCandidate> right)
        => left.Count == right.Count && left.Zip(right).All(pair =>
            pair.First.StableId == pair.Second.StableId
            && pair.First.DisplayName == pair.Second.DisplayName
            && pair.First.ServiceIds.Order().SequenceEqual(pair.Second.ServiceIds.Order()));
}

[SupportedOSPlatform("windows10.0.19041.0")]
public sealed class WindowsRfcommConnectionFactory : IConnectionFactory
{
    public const string TransportName = "rfcomm";

    public string Transport => TransportName;

    public async Task<IRawConnection> OpenAsync(
        DeviceCandidate candidate,
        ConnectionOptions options,
        CancellationToken cancellationToken)
    {
        if (options.ServiceId is not { } serviceId)
            throw new InvalidOperationException("RFCOMM connection requires a service UUID selected by ControlManager.");

        var connection = new WindowsRfcommConnection(candidate, serviceId, options.AllowBareChannels, options.Channel);
        try
        {
            await connection.ConnectAsync(cancellationToken);
            return connection;
        }
        catch
        {
            await connection.DisposeAsync();
            throw;
        }
    }
}
