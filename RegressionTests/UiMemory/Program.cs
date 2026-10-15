using System.Reflection;
using System.Runtime.CompilerServices;
using System.Text.Json;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Threading;
using OppoPodsManager.Control.Abstractions;
using OppoPodsManager.Control.Core.Features;
using OppoPodsManager.Control.Core.Models;
using OppoPodsManager.Control.Subsystems.Logging;
using OppoPodsManager.UI.Views;
using MainWindow = OppoPodsManager.UI.MainWindow.MainWindow;

internal static class Program
{
    private static readonly Dictionary<string, object> Metrics = new();
    private static bool Baseline;
    private static void Check(bool condition, string message)
    { if (!Baseline && !condition) throw new InvalidOperationException(message); }
    private static object? Invoke(object target, string name, params object?[] args)
        => target.GetType().GetMethod(name, BindingFlags.NonPublic | BindingFlags.Instance)!.Invoke(target, args);
    private static void Set(object target, Type owner, string name, object value)
        => owner.GetField(name, BindingFlags.NonPublic | BindingFlags.Instance)!.SetValue(target, value);
    private static void Close(MainWindow window)
    {
        Invoke(window, "DisposeRuntimeUiResources");
        Invoke(window, "DisposeSukiWindow");
        Dispatcher.UIThread.RunJobs();
    }
    public static int Main(string[] args)
    {
        Baseline = args.Contains("--baseline");
        try
        {
            AppBuilder.Configure<TestApp>().UseHeadless(new AvaloniaHeadlessPlatformOptions()).SetupWithoutStarting();
            WindowLifetime();
            Sidebar();
            LogRefresh();
            if (!Baseline) Hints();
            Console.WriteLine("METRICS " + JsonSerializer.Serialize(Metrics));
            Console.WriteLine(Baseline ? "Baseline measured" : "8/8 UI regression checks passed");
            return 0;
        }
        catch (Exception error) { Console.WriteLine(error); return 1; }
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static List<WeakReference> CreateClosedWindows()
    {
        var references = new List<WeakReference>();
        for (var i = 0; i < 8; i++)
        {
            var window = new MainWindow();
            references.Add(new(window));
            Close(window);
        }
        return references;
    }
    private static void WindowLifetime()
    {
        // Ignore first-use framework caches, then measure independent closed windows.
        var warmup = new MainWindow(); Close(warmup);
        var references = CreateClosedWindows();
        GC.Collect(); GC.WaitForPendingFinalizers(); GC.Collect();
        var retained = references.Count(reference => reference.IsAlive);
        Metrics["closed_windows_retained_of_8"] = retained;
        Check(retained <= 1, "Closed windows remain rooted after UI disposal: " + retained);
        Console.WriteLine("PASS Closed main windows are collectible");
    }
    private static (ControlManager, UiManagerProxy) Control()
    {
        var manager = DispatchProxy.Create<IBrandManager, UiManagerProxy>();
        var proxy = (UiManagerProxy)(object)manager;
        var control = new ControlManager(new FrontendState());
        Set(control, typeof(ControlManager), "_activeManager", manager);
        return (control, proxy);
    }
    private static void Sidebar()
    {
        var (control, proxy) = Control();
        var window = new MainWindow();
        Set(window, typeof(MainWindow), "_controlManager", control);
        var multi = new MultiDeviceSnapshot([new("PC", "This PC", 0, 2, true, false, false), new("PHONE", "Phone", 0, 0, false, false, false)], true, null);
        var state = proxy.Snapshot with { MultiDevice = multi };
        proxy.Snapshot = state;
        Invoke(window, "SyncNextMultiDeviceList", state);
        var list = window.FindControl<ItemsControl>("DeviceList")!;
        var first = list.Items[0];
        var start = GC.GetAllocatedBytesForCurrentThread();
        for (var i = 0; i < 500; i++)
            Invoke(window, "SyncNextMultiDeviceList", state with { Revision = i, LeftBattery = new((byte)(i % 100), false) });
        var bytes = GC.GetAllocatedBytesForCurrentThread() - start;
        var reused = ReferenceEquals(first, list.Items[0]);
        Metrics["sidebar_500_updates_allocated_bytes"] = bytes;
        Metrics["sidebar_row_reused"] = reused;
        Check(reused && bytes < 1024 * 1024, "Unrelated battery updates rebuild the device sidebar.");
        proxy.Snapshot = state with { MultiDevice = multi with { Devices = [multi.Devices[0], multi.Devices[1] with { Name = "Renamed phone" }] } };
        Invoke(window, "SyncNextMultiDeviceList", proxy.Snapshot);
        Check(!ReferenceEquals(first, list.Items[0]) && list.Items.Count == 2, "Changed device data did not refresh the sidebar.");
        Close(window);
        Console.WriteLine("PASS Sidebar reuses rows for 500 battery updates and refreshes changed devices");
    }
    private static void LogRefresh()
    {
        var previous = Console.Error;
        var directory = Path.Combine(Path.GetTempPath(), "oppo-ui-log-" + Guid.NewGuid().ToString("N"));
        try
        {
            Console.SetError(TextWriter.Null);
            using var log = new ApplicationLog(directory);
            for (var i = 0; i < 2200; i++) log.Info("Test", "entry " + i);
            var view = new LogView();
            Invoke(view, "RefreshLogView");
            log.Info("Test", "NEWEST_ENTRY_AFTER_CAPACITY");
            Invoke(view, "RefreshLogView");
            var entries = ((System.Collections.IEnumerable)view.FindControl<ListBox>("LbLog")!.ItemsSource!).Cast<string>().ToArray();
            var fresh = entries[^1].Contains("NEWEST_ENTRY_AFTER_CAPACITY");
            var start = GC.GetAllocatedBytesForCurrentThread();
            for (var i = 0; i < 10; i++) Invoke(view, "RefreshLogView");
            var bytes = GC.GetAllocatedBytesForCurrentThread() - start;
            Metrics["rolling_log_keeps_updating"] = fresh;
            Metrics["idle_log_10_refreshes_allocated_bytes"] = bytes;
            Check(fresh && bytes < 16 * 1024, "Full log buffer is stale or unchanged logs are repeatedly formatted.");
        }
        finally { Console.SetError(previous); if (Directory.Exists(directory)) Directory.Delete(directory, true); }
        Console.WriteLine("PASS Full log buffer stays current and idle refresh avoids reformatting");
    }
    private static void Hints()
    {
        var (control, proxy) = Control();
        var eq = new EqView(); Set(eq, typeof(PageView), "ControlManager", control);
        var state = proxy.Snapshot with { Game = new GameSnapshot(null, 3) };
        eq.ApplySnapshot(state);
        var hint = eq.FindControl<TextBlock>("EqAvailabilityHint")!;
        Check(hint.IsVisible && hint.Text!.Contains("冲突") && !hint.Text.Contains("Enco"), "Generic conflict hint missing.");
        eq.ApplySnapshot(state with { Game = new GameSnapshot(null, 0) });
        Check(!hint.IsVisible, "Conflict hint did not clear.");
        eq.ApplySnapshot(state with { IsConnected = false });
        Check(hint.IsVisible && hint.Text!.Contains("连接"), "Disconnected hint missing.");
        var home = new HomeView();
        var options = new[] { new DeviceConnectionOption("connected", "Generic headset"), new DeviceConnectionOption("nearby", "AirPods", true) };
        Invoke(home, "ApplyNextDevices", (object)options);
        var nearby = home.FindControl<TextBlock>("NearbyDevicesHint")!;
        var combo = home.FindControl<ComboBox>("CbDevice")!;
        var text = (TextBlock)combo.ItemTemplate!.Build(options[1])!;
        Check(nearby.IsVisible && text.Text!.Contains("附近广播") && combo.Items.Count == 2, "Nearby labels changed original choices.");
        Invoke(home, "ApplyNextDevices", (object)new[] { options[0] });
        Check(!nearby.IsVisible, "Nearby hint did not clear.");
        Console.WriteLine("PASS Five existing generic conflict and nearby-device UI checks");
    }
}

public sealed class TestApp : Application
{
    public override void Initialize()
    {
        Styles.Add(new Avalonia.Themes.Fluent.FluentTheme());
        Styles.Add(new SukiUI.SukiTheme());
    }
}
public class UiManagerProxy : DispatchProxy
{
    public BusinessSnapshot Snapshot { get; set; } = new FrontendState().Snapshot with { DeviceName = "Generic headset", IsConnected = true };
    private readonly DeviceCapability _capability = DeviceCapability.Unknown with { ModelName = "Generic headset", IsKnownModel = true, GameSoundMutexes = new HashSet<int> { 1 }, SupportedFeatures = new HashSet<string> { "custom-equalizer" } };
    protected override object? Invoke(MethodInfo? method, object?[]? args) => method!.Name switch
    {
        "get_Capability" => _capability,
        "get_Snapshot" => Snapshot,
        "get_Presentation" => new BrandPresentation("Generic headset", true, false, true, false, false, [], -6, 6, [], new HashSet<string>(), new Dictionary<string, bool>(), new Dictionary<string, bool>(), [], ""),
        "GetMultiDeviceDisplayState" => MultiDevicePolicy.BuildDisplayState(Snapshot.MultiDevice, (IReadOnlySet<string>)args![0]!),
        _ => null
    };
}
