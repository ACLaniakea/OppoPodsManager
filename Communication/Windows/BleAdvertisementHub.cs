using System.Runtime.Versioning;
using OppoPodsManager.Communication.Abstractions;
using Windows.Devices.Bluetooth.Advertisement;
using Windows.Storage.Streams;

namespace OppoPodsManager.Communication.Windows;

// Shared, bounded cache of currently visible AirPods proximity advertisements.
[SupportedOSPlatform("windows10.0.19041.0")]
public sealed class BleAdvertisementHub : IDisposable
{
    private const int DefaultCapacity = 256;
    private static readonly TimeSpan DefaultLifetime = TimeSpan.FromMinutes(2);
    private static readonly TimeSpan CleanupInterval = TimeSpan.FromSeconds(30);
    private readonly BluetoothLEAdvertisementWatcher? _watcher;
    private readonly Dictionary<string, BleAdvEntry> _entries = new(StringComparer.Ordinal);
    private readonly object _gate = new();
    private readonly TimeProvider _timeProvider;
    private readonly int _capacity;
    private readonly TimeSpan _entryLifetime;
    private readonly ITimer _expiryTimer;
    private bool _started;
    private bool _disposed;

    public event EventHandler<AppleAdvertisementEventArgs>? AdvertisementReceived;
    public event EventHandler? DevicesChanged;

    public BleAdvertisementHub() : this(TimeProvider.System, DefaultCapacity, DefaultLifetime) { }

    internal BleAdvertisementHub(TimeProvider timeProvider, int capacity, TimeSpan entryLifetime)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(capacity);
        if (entryLifetime <= TimeSpan.Zero)
            throw new ArgumentOutOfRangeException(nameof(entryLifetime));
        _timeProvider = timeProvider;
        _capacity = capacity;
        _entryLifetime = entryLifetime;
        _expiryTimer = timeProvider.CreateTimer(_ => ExpireEntries(), null,
            Timeout.InfiniteTimeSpan, Timeout.InfiniteTimeSpan);
        try
        {
            _watcher = new BluetoothLEAdvertisementWatcher();
            var manufacturer = new BluetoothLEManufacturerData { CompanyId = 0x004C };
            var advertisement = new BluetoothLEAdvertisement();
            advertisement.ManufacturerData.Add(manufacturer);
            _watcher.AdvertisementFilter = new BluetoothLEAdvertisementFilter { Advertisement = advertisement };
            _watcher.Received += OnReceived;
        }
        catch (Exception exception)
        {
            global::OppoPodsManager.Control.Subsystems.Logging.ApplicationLog.Current?.Debug(
                "BLE", $"初始化 BLE 广播监听失败，AirPods 发现不可用：{exception.Message}");
            _watcher = null;
        }
    }

    public void Start()
    {
        lock (_gate)
        {
            if (_disposed || _started)
                return;
            try
            {
                _started = true;
                _watcher?.Start();
                _expiryTimer.Change(CleanupInterval, CleanupInterval);
            }
            catch (Exception exception)
            {
                _started = false;
                global::OppoPodsManager.Control.Subsystems.Logging.ApplicationLog.Current?.Debug(
                    "BLE", $"启动 BLE 广播监听失败：{exception.Message}");
            }
        }
    }

    public void Stop()
    {
        bool changed;
        lock (_gate)
        {
            if (_disposed)
                return;
            _started = false;
            _expiryTimer.Change(Timeout.InfiniteTimeSpan, Timeout.InfiniteTimeSpan);
            try { _watcher?.Stop(); } catch { }
            changed = _entries.Count != 0;
            _entries.Clear();
        }
        if (changed)
            DevicesChanged?.Invoke(this, EventArgs.Empty);
    }

    public byte[]? GetLatest(string stableId)
    {
        lock (_gate)
            return _entries.TryGetValue(stableId, out var entry)
                && !IsExpired(entry, _timeProvider.GetTimestamp()) ? entry.Data : null;
    }

    public IReadOnlyCollection<KeyValuePair<string, BleAdvEntry>> Snapshot()
    {
        lock (_gate)
        {
            var now = _timeProvider.GetTimestamp();
            return _entries.Where(entry => !IsExpired(entry.Value, now)).ToArray();
        }
    }

    internal void ExpireEntries()
    {
        bool changed;
        lock (_gate)
        {
            if (_disposed || !_started)
                return;
            changed = RemoveExpired(_timeProvider.GetTimestamp());
        }
        if (changed)
            DevicesChanged?.Invoke(this, EventArgs.Empty);
    }

    private bool IsExpired(BleAdvEntry entry, long now)
        => _timeProvider.GetElapsedTime(entry.LastSeenTimestamp, now) >= _entryLifetime;

    // Caller holds _gate. Cleanup runs periodically and before admitting a new address.
    private bool RemoveExpired(long now)
    {
        var expired = _entries.Where(entry => IsExpired(entry.Value, now)).Select(entry => entry.Key).ToArray();
        foreach (var id in expired)
            _entries.Remove(id);
        return expired.Length != 0;
    }

    private void OnReceived(BluetoothLEAdvertisementWatcher sender, BluetoothLEAdvertisementReceivedEventArgs args)
    {
        if (args.Advertisement is null)
            return;
        BluetoothLEManufacturerData? manufacturer = null;
        foreach (var data in args.Advertisement.ManufacturerData)
            if (data.CompanyId == 0x004C) { manufacturer = data; break; }
        // Bound the allocation before copying untrusted radio input.
        if (manufacturer?.Data is not { Length: >= 27 and <= 255 } buffer)
            return;
        var bytes = ReadBuffer(buffer);
        if (!AppleProximityAdvertisement.IsSupported(bytes))
            return;

        var stableId = "BLE:" + args.BluetoothAddress.ToString("X12");
        bool changed;
        lock (_gate)
        {
            if (_disposed || !_started)
                return;
            var now = _timeProvider.GetTimestamp();
            changed = !_entries.TryGetValue(stableId, out var previous) || IsExpired(previous, now);
            if (changed)
            {
                RemoveExpired(now);
                if (_entries.Count >= _capacity)
                {
                    var oldest = _entries.MinBy(entry => entry.Value.LastSeenTimestamp).Key;
                    _entries.Remove(oldest);
                }
            }
            _entries[stableId] = new BleAdvEntry(bytes, args.Advertisement.LocalName, now);
        }
        // Battery updates go directly to the active connection; only membership changes
        // rebuild discovery snapshots.
        if (changed)
            DevicesChanged?.Invoke(this, EventArgs.Empty);
        AdvertisementReceived?.Invoke(this, new AppleAdvertisementEventArgs(stableId, bytes));
    }

    private static byte[] ReadBuffer(IBuffer buffer)
    {
        using var reader = DataReader.FromBuffer(buffer);
        var bytes = new byte[reader.UnconsumedBufferLength];
        reader.ReadBytes(bytes);
        return bytes;
    }

    public void Dispose()
    {
        lock (_gate)
        {
            if (_disposed)
                return;
            _disposed = true;
            _started = false;
            _expiryTimer.Dispose();
            if (_watcher is not null)
            {
                _watcher.Received -= OnReceived;
                try { _watcher.Stop(); } catch { }
            }
            _entries.Clear();
            AdvertisementReceived = null;
            DevicesChanged = null;
        }
    }
}

public sealed record BleAdvEntry(byte[] Data, string? LocalName, long LastSeenTimestamp = 0);
