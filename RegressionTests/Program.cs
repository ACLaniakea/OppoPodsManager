using System.Collections.Concurrent;
using OppoPodsManager.Communication;
using OppoPodsManager.Communication.Abstractions;
using OppoPodsManager.Communication.Windows;
using OppoPodsManager.Control.Brands.Apple;
using OppoPodsManager.Control.Brands.Huawei;
using Windows.Devices.Bluetooth.Advertisement;
using Windows.Storage.Streams;

internal static class Program
{
    private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(10);

    private static async Task<int> Main()
    {
        (string Name, Func<Task> Run)[] tests =
        [
            ("Reject unrelated, truncated and oversized Apple advertisements", InvalidAdvertisements),
            ("Keep valid proximity frames and normalize blank names", ValidAdvertisements),
            ("Battery updates bypass full discovery", BatteryUpdates),
            ("Rotating addresses stay within cache capacity", BoundedCache),
            ("Expired devices are removed without new radio traffic", Expiry),
            ("Stop clears cache and rejects late callbacks", StopAndDispose),
            ("200th address allocation stays below quadratic baseline", LinearAllocation),
            ("Blocked worker coalesces 10,000 snapshots to the newest", Coalescing),
            ("Disposal cancels active work and drops pending snapshots", CancelWorker),
            ("Worker recovers after an operation fails", WorkerFailure),
            ("Concurrent producers preserve the final snapshot", ConcurrentProducers),
            ("Communication controller bounds rediscovery during a burst", ControllerBurst),
            ("Controller disposal cancels discovery and suppresses delivery", ControllerDisposal),
            ("Manual discovery still deduplicates and sorts devices", ManualDiscovery),
            ("Huawei MBB known vectors moved out of unused production self-test", MbbProtocolVectors),
            ("UI dispatcher keeps one callback and only the newest of 10,000 values", UiBurst),
            ("Disposed UI dispatcher drops its queued values and subscriber", UiDisposal),
            ("UI dispatcher preserves reentrant updates and recovers after apply errors", UiReentrancy),
        ];
        var failures = 0;
        foreach (var test in tests)
        {
            try { await test.Run().WaitAsync(Timeout); Console.WriteLine($"PASS {test.Name}"); }
            catch (Exception exception) { failures++; Console.WriteLine($"FAIL {test.Name}: {exception}"); }
        }
        Console.WriteLine($"{tests.Length - failures}/{tests.Length} passed");
        return failures == 0 ? 0 : 1;
    }

    private static Task Sync(Action test) { test(); return Task.CompletedTask; }
    private static void Check(bool condition, string message)
    {
        if (!condition) throw new InvalidOperationException(message);
    }
    private static TaskCompletionSource Signal() => new(TaskCreationOptions.RunContinuationsAsynchronously);
    private static byte[] Frame(byte battery = 0x77)
    {
        var data = new byte[27];
        data[0] = 0x07; data[1] = 0x19; data[2] = 1;
        data[3] = 0x0E; data[4] = 0x20; data[6] = battery;
        return data;
    }
    private static DeviceCandidate Candidate(string id, string? name = null)
        => new(id, id, id, name ?? id, [], ["test"]);

    private static Task InvalidAdvertisements() => Sync(() =>
    {
        using var hub = new BleAdvertisementHub(); hub.Start();
        var watcher = BluetoothLEAdvertisementWatcher.Last;
        var changed = 0; hub.DevicesChanged += (_, _) => changed++;
        var wrongType = Frame(); wrongType[0] = 0x10;
        var wrongLength = Frame(); wrongLength[1] = 0x18;
        foreach (var data in new[] { new byte[3], new byte[11], Frame()[..26], wrongType, wrongLength, new byte[4096] })
            watcher.Emit(1, data);
        watcher.Emit(1, Frame(), company: 0x1234);
        Check(hub.Snapshot().Count == 0 && changed == 0, "Unrelated Apple traffic became a headphone candidate.");
        Check(DataReader.Created == DataReader.Disposed, "DataReader was not deterministically released.");
    });

    private static async Task ValidAdvertisements()
    {
        using var hub = new BleAdvertisementHub();
        using var discovery = new WindowsBleAdvertisementDiscovery(hub); discovery.Start();
        var watcher = BluetoothLEAdvertisementWatcher.Last;
        watcher.Emit(1, Frame(), "   ");
        var devices = await discovery.DiscoverAsync(default);
        Check(devices.Count == 1 && devices[0].DisplayName == "AirPods", "Blank name was not normalized.");
        Check(AppleAdvertisementParser.TryParse(Frame(), out var status) && status.LeftBattery == 70,
            "Valid existing status decoding regressed.");
        var unknownModel = Frame(); unknownModel[3] = 0xFF; unknownModel[4] = 0xFF;
        Check(AppleAdvertisementParser.TryParse(unknownModel, out _), "Future model IDs should retain basic decoding.");
        Check(!AppleAdvertisementParser.TryParse(new byte[27], out _), "Parser accepts unrelated data.");
    }

