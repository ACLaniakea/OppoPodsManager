using System.Reflection;
using OppoPodsManager.Communication;
using OppoPodsManager.Communication.Abstractions;
using OppoPodsManager.Control.Abstractions;
using OppoPodsManager.Control.Brands.Apple;
using OppoPodsManager.Control.Core.Models;

internal static class SelectionRegression
{
    private static readonly DeviceCandidate Nearby = new("BLE:000000000001", "BLE:000000000001", "000000000001", "AirPods", [], [TransportNames.BleAdvertisement]);
    private static readonly DeviceCandidate Oppo = new("OPPO-CONNECTED", "OPPO-CONNECTED", "000000000002", "OPPO Enco X3", [], ["rfcomm"]);
    private static void Check(bool value, string message) { if (!value) throw new InvalidOperationException(message); }

    public static async Task RunAsync()
    {
        await NearbyIsManualOnly();
        Console.WriteLine("PASS Nearby AirPods cannot become active through discovery or automatic reconnect");
        await OppoArrivesLater();
        Console.WriteLine("PASS OPPO Enco X3 becomes active even when nearby AirPods arrived first");
        await NoPassiveReplacement();
        Console.WriteLine("PASS Removing the connected headset does not switch to a nearby broadcast");
        await PreserveManualSelection();
        Console.WriteLine("PASS An explicit AirPods selection remains supported and is preserved");
        await GenericConnectedDevices();
        Console.WriteLine("PASS Connected-headset selection is generic across six names and RFCOMM/GATT transports");
        await RepeatedAppleDisposal();
        Console.WriteLine("PASS 1,000 Apple session cycles release all owned raw connections and subscriptions");
        await CachedFailureRecovery();
        Console.WriteLine("PASS Automatic retry bypasses stale negative brand results");
        await LateStartupSnapshot();
        Console.WriteLine("PASS Startup retries find a late device without a watcher event or restart");
        await RespectManualDisconnect();
        Console.WriteLine("PASS Retry loop preserves explicit user disconnection");
        await CancelledProbeReleasesConnection();
        Console.WriteLine("PASS Cancelled initialization releases the raw connection and permits a fresh attempt");
    }

    private static async Task NearbyIsManualOnly()
    {
        await using var f = new Fixture(); f.Discovery.Devices = [Nearby];
        var devices = await f.Control.RefreshAvailableDevicesAsync(default);
        Check(devices.Count == 1 && f.Control.ActiveManager is null, "Passive broadcast became the current headset.");
        Check(!await f.Control.ConnectFirstAvailableAsync(default), "Automatic reconnect chose a nearby broadcast.");
        Check(f.Ble.OpenCount == 0, "Discovery opened unnecessary BLE sessions.");
    }

    private static async Task OppoArrivesLater()
    {
        await using var f = new Fixture(); f.Discovery.Devices = [Nearby];
        await f.Control.RefreshAvailableDevicesAsync(default);
        f.Discovery.Devices = [Nearby, Oppo];
        await f.Control.RefreshAvailableDevicesAsync(default);
        Check(f.Control.ActiveDeviceId == Oppo.StableId && f.State.Snapshot.DeviceName == "OPPO Enco X3", "Nearby AirPods blocked the actual OPPO headset.");
        Check(f.Ble.OpenCount == 0 && f.Classic.OpenCount == 1, "Discovery probed the wrong transport.");
    }

    private static async Task NoPassiveReplacement()
    {
        await using var f = new Fixture(); f.Discovery.Devices = [Nearby, Oppo];
        await f.Control.RefreshAvailableDevicesAsync(default);
        f.Discovery.Devices = [Nearby];
        await f.Control.RefreshAvailableDevicesAsync(default);
        Check(f.Control.ActiveManager is null && f.Ble.OpenCount == 0, "Removal selected a stranger's advertisement.");
    }

    private static async Task PreserveManualSelection()
    {
        await using var f = new Fixture(); f.Discovery.Devices = [Nearby];
        await f.Control.RefreshAvailableDevicesAsync(default);
        Check(await f.Control.ConnectAsync(Nearby.StableId, default), "Manual AirPods selection failed.");
        Check(f.State.Snapshot.DeviceName == "AirPods Pro 2 (USB-C)", "Actual Apple factory no longer decodes its own device.");
        f.Discovery.Devices = [Nearby, Oppo];
        await f.Control.RefreshAvailableDevicesAsync(default);
        Check(f.Control.ActiveDeviceId == Nearby.StableId, "An explicit manual choice was overwritten.");
    }

