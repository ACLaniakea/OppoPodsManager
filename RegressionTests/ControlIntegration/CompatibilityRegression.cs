using OppoPodsManager.Assets.Oplus;
using OppoPodsManager.Communication.Abstractions;
using OppoPodsManager.Control.Abstractions;
using OppoPodsManager.Control.Brands.Oppo.Features;
using OppoPodsManager.Control.Brands.Oppo.Models;
using OppoPodsManager.Control.Core.Features;
using OppoPodsManager.Control.Core.Models;
using OppoPodsManager.Control.Core.Transport;

internal static class CompatibilityRegression
{
    private static void Check(bool value, string message) { if (!value) throw new InvalidOperationException(message); }
    public static void Run()
    {
        var capability = new CapabilityLoader(DeviceModelData.LoadCatalog()).Load("067410", "OPPO Enco X3",
            new HashSet<ushort> { CommandId.CurrentEqualizer, CommandId.SetEqualizer, CommandId.GameSound, CommandId.SetGameSound });
        Check(capability.IsKnownModel && capability.ModelName == "OPPO Enco X3", "X3 identity changed.");
        Check(capability.GameSoundBlocksEqualizer && capability.SupportsCustomEqualizer, "Original model capabilities changed.");
        Check(!FeatureSwitches.ResolveControlEnabledStates(capability, FeatureStateSnapshot.Empty, new GameSnapshot(null, 3))["equalizer"], "Game sound no longer enforces the original EQ restriction.");
        Check(FeatureSwitches.ResolveControlEnabledStates(capability, FeatureStateSnapshot.Empty, new GameSnapshot(null, 0))["equalizer"], "EQ does not recover after game sound is off.");
        Console.WriteLine("PASS Actual X3 catalog and original game-sound/EQ restriction, including recovery");

        var current = new ConnectedDeviceSnapshot("PC", "WUJIE14", 0, 2, true, false, false);
        var phone = new ConnectedDeviceSnapshot("PHONE", "Phone", 0, 0, false, false, false);
        var snapshot = MultiDeviceSnapshot.Empty with { Devices = new[] { current, phone } };
        var display = MultiDevicePolicy.BuildDisplayState(snapshot, new HashSet<string> { "PC", "PHONE" });
        Check(display.VisibleDevices.Count == 1 && display.VisibleDevices[0].Name == "WUJIE14", "Original remembered-host filtering changed.");
        Check(MultiDevicePolicy.BuildDisplayState(snapshot, new HashSet<string>()).VisibleDevices.Count == 2, "Restore no longer reveals remembered hosts.");
        Console.WriteLine("PASS Original sidebar shows remembered hosts, preserves the current PC and hidden preferences");

        IBrandManagerFactory[] factories = [new OppoPodsManager.Control.Brands.Oppo.OppoManagerFactory(),
            new OppoPodsManager.Control.Brands.Vivo.VivoManagerFactory(), new OppoPodsManager.Control.Brands.Huawei.HuaweiManagerFactory(),
            new OppoPodsManager.Control.Brands.Xiaomi.XiaomiManagerFactory(), new OppoPodsManager.Control.Brands.Edifier.EdifierManagerFactory()];
        foreach (var factory in factories)
            Check(factory.SupportsTransport("rfcomm") && factory.SupportsTransport("gatt") && !factory.SupportsTransport(TransportNames.BleAdvertisement), "Classic brand transport contract regressed: " + factory.Brand);
        var apple = new OppoPodsManager.Control.Brands.Apple.AppleManagerFactory();
        Check(apple.SupportsTransport(TransportNames.BleAdvertisement) && !apple.SupportsTransport("rfcomm"), "Apple transport contract regressed.");
        Console.WriteLine("PASS Actual brand factories retain classic/GATT paths and isolate passive advertisements");
    }
}