    private static Task BatteryUpdates() => Sync(() =>
    {
        using var hub = new BleAdvertisementHub();
        using var discovery = new WindowsBleAdvertisementDiscovery(hub); discovery.Start();
        var watcher = BluetoothLEAdvertisementWatcher.Last;
        var refreshes = 0; var updates = 0;
        discovery.DevicesChanged += (_, _) => refreshes++;
        hub.AdvertisementReceived += (_, _) => updates++;
        for (var i = 0; i < 1000; i++) watcher.Emit(1, Frame((byte)i));
        Check(refreshes == 1 && updates == 1000, $"refreshes={refreshes}, updates={updates}");
        Check(hub.GetLatest("BLE:000000000001")![6] == unchecked((byte)999), "Latest battery payload was lost.");
    });

    private static Task BoundedCache() => Sync(() =>
    {
        var clock = new ManualClock();
        using var hub = new BleAdvertisementHub(clock, 32, TimeSpan.FromMinutes(2)); hub.Start();
        var watcher = BluetoothLEAdvertisementWatcher.Last;
        for (ulong address = 1; address <= 10_000; address++)
        {
            clock.Advance(TimeSpan.FromMilliseconds(1));
            watcher.Emit(address, Frame());
            Check(hub.Snapshot().Count <= 32, "Cache exceeded its configured bound.");
        }
        Check(hub.Snapshot().Count == 32, "Capacity was not used.");
        Check(hub.GetLatest("BLE:000000000001") is null, "Oldest rotating address was retained.");
        Check(hub.GetLatest("BLE:" + 10_000UL.ToString("X12")) is not null, "Newest address was evicted.");
    });

    private static Task Expiry() => Sync(() =>
    {
        var clock = new ManualClock();
        using var hub = new BleAdvertisementHub(clock, 32, TimeSpan.FromMinutes(2));
        using var discovery = new WindowsBleAdvertisementDiscovery(hub); discovery.Start();
        var watcher = BluetoothLEAdvertisementWatcher.Last;
        IReadOnlyList<DeviceCandidate>? last = null;
        discovery.DevicesChanged += (_, args) => last = args.Devices;
        watcher.Emit(1, Frame());
        clock.Advance(TimeSpan.FromMinutes(3)); hub.ExpireEntries();
        Check(hub.Snapshot().Count == 0 && last?.Count == 0, "Expiry did not publish removal.");
        watcher.Emit(1, Frame());
        clock.Advance(TimeSpan.FromMinutes(3));
        // A frame can arrive after TTL but before the cleanup timer. It must rediscover the device.
        var additions = 0; hub.DevicesChanged += (_, _) => additions++;
        watcher.Emit(1, Frame());
        Check(additions == 1 && hub.Snapshot().Count == 1, "Expired address did not become visible again.");
    });

    private static Task StopAndDispose() => Sync(() =>
    {
        using var hub = new BleAdvertisementHub(); hub.Start();
        var watcher = BluetoothLEAdvertisementWatcher.Last;
        watcher.Emit(1, Frame()); hub.Stop(); watcher.Emit(2, Frame());
        Check(hub.Snapshot().Count == 0, "Stopped hub retained or admitted addresses.");
        hub.Start(); watcher.Emit(3, Frame()); Check(hub.Snapshot().Count == 1, "Restart failed.");
        hub.Dispose(); watcher.Emit(4, Frame());
        Check(hub.Snapshot().Count == 0, "Disposed hub admitted a late callback.");
    });

    private static Task LinearAllocation() => Sync(() =>
    {
        using var hub = new BleAdvertisementHub();
        using var discovery = new WindowsBleAdvertisementDiscovery(hub); discovery.Start();
        var watcher = BluetoothLEAdvertisementWatcher.Last;
        for (ulong i = 1; i < 200; i++) watcher.Emit(i, Frame());
        var before = GC.GetAllocatedBytesForCurrentThread(); watcher.Emit(200, Frame());
        var allocated = GC.GetAllocatedBytesForCurrentThread() - before;
        Console.WriteLine($"  200th address allocation: {allocated:N0} bytes (previous isolated baseline: 720,072)");
        Check(allocated < 200_000, "Per-address snapshot allocation returned to the quadratic baseline.");
    });

