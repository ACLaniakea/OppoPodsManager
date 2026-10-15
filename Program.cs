using Avalonia;
using Avalonia.Threading;
using OppoPodsManager.Infrastructure;

namespace OppoPodsManager;

internal static class Program
{
    private static SingleInstance? _instance;

    private static void Main(string[] args)
    {
        using var instance = new SingleInstance("OppoPodsManager.Desktop");
        if (!instance.IsPrimary)
        {
            instance.ActivateExistingAsync().GetAwaiter().GetResult();
            return;
        }
        _instance = instance;
        try { BuildAvaloniaApp().StartWithClassicDesktopLifetime(args); }
        finally { _instance = null; }
    }

    internal static void EnableActivation(Action activate)
        => _instance?.SetActivationHandler(() => Dispatcher.UIThread.Post(activate));

    public static AppBuilder BuildAvaloniaApp() => AppBuilder
        .Configure<App>()
        .UsePlatformDetect()
        .LogToTrace();
}