    private static async Task RepeatedAppleDisposal()
    {
        await using var f = new Fixture(); f.Discovery.Devices = [Nearby];
        await f.Control.RefreshAvailableDevicesAsync(default);
        for (var i = 0; i < 1000; i++)
        {
            Check(await f.Control.ConnectAsync(Nearby.StableId, default), "Session creation failed.");
            await f.Control.DisconnectAsync();
            Check(f.Ble.ActiveConnections == 0, "Apple manager retained its owned raw connection.");
            Check(f.Ble.Last!.SubscriberCountAtDisposal == 0, "Advertisement subscription survived disconnection.");
        }
        GC.Collect(); GC.WaitForPendingFinalizers(); GC.Collect();
        var retained = f.Ble.Created.Count(reference => reference.TryGetTarget(out _));
        Console.WriteLine($"  Raw BLE connections retained after 1,000 disconnects: {retained}");
        Check(retained <= 3, "Disposed BLE connections remain rooted.");
    }

    private static async Task GenericConnectedDevices()
    {
        foreach (var (brand, name) in new[] { ("OPPO", "OPPO Enco X3"), ("OPPO", "OnePlus Buds Pro 3"),
            ("Vivo", "vivo TWS 4"), ("Huawei", "HUAWEI FreeBuds Pro"), ("Xiaomi", "Xiaomi Buds 5"), ("Edifier", "EDIFIER NeoBuds") })
        foreach (var transport in new[] { "rfcomm", "gatt" })
        {
            await using var f = new Fixture(brand, name, transport);
            f.Discovery.Devices = [Nearby];
            await f.Control.RefreshAvailableDevicesAsync(default);
            var candidate = new DeviceCandidate("CONNECTED", "CONNECTED", "000000000003", name, [], [transport]);
            f.Discovery.Devices = [Nearby, candidate];
            var options = await f.Control.RefreshAvailableDevicesAsync(default);
            Check(f.Control.ActiveDeviceId == candidate.StableId && f.State.Snapshot.DeviceName == name, "Selection was restricted to an OPPO name or RFCOMM.");
            Check(options.Single(item => item.Id == Nearby.StableId).IsNearbyOnly, "Nearby origin was lost.");
            Check(!options.Single(item => item.Id == candidate.StableId).IsNearbyOnly, "Connected headset was labeled as a broadcast.");
            await f.Control.DisconnectAsync();
            await f.Control.RefreshAvailableDevicesAsync(default);
            Check(f.Control.ActiveManager is null, "Refresh overrode manual disconnect.");
        }
    }

    private static async Task Until(Func<bool> condition, int milliseconds = 8000)
    {
        using var timeout = new CancellationTokenSource(milliseconds);
        while (!condition()) await Task.Delay(20, timeout.Token);
    }
    private static async Task CachedFailureRecovery()
    {
        await using var f = new Fixture(); f.Discovery.Devices = [Oppo]; f.Factory.FailuresRemaining = 1;
        await f.Control.RefreshAvailableDevicesAsync(default);
        Check(f.Control.ActiveManager is null && f.Classic.ActiveConnections == 0, "Failed initialization retained its connection.");
        Check(await f.Control.ConnectFirstAvailableAsync(default), "Negative cache prevented a fresh automatic attempt.");
        Check(f.Control.ActiveDeviceId == Oppo.StableId, "Recovery did not select the connected device.");
    }
    private static async Task LateStartupSnapshot()
    {
        await using var f = new Fixture();
        f.Control.StartMonitoring();
        await Until(() => f.Discovery.Scans > 0);
        f.Discovery.Devices = [Oppo]; // Deliberately emit no DeviceWatcher event.
        await Until(() => f.Control.ActiveDeviceId == Oppo.StableId);
    }
    private static async Task RespectManualDisconnect()
    {
        await using var f = new Fixture(); f.Discovery.Devices = [Oppo];
        await f.Control.RefreshAvailableDevicesAsync(default);
        await f.Control.DisconnectAsync(); var opened = f.Classic.OpenCount;
        f.Control.StartMonitoring(); await Task.Delay(5500);
        Check(f.Control.ActiveManager is null && f.Classic.OpenCount == opened, "Retry overrode an explicit disconnect.");
    }
    private static async Task CancelledProbeReleasesConnection()
    {
        await using var f = new Fixture(); f.Discovery.Devices = [Oppo];
        using var cancelled = new CancellationTokenSource(); f.Factory.CancelOnCreate = cancelled;
        try { await f.Control.RefreshAvailableDevicesAsync(cancelled.Token); throw new Exception("Expected cancellation."); }
        catch (OperationCanceledException) when (cancelled.IsCancellationRequested) { }
        Check(f.Classic.ActiveConnections == 0, "Cancelled probe leaked the RFCOMM connection.");
        Check(await f.Control.ConnectFirstAvailableAsync(default), "A cancelled probe prevented recovery.");
    }

