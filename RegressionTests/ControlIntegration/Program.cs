using System.Reflection;
using System.Runtime.CompilerServices;
using OppoPodsManager.Control.Abstractions;

// Exercise the actual ControlManager from the application assembly. The private event
// handler is a test injection seam; no Bluetooth, UI, settings or real application is started.
internal static class Program
{
    private static readonly MethodInfo OnPlans = typeof(ControlManager).GetMethod("OnPlansChanged", BindingFlags.NonPublic | BindingFlags.Instance)!;
    private static readonly FieldInfo Worker = typeof(ControlManager).GetField("_plansWorker", BindingFlags.NonPublic | BindingFlags.Instance)!;
    private static void Emit(ControlManager manager, long generation)
        => OnPlans.Invoke(manager, [null, new DevicePlansChangedEventArgs([], generation)]);
    private static Task Completion(ControlManager manager)
    {
        var worker = Worker.GetValue(manager)!;
        return (Task)worker.GetType().GetProperty("Completion")!.GetValue(worker)!;
    }
    private static TaskCompletionSource Signal() => new(TaskCreationOptions.RunContinuationsAsynchronously);
    private static void Check(bool condition, string message)
    { if (!condition) throw new InvalidOperationException(message); }

    private static async Task<int> Main()
    {
        try
        {
            await BoundRetainedSnapshots().WaitAsync(TimeSpan.FromSeconds(15));
            Console.WriteLine("PASS Actual ControlManager keeps only the newest pending snapshot");
            await RejectOldGenerations().WaitAsync(TimeSpan.FromSeconds(10));
            Console.WriteLine("PASS Actual ControlManager rejects out-of-order generations");
            await CancelWhileWaiting().WaitAsync(TimeSpan.FromSeconds(10));
            Console.WriteLine("PASS Actual ControlManager cancels while discovery gate is occupied");
            await SelectionRegression.RunAsync().WaitAsync(TimeSpan.FromSeconds(20));
            NativeLayoutRegression.Run();
            CompatibilityRegression.Run();
            Console.WriteLine("15/15 passed");
            return 0;
        }
        catch (Exception exception) { Console.WriteLine(exception); return 1; }
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static List<WeakReference> PostMany(ControlManager manager)
    {
        var references = new List<WeakReference>();
        for (var i = 2; i <= 10_001; i++)
        {
            var args = new DevicePlansChangedEventArgs([], i);
            references.Add(new WeakReference(args));
            OnPlans.Invoke(manager, [null, args]);
        }
        return references;
    }

    private static async Task BoundRetainedSnapshots()
    {
        await using var manager = new ControlManager(new FrontendState());
        var entered = Signal(); var release = Signal(); var delivered = 0;
        manager.AvailableDevicesChanged += (_, _) =>
        {
            if (Interlocked.Increment(ref delivered) == 1)
            {
                entered.TrySetResult(); release.Task.GetAwaiter().GetResult();
            }
        };
        Emit(manager, 1); await entered.Task;
        try
        {
            var references = PostMany(manager);
            GC.Collect(); GC.WaitForPendingFinalizers(); GC.Collect();
            var retained = references.Count(reference => reference.IsAlive);
            Console.WriteLine($"  Pending event snapshots retained after 10,000 signals: {retained}");
            Check(retained <= 3, "Control layer retained a backlog of stale snapshots.");
        }
        finally { release.TrySetResult(); }
        await Completion(manager);
        Check(delivered == 2, $"Expected current + latest, got {delivered} deliveries.");
    }

    private static async Task RejectOldGenerations()
    {
        await using var manager = new ControlManager(new FrontendState());
        var delivered = 0; manager.AvailableDevicesChanged += (_, _) => delivered++;
        Emit(manager, 20); await Completion(manager);
        Emit(manager, 19); Emit(manager, 20); await Completion(manager);
        Check(delivered == 1, "Old or duplicate generation was applied.");
    }

    private static async Task CancelWhileWaiting()
    {
        var manager = new ControlManager(new FrontendState());
        var gate = (SemaphoreSlim)typeof(ControlManager).GetField("_discoveryGate", BindingFlags.NonPublic | BindingFlags.Instance)!.GetValue(manager)!;
        await gate.WaitAsync();
        Emit(manager, 1);
        await manager.DisposeAsync();
        Check(Completion(manager).IsCompletedSuccessfully, "Pending work survived disposal.");
    }
}
