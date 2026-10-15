using System.Diagnostics;
using System.Reflection;
using OppoPodsManager.Infrastructure;

internal static class SingleInstanceRegression
{
    public static int Child(string[] args)
    {
        using var instance = new SingleInstance(args[1]);
        if (args[0] == "--instance-abandon")
        {
            if (!instance.IsPrimary) return 4;
            Console.WriteLine("READY"); Console.Out.Flush();
            Thread.Sleep(System.Threading.Timeout.Infinite);
        }
        if (instance.IsPrimary) return 5;
        return instance.ActivateExistingAsync().GetAwaiter().GetResult() ? 0 : 6;
    }

    private static Process Start(string mode, string id)
    {
        var start = new ProcessStartInfo(Environment.ProcessPath!) { RedirectStandardOutput = true, RedirectStandardError = true };
        start.ArgumentList.Add(Assembly.GetExecutingAssembly().Location);
        start.ArgumentList.Add(mode); start.ArgumentList.Add(id);
        return Process.Start(start)!;
    }

    private static void Notify(string id)
    {
        using var child = Start("--instance-notify", id);
        if (!child.WaitForExit(8000)) { child.Kill(true); throw new Exception("Secondary launch hung."); }
        if (child.ExitCode != 0) throw new Exception("Secondary launch became another owner or failed to activate: " + child.ExitCode);
    }

    // Keep acquisition and ReleaseMutex on the same synchronous worker thread.
    public static Task NotifyBeforeReady() => Task.Run(() =>
    {
        var id = Guid.NewGuid().ToString("N");
        using var primary = new SingleInstance(id);
        if (!primary.IsPrimary) throw new Exception("First launch lost ownership.");
        Notify(id);
        using var ready = new ManualResetEventSlim();
        primary.SetActivationHandler(() => ready.Set());
        if (!ready.Wait(3000)) throw new Exception("Startup activation was lost before UI initialization.");
    });

    public static Task BurstAndExit() => Task.Run(() =>
    {
        var id = Guid.NewGuid().ToString("N");
        using (var primary = new SingleInstance(id))
        {
            var count = 0;
            primary.SetActivationHandler(() => Interlocked.Increment(ref count));
            for (var i = 0; i < 5; i++) Notify(id);
            if (!SpinWait.SpinUntil(() => Volatile.Read(ref count) == 5, 3000))
                throw new Exception("Repeated activation stopped reaching the existing process.");
        }
        using var restarted = new SingleInstance(id);
        if (!restarted.IsPrimary) throw new Exception("Clean exit left the application locked.");
    });

    public static Task AbandonedOwner() => Task.Run(() =>
    {
        var id = Guid.NewGuid().ToString("N");
        using var child = Start("--instance-abandon", id);
        try
        {
            if (child.StandardOutput.ReadLineAsync().WaitAsync(TimeSpan.FromSeconds(5)).GetAwaiter().GetResult() != "READY")
                throw new Exception("Owner child did not become ready.");
        }
        finally { if (!child.HasExited) child.Kill(true); child.WaitForExit(); }
        using var restarted = new SingleInstance(id);
        if (!restarted.IsPrimary) throw new Exception("A crashed owner permanently locked the application.");
    });
}