    private sealed class Fixture : IAsyncDisposable
    {
        public SnapshotDiscovery Discovery { get; } = new();
        public FakeConnectionFactory Ble { get; } = new(TransportNames.BleAdvertisement);
        public FakeConnectionFactory Classic { get; }
        public FrontendState State { get; } = new();
        public ControlManager Control { get; }
        public OppoFactory Factory { get; }
        public Fixture(string brand = "OPPO", string nameToken = "OPPO", string transport = "rfcomm")
        {
            Classic = new(transport);
            var communication = new CommunicationController([Ble, Classic], [Discovery]);
            Factory = new OppoFactory(brand, nameToken);
            Control = new ControlManager(State, new DeviceScanner(communication), [new AppleManagerFactory(), Factory]);
        }
        public ValueTask DisposeAsync() => Control.DisposeAsync();
    }

    private sealed class SnapshotDiscovery : IDeviceDiscovery
    {
        public IReadOnlyList<DeviceCandidate> Devices { get; set; } = [];
        public int Scans;
        public Task<IReadOnlyList<DeviceCandidate>> DiscoverAsync(CancellationToken token)
        { Interlocked.Increment(ref Scans); return Task.FromResult(Devices); }
    }

    private sealed class FakeConnectionFactory(string transport) : IConnectionFactory
    {
        public string Transport => transport;
        public int OpenCount, ActiveConnections;
        public FakeRawConnection? Last;
        public List<WeakReference<FakeRawConnection>> Created { get; } = [];
        public Task<IRawConnection> OpenAsync(DeviceCandidate candidate, ConnectionOptions options, CancellationToken token)
        {
            OpenCount++; ActiveConnections++;
            Last = new FakeRawConnection(() => ActiveConnections--);
            Created.Add(new(Last));
            return Task.FromResult<IRawConnection>(Last);
        }
    }

    private sealed class FakeRawConnection(Action onDispose) : IRawConnection, IAppleAdvertisementProvider
    {
        private bool _disposed;
        private EventHandler<byte[]>? _updated;
        public int SubscriberCountAtDisposal { get; private set; } = -1;
        public bool IsConnected => !_disposed;
        public event EventHandler<ReadOnlyMemory<byte>>? DataReceived { add { } remove { } }
        public event EventHandler? Disconnected { add { } remove { } }
        public event EventHandler<byte[]>? AdvertisementUpdated { add => _updated += value; remove => _updated -= value; }
        public byte[] LatestData { get; } = [0x07,0x19,1,0x24,0x20,0,0x77,0x07,0,0,0,0,0,0,0,0,0,0,0,0,0,0,0,0,0,0,0];
        public Task ConnectAsync(CancellationToken token) => Task.CompletedTask;
        public Task DisconnectAsync(CancellationToken token) => Task.CompletedTask;
        public Task SendAsync(ReadOnlyMemory<byte> bytes, CancellationToken token) => Task.CompletedTask;
        public ValueTask DisposeAsync()
        {
            if (!_disposed)
            {
                _disposed = true;
                SubscriberCountAtDisposal = _updated?.GetInvocationList().Length ?? 0;
                _updated = null; onDispose();
            }
            return ValueTask.CompletedTask;
        }
    }

    private sealed class OppoFactory(string brand, string nameToken) : IBrandManagerFactory
    {
        public int FailuresRemaining;
        public CancellationTokenSource? CancelOnCreate;
        public string Brand => brand;
        public Guid ServiceId => new("0000079a-d102-11e1-9b23-00025b00a5a5");
        public bool IsCandidateName(string? name) => name?.Contains(nameToken, StringComparison.Ordinal) == true;
        public Task<IBrandManager> CreateAsync(DeviceConnectionPlan plan, IRawConnection connection, CancellationToken token)
        {
            if (CancelOnCreate is { } cancel)
            { CancelOnCreate = null; cancel.Cancel(); throw new OperationCanceledException(token); }
            if (FailuresRemaining-- > 0) throw new InvalidOperationException("Transient initialization failure.");
            var manager = DispatchProxy.Create<IBrandManager, TestManagerProxy>();
            ((TestManagerProxy)(object)manager).Connection = connection;
            ((TestManagerProxy)(object)manager).DeviceName = plan.Candidate.DisplayName;
            return Task.FromResult(manager);
        }
    }
}

public class TestManagerProxy : DispatchProxy
{
    public IRawConnection Connection { get; set; } = null!;
    public string DeviceName { get; set; } = "OPPO Enco X3";
    private BusinessSnapshot Snapshot => new FrontendState().Snapshot with { DeviceName = DeviceName, IsConnected = true };
    protected override object? Invoke(MethodInfo? method, object?[]? args) => method!.Name switch
    {
        "get_Snapshot" => Snapshot,
        "get_Capability" => DeviceCapability.Unknown with { ModelName = DeviceName, IsKnownModel = true },
        "get_SessionLiveness" => null,
        "DisconnectAsync" => Task.CompletedTask,
        "DisposeAsync" => Connection.DisposeAsync(),
        _ => method.ReturnType == typeof(Task<bool>) ? Task.FromResult(false) : null
    };
}
