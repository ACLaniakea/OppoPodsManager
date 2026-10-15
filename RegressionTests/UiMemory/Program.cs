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
using SmallWindow = OppoPodsManager.UI.MiniWindow.Status.SmallWindow;
using MainWindow = OppoPodsManager.UI.MainWindow.MainWindow;

internal static class Program
{
    private static readonly Dictionary<string, object> Metrics = new();
    private static bool Baseline;
    private static void Check(bool condition, string message)
    { if (!Baseline && !condition) throw new InvalidOperationException(message); }
    private static object? Invoke(object target, string name, params object?[] args)
        => target.GetType().GetMethod(name, BindingFlags.NonPublic | BindingFlags.Public | BindingFlags.Instance)!.Invoke(target, args);
    private static void Set(object target, Type owner, string name, object value)
        => owner.GetField(name, BindingFlags.NonPublic | BindingFlags.Instance)!.SetValue(target, value);
    private static void Close(MainWindow window)
    {
        // Close the actual Avalonia window as well as its application resources.
        // Calling only the private disposal helpers leaves the platform window alive.
        window.Close();
        Dispatcher.UIThread.RunJobs();
    }
    public static int Main(string[] args)
    {
        Baseline = args.Contains("--baseline");
        try
        {
            AppBuilder.Configure<TestApp>().UseHeadless(new AvaloniaHeadlessPlatformOptions()).SetupWithoutStarting();
            var assembly = typeof(MainWindow).Assembly;
            var optimized = assembly.GetType("OppoPodsManager.UI.LatestUiUpdate`1") is not null;
            Console.WriteLine("APPLICATION " + assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion);
            if (optimized == Baseline) throw new InvalidOperationException("Wrong application assembly loaded for comparison.");
            var errors = new List<Exception>();
            foreach (var test in new Action[] { WindowLifetime, Sidebar, LogRefresh })
                try { test(); } catch (Exception error) { errors.Add(error); Console.WriteLine(error); }
            if (!Baseline) try { NoSupplementalHints(); ExclusiveWindows(); } catch (Exception error) { errors.Add(error); Console.WriteLine(error); }
            if (!Baseline)
                foreach (var mini in new[] { false, true })
                    foreach (var fail in new[] { false, true })
                        foreach (var grouped in new[] { false, true })
                        try { NoiseSelection(mini, fail, grouped); } catch (Exception error) { errors.Add(error); Console.WriteLine(error); }
            Console.WriteLine("METRICS " + JsonSerializer.Serialize(Metrics));
            if (errors.Count != 0) return 1;
            Console.WriteLine(Baseline ? "Baseline measured" : "13/13 UI regression checks passed");
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
        Console.WriteLine("MEASURED Closed main-window lifetime");
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
        Console.WriteLine("MEASURED Sidebar allocation and changed-device refresh");
    }
    private static void LogRefresh()
    {
        var previous = Console.Error;
        var directory = Path.Combine(Path.GetTempPath(), "oppo-ui-log-" + Guid.NewGuid().ToString("N"));
        try
        {
            using var log = new ApplicationLog(directory);
            Console.SetError(TextWriter.Null);
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
        Console.WriteLine("MEASURED Full log buffer freshness and idle refresh allocation");
    }
    private static void NoiseSelection(bool mini, bool fail, bool grouped)
    {
        var directory = Path.Combine(Path.GetTempPath(), "oppo-noise-test-" + Guid.NewGuid().ToString("N"));
        var previousError = Console.Error;
        try
        {
            using var log = new ApplicationLog(directory);
            Console.SetError(TextWriter.Null);
            var (control, proxy) = Control();
            var initial = grouped ? NoiseMode.Smart : NoiseMode.Off;
            proxy.Snapshot = proxy.Snapshot with { Noise = new(initial, null) };
            if (grouped)
            {
                var capability = new OppoPodsManager.Control.Brands.Oppo.Models.CapabilityLoader(OppoPodsManager.Assets.Oplus.DeviceModelData.LoadCatalog())
                    .Load("067410", null, new HashSet<ushort> { 0x010C, 0x0404 });
                proxy.NoiseOptions = OppoPodsManager.Control.Brands.Oppo.Features.NoiseCancellation.BuildOptions(capability);
            }
            proxy.NoiseCommand = new();
            var frontend = new FrontendState(); frontend.Publish(proxy.Snapshot);
            var dispatcher = new CommandDispatcher(control, log);
            Avalonia.Controls.Control view;
            if (mini) view = new SmallWindow(frontend, control, dispatcher);
            else
            {
                var home = new HomeView();
                home.Attach(control, new OppoPodsManager.Assets.UserSettings.SettingsStore(null), log, dispatcher, frontend, null);
                home.ApplySnapshot(proxy.Snapshot); view = home;
            }
            try
            {
                var options = proxy.NoiseOptions;
                Invoke(view, "SwitchAncMain", options.Single(option => option.Key == "Transparency"));
                var hint = view.FindControl<TextBlock>("AncRealtimeHint")!;
                var row = view.FindControl<Grid>("AncMainRow")!;
                string Selected() => (string)view.GetType().GetField("_ancMain", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(view)!;
                void Apply()
                {
                    if (view is HomeView home) home.ApplySnapshot(proxy.Snapshot);
                    else Invoke(view, "ApplyNextSnapshot", proxy.Snapshot);
                }
                for (var i = 0; i < 20; i++)
                {
                    proxy.Snapshot = proxy.Snapshot with { Revision = i + 1, LeftBattery = new((byte)i, false) };
                    Apply();
                }
                Check(Selected() == "Transparency" && !hint.IsVisible && !row.IsEnabled && proxy.LastNoiseKey == "Transparency",
                    "Old battery notifications made the pending noise selection jump back.");
                Invoke(view, "SwitchAncMain", options.Single(option => option.Key == "NC"));
                Check(proxy.NoiseCommandCount == 1, "A second ANC write raced the pending command.");
                if (!fail) proxy.Snapshot = proxy.Snapshot with { Noise = new(NoiseMode.Transparency, null) };
                proxy.NoiseCommand.SetResult(!fail);
                // Execute captured UI continuations without starting any real application.
                Dispatcher.UIThread.RunJobs();
                Check(Selected() == (fail ? (grouped ? "NC" : "Off") : "Transparency") && row.IsEnabled && !hint.IsVisible,
                    "Completed ANC command did not reconcile the real device state or release its pending UI.");
                // Once finished, genuine physical-device changes must still update the display.
                proxy.Snapshot = proxy.Snapshot with { Noise = new(NoiseMode.Deep, null) }; Apply();
                Check(Selected() == "NC", "Physical ANC changes stopped updating after a command.");
            }
            finally
            {
                if (view is Window window) window.Close();
                else ((HomeView)view).MarkClosed();
                control.DisposeAsync().AsTask().GetAwaiter().GetResult();
            }
        }
        finally
        {
            Console.SetError(previousError);
            if (Directory.Exists(directory)) Directory.Delete(directory, true);
        }
        Console.WriteLine($"PASS {(mini ? "Mini" : "Main")} {(grouped ? "real catalog" : "generic")} ANC stays selected during notifications; {(fail ? "failure restores device state" : "confirmation and physical changes update it")}");
    }

    private static void NoSupplementalHints()
    {
        var home = new HomeView(); var eq = new EqView(); var window = new MainWindow();
        try
        {
            Check(home.FindControl<TextBlock>("NearbyDevicesHint") is null
                && eq.FindControl<TextBlock>("EqAvailabilityHint") is null
                && window.FindControl<TextBlock>("DeviceListHiddenHint") is null,
                "Removed supplemental footnotes still occupy the layout.");
            var options = new[] { new DeviceConnectionOption("connected", "Generic headset"), new DeviceConnectionOption("nearby", "AirPods", true) };
            Invoke(home, "ApplyNextDevices", (object)options);
            Check(home.FindControl<ComboBox>("CbDevice")!.Items.Count == 2, "Removing hints changed the original selectable devices.");
        }
        finally { Close(window); home.MarkClosed(); }
        Console.WriteLine("PASS Supplemental footnotes removed while device choices remain available");
    }
    private static void ExclusiveWindows()
    {
        var main = new Window { Width = 400, Height = 300 };
        var mini = new SmallWindow();
        var tray = (OppoPodsManager.UI.Tray.TrayIconController)RuntimeHelpers.GetUninitializedObject(typeof(OppoPodsManager.UI.Tray.TrayIconController));
        Set(tray, tray.GetType(), "_mainWindow", (Func<Window?>)(() => main));
        Set(tray, tray.GetType(), "_hideMainWindow", (Action)(() => main.Hide()));
        Set(tray, tray.GetType(), "_miniWindow", mini);
        try
        {
            main.Show(); Invoke(tray, "ToggleMiniWindow");
            Check(!main.IsVisible && mini.IsVisible, "Opening the mini window left the main window visible.");
            for (var i = 0; i < 5; i++) Invoke(tray, "ShowMainWindow");
            Check(main.IsVisible && !mini.IsVisible, "Repeated activation did not reuse the main window exclusively.");
        }
        finally { main.Close(); mini.Close(); Invoke(mini, "DisposeSukiWindow"); }
        Console.WriteLine("PASS Main and mini windows are mutually exclusive and activation reuses the main window");
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
    public TaskCompletionSource<bool>? NoiseCommand { get; set; }
    public int NoiseCommandCount { get; private set; }
    public string? LastNoiseKey { get; private set; }
    public IReadOnlyList<NoiseOptionModel> NoiseOptions { get; set; } = [
        new("Off", NoiseMode.Off, 1, []),
        new("NC", NoiseMode.NoiseCancellation, 2, [new("Smart", NoiseMode.Smart, 3, []), new("Deep", NoiseMode.Deep, 4, [])]),
        new("Transparency", NoiseMode.Transparency, 5, [])];
    private Task<bool> SetNoise(string key) { LastNoiseKey = key; NoiseCommandCount++; return NoiseCommand?.Task ?? Task.FromResult(false); }
    private readonly DeviceCapability _capability = DeviceCapability.Unknown with { ModelName = "Generic headset", IsKnownModel = true, GameSoundMutexes = new HashSet<int> { 1 }, SupportedFeatures = new HashSet<string> { "custom-equalizer" } };
    protected override object? Invoke(MethodInfo? method, object?[]? args) => method!.Name switch
    {
        "SetNoiseCancellationByKeyAsync" => SetNoise((string)args![0]!),
        "DisconnectAsync" => Task.CompletedTask,
        "DisposeAsync" => ValueTask.CompletedTask,
        "get_Capability" => _capability,
        "get_Snapshot" => Snapshot,
        "get_Presentation" => new BrandPresentation("Generic headset", true, false, true, true, false, [], -6, 6, [], new HashSet<string>(), new Dictionary<string, bool>(), new Dictionary<string, bool>(), NoiseOptions, Snapshot.Noise.Mode.ToString()),
        "GetMultiDeviceDisplayState" => MultiDevicePolicy.BuildDisplayState(Snapshot.MultiDevice, (IReadOnlySet<string>)args![0]!),
        _ => null
    };
}
