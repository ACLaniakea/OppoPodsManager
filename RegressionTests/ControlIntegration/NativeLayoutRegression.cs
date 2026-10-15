using System.Reflection;
using System.Runtime.InteropServices;
using OppoPodsManager.Communication.Windows;

internal static class NativeLayoutRegression
{
    private static void Check(bool value, string message) { if (!value) throw new InvalidOperationException(message); }
    public static void Run()
    {
        var owner = typeof(WindowsRfcommConnection);
        var query = owner.GetNestedType("WsaQuerySet", BindingFlags.NonPublic)!;
        Check(IntPtr.Size == 8, "This regression targets Windows x64.");
        Check(Marshal.SizeOf(query) == 120, "WSAQUERYSETW must be 120 bytes on x64.");
        (string Field, int Offset)[] layout = [("LpVersion",24),("LpszComment",32),("DwNameSpace",40),("LpszContext",56),("DwNumberOfProtocols",64),("LpcsaBuffer",96),("LpBlob",112)];
        foreach (var entry in layout)
            Check(Marshal.OffsetOf(query, entry.Field).ToInt32() == entry.Offset, $"Wrong native offset for {entry.Field}.");
        var socket = owner.GetNestedType("SockAddrBth", BindingFlags.NonPublic)!;
        Check(Marshal.SizeOf(socket) == 30 && Marshal.OffsetOf(socket, "Port").ToInt32() == 26, "SOCKADDR_BTH byte packing changed.");
        Check((uint)owner.GetField("LupFlushCache", BindingFlags.NonPublic | BindingFlags.Static)!.GetRawConstantValue()! == 0x2000, "FLUSHPREVIOUS was used in place of FLUSHCACHE.");
        Console.WriteLine("PASS Winsock structures and lookup flags match the Windows SDK ABI");
        foreach (var name in new[] { "WSALookupServiceBegin", "WSALookupServiceNext" })
        {
            var import = owner.GetMethod(name, BindingFlags.NonPublic | BindingFlags.Static)!.GetCustomAttribute<DllImportAttribute>()!;
            Check(import.ExactSpelling && import.EntryPoint == name + "W", "UTF-16 data was sent to the wrong native API.");
        }
        Console.WriteLine("PASS SDP queries use explicit Unicode entry points");
        var grow = owner.GetMethod("TryGetNextLookupBufferSize", BindingFlags.NonPublic | BindingFlags.Static)!;
        bool TryGrow(int current, int required, out int next)
        {
            object[] args = [current, required, 0];
            var result = (bool)grow.Invoke(null, args)!; next = (int)args[2]; return result;
        }
        Check(TryGrow(4096, 8192, out var value) && value == 8192, "Normal lookup growth failed.");
        Check(!TryGrow(4096, unchecked((int)0xEBC03300), out _), "Observed corrupt BLOB-sized request was accepted.");
        Check(!TryGrow(4096, int.MaxValue, out _) && !TryGrow(4096, 4096, out _), "Invalid lengths were accepted.");
        Check(!TryGrow(1024*1024, 1024*1024+1, out _), "Result allocation exceeded one MiB.");
        var size = 4096; var iterations = 0;
        while (TryGrow(size, size+1, out var next)) { size = next; iterations++; }
        Check(iterations <= 8 && size == 1024*1024, "Lookup retries are not bounded.");
        Console.WriteLine("PASS SDP result buffers reject corrupt lengths and grow within a bounded budget");
    }
}
