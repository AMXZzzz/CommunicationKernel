// -----------------------------------------------------------------------------
// 文件: ModbusAsciiFraming.cs
// 层级: 插件层 / 协议
// 作用: Modbus ASCII 封装与解封（':' + 十六进制 + LRC + CRLF）。
// -----------------------------------------------------------------------------

using System.Globalization;
using System.Text;
using CommunicationKernel.Core.Abstractions.Errors;
using CommunicationKernel.Core.Abstractions.Results;

namespace CommunicationKernel.Plugins.Protocol.Modbus.Core;

/// <summary>
/// Modbus ASCII 封装：':' + 十六进制文本(从站号 + PDU + LRC) + CRLF。
/// </summary>
/// <remarks>
/// 与 RTU 一样与传输介质无关，同样支持经 TCP 转串口透传装置传输。
/// 以 CRLF 收尾使其具备确定性帧边界，无需时序猜测。
/// </remarks>
public static class ModbusAsciiFraming {

    /// <summary>起始符。</summary>
    public const byte StartDelimiter = (byte)':';

    /// <summary>最小帧长：':' + 从站号(2) + 功能码(2) + LRC(2) + CRLF(2)。</summary>
    public const int MinFrameLength = 9;

    // ============================================================================
    // 封装
    // ============================================================================

    /// <summary>
    /// 将 PDU 封装为完整 ASCII 帧。
    /// </summary>
    public static byte[] Wrap(byte unitId, byte[] pdu) {
        // LRC 覆盖从站号与 PDU 的二进制形式（尚未转十六进制文本）
        byte[] binary = new byte[1 + pdu.Length];
        binary[0] = unitId;
        Buffer.BlockCopy(pdu, 0, binary, 1, pdu.Length);
        byte lrc = ComputeLrc(binary, 0, binary.Length);

        // ':' + 每字节两字符十六进制 + LRC 两字符 + CRLF
        var sb = new StringBuilder(2 + binary.Length * 2 + 2 + 2);
        sb.Append(':');
        // 从站号与 PDU 逐字节转成两位大写十六进制
        foreach (byte b in binary)
            sb.Append(b.ToString("X2", CultureInfo.InvariantCulture));
        sb.Append(lrc.ToString("X2", CultureInfo.InvariantCulture));
        sb.Append("\r\n");

        // ASCII 编码后交给传输层原样发送
        return Encoding.ASCII.GetBytes(sb.ToString());
    }

    // ============================================================================
    // 帧长探测
    // ============================================================================

    /// <summary>
    /// 帧长探测：扫描 CRLF 结束序列。
    /// </summary>
    public static bool TryGetFrameLength(ReadOnlySpan<byte> received, out int totalLength) {
        totalLength = 0;

        // 最短帧尚未收齐，继续等
        if (received.Length < MinFrameLength)
            return false;

        // 首字节必须是起始符，否则该流已错位
        if (received[0] != StartDelimiter) {
            totalLength = -1;
            return true;
        }

        // 扫描 CRLF：ASCII 帧以 \r\n 为确定性边界
        for (int i = 1; i < received.Length; i++) {
            if (received[i - 1] == (byte)'\r' && received[i] == (byte)'\n') {
                totalLength = i + 1;
                return true;
            }
        }

        return false;   // 尚未收到 CRLF
    }

    // ============================================================================
    // 解封
    // ============================================================================

