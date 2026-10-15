using System.Reflection;
using OppoPodsManager.Control.Brands.Oppo;
using OppoPodsManager.Control.Core;
using OppoPodsManager.Control.Core.Models;

internal static class NoisePresentationRegression
{
    public static async Task RunAsync()
    {
        await using var manager = new OppoManager();
        var state = (BusinessState)typeof(BrandManagerBase).GetProperty("State", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(manager)!;
        foreach (var level in new[] { NoiseMode.Light, NoiseMode.Medium, NoiseMode.Deep })
        {
            state.SetNoise(new(NoiseMode.Smart, level));
            if (manager.Presentation.CurrentNoiseModeKey != "Smart")
                throw new InvalidOperationException("Automatic ANC follows its changing real-time level instead of staying selected.");
        }
        Console.WriteLine("PASS Automatic ANC stays selected while real-time levels change");
        foreach (var (mode, key) in new[] { (NoiseMode.Transparency, "Transparency"), (NoiseMode.Deep, "Deep"), (NoiseMode.Off, "Off") })
        {
            state.SetNoise(new(mode, null));
            if (manager.Presentation.CurrentNoiseModeKey != key)
                throw new InvalidOperationException("Manual ANC selection no longer follows the device state.");
        }
        Console.WriteLine("PASS Transparency, manual ANC and off still follow device reports");
    }
}