    private static async Task Coalescing()
    {
        var entered = Signal(); var release = Signal(); var processed = new List<int>();
        var active = 0; var maximum = 0;
        await using var worker = new LatestAsyncWorker<int>(async (value, token) =>
        {
            maximum = Math.Max(maximum, Interlocked.Increment(ref active));
            processed.Add(value);
            if (value == 0) { entered.TrySetResult(); await release.Task.WaitAsync(token); }
            Interlocked.Decrement(ref active);
        }, exception => throw new InvalidOperationException("Unexpected worker error", exception));
        worker.Post(0); await entered.Task;
        for (var i = 1; i <= 10_000; i++) worker.Post(i);
        Check(processed.Count == 1, "Started concurrent operations while blocked.");
        release.SetResult(); await worker.Completion;
        Check(processed.SequenceEqual(new[] { 0, 10_000 }) && maximum == 1, "Pending snapshots were not coalesced.");
    }

    private static async Task CancelWorker()
    {
        var entered = Signal(); var count = 0;
        var worker = new LatestAsyncWorker<int>(async (_, token) =>
        {
            Interlocked.Increment(ref count); entered.SetResult(); await Task.Delay(-1, token);
        }, _ => { });
        worker.Post(1); await entered.Task; worker.Post(2);
        await worker.DisposeAsync();
        Check(count == 1 && !worker.Post(3), "Disposal did not drop pending work.");
    }

    private static async Task WorkerFailure()
    {
        var errors = 0; var recovered = Signal();
        await using var worker = new LatestAsyncWorker<int>((value, _) =>
        {
            if (value == 1) throw new InvalidOperationException("Expected test failure");
            recovered.SetResult(); return Task.CompletedTask;
        }, _ => Interlocked.Increment(ref errors));
        worker.Post(1); await worker.Completion;
        worker.Post(2); await recovered.Task; await worker.Completion;
        Check(errors == 1, "Failure was not reported exactly once.");
    }

    private static async Task ConcurrentProducers()
    {
        var active = 0; var max = 0; var last = 0;
        await using var worker = new LatestAsyncWorker<int>(async (value, _) =>
        {
            var current = Interlocked.Increment(ref active);
            max = Math.Max(max, current); last = value; await Task.Yield(); Interlocked.Decrement(ref active);
        }, _ => { });
        await Task.WhenAll(Enumerable.Range(0, 8).Select(producer => Task.Run(() =>
        {
            for (var i = 0; i < 1000; i++) worker.Post(producer * 1000 + i);
        })));
        worker.Post(99_999); await worker.Completion;
        Check(max == 1 && last == 99_999, "Concurrent signals lost the final snapshot or overlapped work.");
    }

    private static async Task ControllerBurst()
    {
        var entered = Signal(); var release = Signal(); var started = 0;
        using var monitor = new FakeMonitor(async (version, token) =>
        {
            if (Interlocked.Increment(ref started) == 1)
            { entered.SetResult(); await release.Task.WaitAsync(token); }
            return new[] { Candidate(version.ToString()) };
        });
        using var controller = new CommunicationController([], [monitor]);
        var delivered = new ConcurrentQueue<string>(); var latest = Signal();
        controller.DevicesChanged += (_, args) =>
        {
            delivered.Enqueue(args.Devices.Single().StableId);
            if (args.Devices[0].StableId == "10000") latest.TrySetResult();
        };
        monitor.Emit(0); await entered.Task;
        for (var i = 1; i <= 10_000; i++) monitor.Emit(i);
        Check(started == 1, "Communication controller started unbounded rediscoveries.");
        release.SetResult(); await latest.Task;
        Check(started == 2 && delivered.SequenceEqual(new[] { "0", "10000" }), "Latest discovery was lost or redundant snapshots were delivered.");
    }

    private static async Task ControllerDisposal()
    {
        var entered = Signal(); var canceled = Signal(); var deliveries = 0;
        using var monitor = new FakeMonitor(async (_, token) =>
        {
            entered.SetResult();
            try { await Task.Delay(-1, token); }
            finally { canceled.TrySetResult(); }
            return Array.Empty<DeviceCandidate>();
        });
        var controller = new CommunicationController([], [monitor]);
        controller.DevicesChanged += (_, _) => Interlocked.Increment(ref deliveries);
        monitor.Emit(1); await entered.Task;
        controller.Dispose(); await canceled.Task;
        Check(monitor.Disposed && deliveries == 0, "Disposed controller still publishes results.");
    }

