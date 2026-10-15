using System.Runtime.Versioning;
using OppoPodsManager.Communication.Abstractions;

namespace OppoPodsManager.Communication.Windows;

[SupportedOSPlatform("windows10.0.19041.0")]
public sealed class WindowsBleAdvertisementDiscovery : IDeviceDiscoveryMonitor
{
    private readonly BleAdvertisementHub _hub;
    private readonly object _gate = new();
    private HashSet<string> _lastStableIds = new(StringComparer.Ordinal);
    private long _generation;
    private bool _disposed;

    public event EventHandler<DeviceCandidatesChangedEventArgs>? DevicesChanged;

    public WindowsBleAdvertisementDiscovery(BleAdvertisementHub hub)
    {
        _hub = hub;
        _hub.DevicesChanged += OnHubDevicesChanged;
    }

    public Task<IReadOnlyList<DeviceCandidate>> DiscoverAsync(CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return Task.FromResult(CreateCandidates(_hub.Snapshot()));
    }

    private static IReadOnlyList<DeviceCandidate> CreateCandidates(
        IReadOnlyCollection<KeyValuePair<string, BleAdvEntry>> snapshot)
        => snapshot.Select(entry => new DeviceCandidate(
            entry.Key, entry.Key,
            entry.Key.StartsWith("BLE:", StringComparison.Ordinal) ? entry.Key[4..] : entry.Key,
            string.IsNullOrWhiteSpace(entry.Value.LocalName) ? "AirPods" : entry.Value.LocalName,
            [], [TransportNames.BleAdvertisement])).ToArray();

    public void Start()
    {
        lock (_gate)
        {
            if (_disposed)
                return;
            _hub.Start();
        }
        RaiseIfChanged();
    }

    public void Dispose()
    {
        lock (_gate)
        {
            if (_disposed)
                return;
            _disposed = true;
            _hub.DevicesChanged -= OnHubDevicesChanged;
            _lastStableIds.Clear();
        }
        _hub.Dispose();
    }

    private void OnHubDevicesChanged(object? sender, EventArgs e) => RaiseIfChanged();

    private void RaiseIfChanged()
    {
        DeviceCandidatesChangedEventArgs args;
        lock (_gate)
        {
            if (_disposed)
                return;
            // One snapshot per refresh, with no per-candidate copies or lookups.
            var snapshot = _hub.Snapshot();
            var ids = snapshot.Select(entry => entry.Key).ToHashSet(StringComparer.Ordinal);
            if (ids.SetEquals(_lastStableIds))
                return;
            _lastStableIds = ids;
            args = new DeviceCandidatesChangedEventArgs(CreateCandidates(snapshot), ++_generation);
        }
        DevicesChanged?.Invoke(this, args);
    }
}
