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
        await RepeatedAppleDisposal();
        Console.WriteLine("PASS 1,000 Apple session cycles release all owned raw connections and subscriptions");
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

    private sealed class Fixture : IAsyncDisposable
    {
        public SnapshotDiscovery Discovery { get; } = new();
        public FakeConnectionFactory Ble { get; } = new(TransportNames.BleAdvertisement);
        public FakeConnectionFactory Classic { get; } = new("rfcomm");
        public FrontendState State { get; } = new();
        public ControlManager Control { get; }
        public Fixture()
        {
            var communication = new CommunicationController([Ble, Classic], [Discovery]);
            Control = new ControlManager(State, new DeviceScanner(communication), [new AppleManagerFactory(), new OppoFactory()]);
        }
        public ValueTask DisposeAsync() => Control.DisposeAsync();
    }

    private sealed class SnapshotDiscovery : IDeviceDiscovery
    {
        public IReadOnlyList<DeviceCandidate> Devices { get; set; } = [];
        public Task<IReadOnlyList<DeviceCandidate>> DiscoverAsync(CancellationToken token) => Task.FromResult(Devices);
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

    private sealed class OppoFactory : IBrandManagerFactory
    {
        public string Brand => "OPPO";
        public Guid ServiceId => new("0000079a-d102-11e1-9b23-00025b00a5a5");
        public bool IsCandidateName(string? name) => name?.Contains("OPPO", StringComparison.Ordinal) == true;
        public Task<IBrandManager> CreateAsync(DeviceConnectionPlan plan, IRawConnection connection, CancellationToken token)
        {
            var manager = DispatchProxy.Create<IBrandManager, TestManagerProxy>();
            ((TestManagerProxy)(object)manager).Connection = connection;
            return Task.FromResult(manager);
        }
    }
}

public class TestManagerProxy : DispatchProxy
{
    public IRawConnection Connection { get; set; } = null!;
    private readonly BusinessSnapshot _snapshot = new FrontendState().Snapshot with { DeviceName = "OPPO Enco X3", IsConnected = true };
    protected override object? Invoke(MethodInfo? method, object?[]? args) => method!.Name switch
    {
        "get_Snapshot" => _snapshot,
        "get_Capability" => DeviceCapability.Unknown with { ModelName = "OPPO Enco X3", IsKnownModel = true },
        "get_SessionLiveness" => null,
        "DisconnectAsync" => Task.CompletedTask,
        "DisposeAsync" => Connection.DisposeAsync(),
        _ => method.ReturnType == typeof(Task<bool>) ? Task.FromResult(false) : null
    };
}
