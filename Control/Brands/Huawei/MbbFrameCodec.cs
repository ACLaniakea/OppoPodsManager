using OppoPodsManager.Control.Core.Transport;

namespace OppoPodsManager.Control.Brands.Huawei;

// 华为新 HarmonyOS 智慧音频（MBB）私有帧编解码器，实现项目统一的 IFrameCodec 契约。
//
// 帧格式（对照 D:\System\Documents\逆向\results\MBB_PROTOCOL_FULL.md，已与 mbb_codec.py 自测向量逐字节对齐）：
//   [SOF=5A][LEN 2B 大端 = payloadLen+1][CTRL 0单/1首/2中/3末][SID][CID][TLV...][CRC16]
//   - LEN = (SID + CID + TLV) 字节数 + 1 = TLV 长度 + 3；不含末尾 2 字节 CRC
//   - CTRL：0=单帧；1/2/3=分片首/中/末（>~1017B 触发，FSN 写入偏移 4）
//   - CRC16/CCITT：poly=0x1021, init=0, 无反射, 无结果异或（与老协议 XMODEM 同算法），覆盖 [0 .. n-3]，大端追加
//
// 命令号与老 FreeBuds 协议共用（service 43 == 老 0x2B），故可直接复用 HuaweiConstants 现有常量，
// 业务层无需感知协议差异——只要把工厂创建的 codec 换成本类即可对 MBB 设备收发。
//
// 自测向量（来自逆向 mbb_codec.py，EQ 示例，真实 TLV 值需按命令语义填充）：
//   Encode(0x2B49, [01 01 05 02 02 10 20]) → 5a 00 0a 00 2b 49 01 01 05 02 02 10 20 b9 d4，CRC_OK。
internal sealed class MbbFrameCodec : IFrameCodec
{
    private readonly List<byte> _buffer = [];
    // 分片重组缓冲：键为 FSN，值为已累积的 body 字节。
    private readonly Dictionary<int, List<byte>> _fragments = [];

    public byte[] Encode(ushort command, ReadOnlySpan<byte> payload)
    {
        var service = (byte)(command >> 8);
        var cmd = (byte)(command & 0xFF);
        var tlvLength = payload.Length;
        var lengthField = tlvLength + 3; // SID(1) + CID(1) + TLV + 协议规定的 +1
        // 帧总长 = SOF(1) + LEN(2) + CTRL(1) + body(lengthField-1) + CRC(2) = lengthField + 5
        var frame = new byte[lengthField + 5];
        frame[0] = 0x5A;
        frame[1] = (byte)(lengthField >> 8);
        frame[2] = (byte)(lengthField & 0xFF);
        frame[3] = 0x00; // CTRL：单帧
        frame[4] = service;
        frame[5] = cmd;
        payload.CopyTo(frame.AsSpan(6));
        var crc = Crc16Ccitt(frame.AsSpan(0, frame.Length - 2));
        frame[^2] = (byte)(crc >> 8);
        frame[^1] = (byte)(crc & 0xFF);
        return frame;
    }

    public IEnumerable<ProtocolFrame> Decode(ReadOnlySpan<byte> bytes)
    {
        _buffer.AddRange(bytes.ToArray());
        var frames = new List<ProtocolFrame>();
        while (true)
        {
            // 扫描 SOF=0x5A。
            var sofIndex = -1;
            for (var i = 0; i + 1 < _buffer.Count; i++)
            {
                if (_buffer[i] == 0x5A)
                {
                    sofIndex = i;
                    break;
                }
            }
            if (sofIndex < 0)
            {
                _buffer.Clear();
                break;
            }
            if (sofIndex > 0)
                _buffer.RemoveRange(0, sofIndex);
            if (_buffer.Count < 5) // SOF + LEN(2) + CTRL 至少需要 5 字节才能定长
                break;
            var lengthField = (_buffer[1] << 8) | _buffer[2];
            var frameSize = lengthField + 5; // 见 Encode 中帧总长推导
            if (frameSize <= 5 || _buffer.Count < frameSize)
                break; // 帧未完整，等待更多数据
            if (!VerifyCrc(_buffer, frameSize))
            {
                _buffer.RemoveAt(0);
                continue;
            }
            var ctrl = _buffer[3];
            var command = (ushort)((_buffer[4] << 8) | _buffer[5]);
            var tlvLength = lengthField - 3; // body(SID+CID+TLV) - SID - CID
            if (ctrl == 0)
            {
                var payload = _buffer.GetRange(6, tlvLength).ToArray();
                frames.Add(new ProtocolFrame(command, payload));
                _buffer.RemoveRange(0, frameSize);
            }
            else
            {
                // 分片：body 从偏移 4 起（含 FSN），累积到对应 FSN 缓冲，末片(CTRL=3)重组后产出。
                // 注：真实分片布局见 MBB_PROTOCOL_FULL.md 第 1 节（bArr[4]=FSN），此处做尽力重组，
                // 单帧响应为绝对主流路径；多片大包（>~1017B）极少出现在本项目的控制命令中。
                var fsn = _buffer[4];
                _fragments.TryGetValue(fsn, out var acc);
                acc ??= [];
                acc.AddRange(_buffer.GetRange(4, lengthField - 1)); // body 不含 CRC，长度 = lengthField-1
                _fragments[fsn] = acc;
                _buffer.RemoveRange(0, frameSize);
                if (ctrl == 3)
                {
                    frames.Add(new ProtocolFrame(command, acc.ToArray()));
                    _fragments.Remove(fsn);
                }
            }
        }
        return frames;
    }

    private static bool VerifyCrc(List<byte> buffer, int frameSize)
    {
        var crc = Crc16Ccitt(buffer.GetRange(0, frameSize - 2).ToArray());
        return buffer[frameSize - 2] == (byte)(crc >> 8) && buffer[frameSize - 1] == (byte)(crc & 0xFF);
    }

    // CRC16/CCITT：poly=0x1021, init=0, 无反射, 无结果异或（与老协议 Crc16Xmodem 同算法）。
    internal static ushort Crc16Ccitt(ReadOnlySpan<byte> bytes)
    {
        ushort crc = 0;
        foreach (var value in bytes)
        {
            crc ^= (ushort)(value << 8);
            for (var i = 0; i < 8; i++)
                crc = (crc & 0x8000) != 0 ? (ushort)((crc << 1) ^ 0x1021) : (ushort)(crc << 1);
        }
        return crc;
    }
}
