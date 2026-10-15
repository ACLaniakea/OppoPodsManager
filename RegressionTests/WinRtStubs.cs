// Only this test executable compiles these doubles. No radio, registry, UI or installed app is used.
namespace Windows.Storage.Streams
{
    public interface IBuffer { uint Length { get; } byte[] Bytes { get; } }
    public sealed class Buffer(byte[] bytes) : IBuffer
    {
        public uint Length => (uint)bytes.Length;
        public byte[] Bytes => bytes;
    }
    public sealed class DataReader : IDisposable
    {
        private readonly byte[] _bytes;
        public static int Created, Disposed;
        private DataReader(IBuffer buffer) { _bytes = buffer.Bytes; Created++; }
        public static DataReader FromBuffer(IBuffer buffer) => new(buffer);
        public uint UnconsumedBufferLength => (uint)_bytes.Length;
        public void ReadBytes(byte[] bytes) => _bytes.CopyTo(bytes, 0);
        public void Dispose() => Disposed++;
    }
}
namespace Windows.Devices.Bluetooth.Advertisement
{
    public sealed class BluetoothLEManufacturerData
    {
        public ushort CompanyId { get; set; }
        public Windows.Storage.Streams.IBuffer Data { get; set; } = new Windows.Storage.Streams.Buffer([]);
    }
    public sealed class BluetoothLEAdvertisement
    {
        public List<BluetoothLEManufacturerData> ManufacturerData { get; } = [];
        public string? LocalName { get; set; }
    }
    public sealed class BluetoothLEAdvertisementFilter { public BluetoothLEAdvertisement? Advertisement { get; set; } }
    public sealed class BluetoothLEAdvertisementReceivedEventArgs : EventArgs
    {
        public BluetoothLEAdvertisement Advertisement { get; set; } = new();
        public ulong BluetoothAddress { get; set; }
    }
    public delegate void ReceivedHandler(BluetoothLEAdvertisementWatcher sender, BluetoothLEAdvertisementReceivedEventArgs args);
    public sealed class BluetoothLEAdvertisementWatcher
    {
        public static BluetoothLEAdvertisementWatcher Last { get; private set; } = null!;
        public BluetoothLEAdvertisementWatcher() => Last = this;
        public BluetoothLEAdvertisementFilter? AdvertisementFilter { get; set; }
        public event ReceivedHandler? Received;
        public void Start() { }
        public void Stop() { }
        // Deliberately allow late callbacks after Stop, as real OS callbacks can race shutdown.
        public void Emit(ulong address, byte[] bytes, string? name = "", ushort company = 0x004C)
        {
            var advertisement = new BluetoothLEAdvertisement { LocalName = name };
            advertisement.ManufacturerData.Add(new() { CompanyId = company, Data = new Windows.Storage.Streams.Buffer(bytes) });
            Received?.Invoke(this, new() { BluetoothAddress = address, Advertisement = advertisement });
        }
    }
}
namespace OppoPodsManager.Control.Subsystems.Logging
{
    public sealed class ApplicationLog
    {
        public static ApplicationLog? Current => null;
        public void Debug(string category, string message) { }
        public void Error(string category, string message, Exception exception) { }
    }
}
