namespace OppoPodsManager.Communication.Abstractions;

internal static class AppleProximityAdvertisement
{
    // Apple manufacturer data, excluding company ID: proximity pairing type 0x07,
    // 25-byte body (0x19). Other Apple traffic must not become headphone candidates.
    public static bool IsSupported(ReadOnlySpan<byte> data)
        => data.Length >= 27 && data[0] == 0x07 && data[1] == 0x19;
}
