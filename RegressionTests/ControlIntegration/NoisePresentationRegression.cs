using System.Reflection;
using OppoPodsManager.Control.Brands.Oppo;
using OppoPodsManager.Control.Core;
using OppoPodsManager.Control.Core.Models;
using OppoPodsManager.Assets.Oplus;
using OppoPodsManager.Communication.Abstractions;
using OppoPodsManager.Control.Brands.Oppo.Commands;
using OppoPodsManager.Control.Brands.Oppo.Features;
using OppoPodsManager.Control.Brands.Oppo.Managers;
using OppoPodsManager.Control.Brands.Oppo.Models;
using OppoPodsManager.Control.Core.Transport;

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
        await CatalogAndProtocol();
    }

    private static async Task CatalogAndProtocol()
    {
        var capability = new CapabilityLoader(DeviceModelData.LoadCatalog()).Load("067410", null,
            new HashSet<ushort> { CommandId.NoiseCancellation, CommandId.SetNoiseCancellation });
        var options = NoiseCancellation.BuildOptions(capability);
        var anc = options.Single(option => option.Key == "NC");
        var transparency = options.Single(option => option.Key == "Transparency");
        if (anc.Children.Any(child => transparency.Children.Any(other => child.Key == other.Key))
            || !transparency.Children.Any(child => child.Mode == NoiseMode.VocalTransparency))
            throw new Exception("Catalog aliases transparency submodes to automatic ANC.");
        Console.WriteLine("PASS Real catalog keeps automatic ANC and voice transparency keys distinct");

        await using var manager = new OppoManager();
        var state = (BusinessState)typeof(BrandManagerBase).GetProperty("State", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(manager)!;
        typeof(OppoManager).GetProperty("Capability")!.SetValue(manager, capability);
        var raw = new LoopbackNoiseConnection();
        var router = new FrameRouter();
        var link = new ConnectionLink(raw, new FrameCodec(), router);
        typeof(BrandManagerBase).GetProperty("Link", BindingFlags.Instance | BindingFlags.NonPublic)!.SetValue(manager, link);
        using var notifier = new Notifier(link, router, false);
        using var noise = new NoiseCancellation(state, capability, notifier);
        typeof(OppoManager).GetField("_noiseCancellation", BindingFlags.Instance | BindingFlags.NonPublic)!.SetValue(manager, noise);
        noise.Apply(new byte[] { 3, 1, 1, 0, 2 });
        if (state.Snapshot().Noise.Mode != NoiseMode.VocalTransparency)
            throw new Exception("Voice-transparency notification decoded as automatic ANC.");
        Console.WriteLine("PASS Real voice-transparency notification retains its own mode");

        foreach (var (key, payload, mode) in new[] {
            ("Smart", "010180", NoiseMode.Smart),
            ("Transparency", "01010001", NoiseMode.Transparency),
            ("VocalTransparency", "01010002", NoiseMode.VocalTransparency),
            ("Off", "010108", NoiseMode.Off) })
        {
            if (!await manager.SetNoiseCancellationByKeyAsync(key, default)
                || Convert.ToHexString(raw.LastWrite) != payload || manager.Snapshot.Noise.Mode != mode)
                throw new Exception("Wrong wire command or readback for mode: " + key);
        }
        Console.WriteLine("PASS Actual mode-key commands emit distinct wire bytes and round-trip device state");
    }

    private sealed class LoopbackNoiseConnection : IRawConnection
    {
        private readonly FrameCodec _codec = new();
        private byte _first = 8, _second;
        public byte[] LastWrite { get; private set; } = [];
        public bool IsConnected => true;
        public event EventHandler<ReadOnlyMemory<byte>>? DataReceived;
        public event EventHandler? Disconnected { add { } remove { } }
        public Task ConnectAsync(CancellationToken token) => Task.CompletedTask;
        public Task DisconnectAsync(CancellationToken token) => Task.CompletedTask;
        public Task SendAsync(ReadOnlyMemory<byte> data, CancellationToken token)
        {
            foreach (var frame in _codec.Decode(data.Span))
            {
                if (frame.Command == CommandId.SetNoiseCancellation)
                {
                    LastWrite = frame.Payload.ToArray(); _first = LastWrite[2];
                    _second = LastWrite.Length > 3 ? LastWrite[3] : (byte)0;
                }
                else if (frame.Command == CommandId.NoiseCancellation)
                    DataReceived?.Invoke(this, _codec.Encode(CommandId.NoiseCancellationResponse,
                        new byte[] { 0, 1, 1, _first, _second }));
            }
            return Task.CompletedTask;
        }
        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }
}