    /// <summary>
    /// 校验 LRC 并剥离外层封装，返回 PDU。
    /// </summary>
    public static OperationResult<byte[]> Unwrap(byte[]? frame, byte expectedUnitId) {
        // 最短：':' + 站号2 + 功能码2 + LRC2 + CRLF2
        if (frame is null || frame.Length < MinFrameLength)
            return OperationResult<byte[]>.Fail(
                $"Modbus ASCII 响应过短：至少需要 {MinFrameLength} 字节，实际 {frame?.Length ?? 0} 字节",
                KernelErrorCode.ProtocolError);

        // 缺少起始符说明收到的不是 ASCII 帧（可能是 RTU 或错位流）
        if (frame[0] != StartDelimiter)
            return OperationResult<byte[]>.Fail("Modbus ASCII 响应缺少起始符 ':'", KernelErrorCode.ProtocolError);

        // 定位 CRLF
        int end = -1;
        for (int i = 1; i < frame.Length; i++) {
            if (frame[i - 1] == (byte)'\r' && frame[i] == (byte)'\n') { end = i - 1; break; }
        }
        if (end < 0)
            return OperationResult<byte[]>.Fail("Modbus ASCII 响应缺少 CRLF 结束符", KernelErrorCode.ProtocolError);

        // ':' 与 CRLF 之间必须是偶数个十六进制字符（每字节两字符）
        int hexLength = end - 1;
        if (hexLength <= 0 || hexLength % 2 != 0)
            return OperationResult<byte[]>.Fail(
                $"Modbus ASCII 十六进制段长度非法：{hexLength}", KernelErrorCode.ProtocolError);

        // 把十六进制文本还原为二进制：[UnitId][PDU...][LRC]
        byte[] binary = new byte[hexLength / 2];
        for (int i = 0; i < binary.Length; i++) {
            if (!TryParseHexByte(frame[1 + i * 2], frame[2 + i * 2], out binary[i]))
                return OperationResult<byte[]>.Fail(
                    "Modbus ASCII 响应含非十六进制字符", KernelErrorCode.ProtocolError);
        }

        // binary = [UnitId][PDU...][LRC]
        if (binary.Length < 3)
            return OperationResult<byte[]>.Fail("Modbus ASCII 响应内容过短", KernelErrorCode.ProtocolError);

        // LRC 覆盖 UnitId+PDU，不含 LRC 自身
        byte actualLrc   = binary[^1];
        byte expectedLrc = ComputeLrc(binary, 0, binary.Length - 1);
        if (actualLrc != expectedLrc)
            return OperationResult<byte[]>.Fail(
                $"Modbus ASCII LRC 校验失败：期望 0x{expectedLrc:X2}，实际 0x{actualLrc:X2}",
                KernelErrorCode.ProtocolError);

        // 从站号必须与请求配对，避免 RS-485 迟到响应错位
        if (binary[0] != expectedUnitId)
            return OperationResult<byte[]>.Fail(
                $"Modbus ASCII 从站号不匹配：请求 {expectedUnitId}，响应 {binary[0]}",
                KernelErrorCode.ProtocolError);

        // 剥掉 UnitId 与 LRC，剩下纯 PDU 交给上层解析功能码
        byte[] pdu = new byte[binary.Length - 2];
        Buffer.BlockCopy(binary, 1, pdu, 0, pdu.Length);
        return OperationResult<byte[]>.Ok(pdu);
    }

    /// <summary>计算 LRC：所有字节求和取补码。</summary>
    public static byte ComputeLrc(byte[] buffer, int offset, int length) {
        byte sum = 0;
        for (int i = offset; i < offset + length; i++)
            sum += buffer[i];
        // 取补码：sum + LRC ≡ 0 (mod 256)
        return (byte)(-(sbyte)sum);
    }

    private static bool TryParseHexByte(byte high, byte low, out byte value) {
        value = 0;
        // 高半字节或低半字节任一非法则整字节失败
        if (!TryParseHexDigit(high, out int h) || !TryParseHexDigit(low, out int l))
            return false;
        value = (byte)((h << 4) | l);
        return true;
    }

    private static bool TryParseHexDigit(byte c, out int value) {
        if (c >= '0' && c <= '9') { value = c - '0';      return true; }
        if (c >= 'A' && c <= 'F') { value = c - 'A' + 10; return true; }
        if (c >= 'a' && c <= 'f') { value = c - 'a' + 10; return true; }
        value = 0;
        return false;
    }
}