    private static async Task ManualDiscovery()
    {
        using var a = new FakeMonitor((_, _) => Task.FromResult<IReadOnlyList<DeviceCandidate>>(
            new[] { Candidate("1", "Zulu"), Candidate("2", "Alpha") }));
        using var b = new FakeMonitor((_, _) => Task.FromResult<IReadOnlyList<DeviceCandidate>>(
            new[] { Candidate("1", "Duplicate") }));
        using var controller = new CommunicationController([], [a, b]);
        var devices = await controller.DiscoverAsync(default);
        Check(devices.Select(device => device.StableId).SequenceEqual(new[] { "2", "1" }), "Manual discovery sorting/deduplication changed.");
    }

    private static Task MbbProtocolVectors() => Sync(() =>
    {
        byte[] payload = [0x01, 0x01, 0x05, 0x02, 0x02, 0x10, 0x20];
        byte[] expected = [0x5A, 0x00, 0x0A, 0x00, 0x2B, 0x49, 0x01, 0x01, 0x05, 0x02, 0x02, 0x10, 0x20, 0xB9, 0xD4];
        var codec = new MbbFrameCodec();
        Check(codec.Encode(0x2B49, payload).AsSpan().SequenceEqual(expected), "MBB encoding differs from known vector.");
        var frame = codec.Decode(expected).Single();
        Check(frame.Command == 0x2B49 && frame.Payload.Span.SequenceEqual(payload), "MBB decoding differs from known vector.");
        Check(MbbFrameCodec.Crc16Ccitt("123456789"u8) == 0x31C3, "CRC16/XMODEM standard vector failed.");
    });

    private static Task UiBurst() => Sync(() =>
    {
        var queue = new Queue<Action>();
        var delivered = new List<int>();
        using var worker = new OppoPodsManager.UI.LatestUiUpdate<int>(queue.Enqueue, delivered.Add);
        for (var i = 0; i < 10_000; i++) worker.Post(i);
        Check(queue.Count == 1, "Dispatcher retained a callback for each update.");
        queue.Dequeue()();
        Check(delivered.SequenceEqual(new[] { 9999 }) && queue.Count == 0, "Latest UI state was lost.");
    });

    private static Task UiDisposal() => Sync(() =>
    {
        var queue = new Queue<Action>();
        var worker = new OppoPodsManager.UI.LatestUiUpdate<byte[]>(queue.Enqueue, _ => throw new InvalidOperationException("Disposed callback ran"));
        var value = new byte[1024 * 1024];
        worker.Post(value); worker.Dispose(); worker.Post([]);
        queue.Dequeue()();
        Check(queue.Count == 0, "Disposed UI worker retained work.");
    });

    private static Task UiReentrancy() => Sync(() =>
    {
        var queue = new Queue<Action>();
        var delivered = new List<int>();
        OppoPodsManager.UI.LatestUiUpdate<int>? worker = null;
        worker = new(queue.Enqueue, value => { delivered.Add(value); if (value == 1) { worker!.Post(2); worker.Post(3); throw new InvalidOperationException("Test"); } });
        using (worker)
        {
            worker.Post(1);
            try { queue.Dequeue()(); } catch (InvalidOperationException) { }
            Check(queue.Count == 1, "Reentrant UI updates created multiple callbacks.");
            queue.Dequeue()();
            Check(delivered.SequenceEqual(new[] { 1, 3 }), "Apply error stranded pending UI state.");
        }
    });

    private sealed class ManualClock : TimeProvider
    {
        private long _timestamp;
        public override long TimestampFrequency => TimeSpan.TicksPerSecond;
        public override long GetTimestamp() => _timestamp;
        public void Advance(TimeSpan duration) => _timestamp += duration.Ticks;
    }

    private sealed class FakeMonitor(Func<int, CancellationToken, Task<IReadOnlyList<DeviceCandidate>>> discover) : IDeviceDiscoveryMonitor
    {
        private int _version;
        public bool Disposed { get; private set; }
        public event EventHandler<DeviceCandidatesChangedEventArgs>? DevicesChanged;
        public Task<IReadOnlyList<DeviceCandidate>> DiscoverAsync(CancellationToken token) => discover(Volatile.Read(ref _version), token);
        public void Emit(int version)
        {
            Volatile.Write(ref _version, version);
            DevicesChanged?.Invoke(this, new([], version));
        }
        public void Start() { }
        public void Dispose() => Disposed = true;
    }
}
