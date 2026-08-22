using CommunicationKernel.Core.Abstractions.Errors;
using CommunicationKernel.Core.Abstractions.Results;

namespace CommunicationKernel.Plugins.Protocol.Modbus.Core;

/// <summary>
/// Modbus RTU 封装：从站号 + PDU + CRC16(小端)。
/// </summary>
/// <remarks>
/// <para>
/// 本封装与传输介质无关。除 RS-485 外，它同样用于「TCP 转串口透传装置」
/// 场景——网关把 TCP 载荷原样转发到串口，因此以太网侧跑的仍是 RTU 帧。
/// </para>
/// <para>
/// <b>分帧必须按长度推定，不能靠静默。</b>
/// 串口上 RTU 依赖 3.5 字符帧间静默分帧，但经透传装置转发后该静默
/// 在 TCP 侧不被保证保留。所幸 RTU 帧长可由前几个字节确定：
/// 读响应为 <c>3 + 字节数 + 2</c>，写响应固定 8 字节，异常响应固定 5 字节。
/// </para>
/// </remarks>
public static class ModbusRtuFraming {

    /// <summary>RTU 帧最小长度：从站号 + 功能码 + CRC16。</summary>
    public const int MinFrameLength = 4;

    /// <summary>异常响应固定长度：从站号 + (FC|0x80) + 异常码 + CRC16。</summary>
    public const int ExceptionFrameLength = 5;

    /// <summary>写响应固定长度：从站号 + FC + 地址(2) + 值/数量(2) + CRC16。</summary>
    public const int WriteResponseLength = 8;

    /// <summary>
    /// 将 PDU 封装为完整 RTU 帧。
    /// </summary>
    public static byte[] Wrap(byte unitId, byte[] pdu) {
        byte[] frame = new byte[1 + pdu.Length + 2];
        frame[0] = unitId;
        Buffer.BlockCopy(pdu, 0, frame, 1, pdu.Length);

        ushort crc = ComputeCrc16(frame, 0, 1 + pdu.Length);
        // CRC16 在 RTU 中按小端附加（低字节在前）
        frame[^2] = (byte)(crc & 0xFF);
        frame[^1] = (byte)(crc >> 8);
        return frame;
    }

    /// <summary>
    /// 帧长探测：供传输层判定响应是否已完整接收。
    /// </summary>
    /// <remarks>
    /// 判定顺序至关重要：<b>先看异常位再看正常帧</b>。
    /// 异常帧只有 5 字节，若先按正常读响应的最小长度等待，
    /// 异常响应会一直等不满而超时，用户看到的是"读取超时"
    /// 而不是真正的"非法数据地址"。
    /// </remarks>
    public static bool TryGetFrameLength(ReadOnlySpan<byte> received, out int totalLength) {
        totalLength = 0;

        // 至少要看到从站号与功能码才能判断
        if (received.Length < 2)
            return false;

        byte fc = received[1];

        // ── 异常响应：固定 5 字节 ──
        if ((fc & ModbusFunctionCode.ExceptionMask) != 0) {
            totalLength = ExceptionFrameLength;
            return true;
        }

        switch (fc) {
            // ── 读类响应：[Unit][FC][ByteCount][Data...][CRC16] ──
            case ModbusFunctionCode.ReadCoils:
            case ModbusFunctionCode.ReadDiscreteInputs:
            case ModbusFunctionCode.ReadHoldingRegisters:
            case ModbusFunctionCode.ReadInputRegisters:
                if (received.Length < 3)
                    return false;   // 还没看到字节数字段
                totalLength = 3 + received[2] + 2;
                return true;

            // ── 写类响应：固定 8 字节 ──
            case ModbusFunctionCode.WriteSingleCoil:
            case ModbusFunctionCode.WriteSingleRegister:
            case ModbusFunctionCode.WriteMultipleCoils:
            case ModbusFunctionCode.WriteMultipleRegisters:
                totalLength = WriteResponseLength;
                return true;

            default:
                // 未知功能码：无法推定长度，交由上层按协议错误处理
                totalLength = -1;
                return true;
        }
    }

    /// <summary>
    /// 校验 CRC 并剥离外层封装，返回 PDU。
    /// </summary>
    /// <param name="frame">完整 RTU 响应帧。</param>
    /// <param name="expectedUnitId">请求所用的从站号，用于配对校验。</param>
    public static OperationResult<byte[]> Unwrap(byte[]? frame, byte expectedUnitId) {
        if (frame is null || frame.Length < MinFrameLength)
            return OperationResult<byte[]>.Fail(
                $"RTU 响应过短：至少需要 {MinFrameLength} 字节，实际 {frame?.Length ?? 0} 字节",
                KernelErrorCode.ProtocolError);

        // ── CRC 校验 ──
        ushort actual   = (ushort)(frame[^2] | (frame[^1] << 8));
        ushort expected = ComputeCrc16(frame, 0, frame.Length - 2);
        if (actual != expected)
            return OperationResult<byte[]>.Fail(
                $"RTU CRC 校验失败：期望 0x{expected:X4}，实际 0x{actual:X4}",
                KernelErrorCode.ProtocolError);

        // ── 从站号配对校验 ──
        // RS-485 一主多从场景下，上一次超时请求的迟到响应会污染本次读取；
        // 不比对从站号就会把从站 A 的数据当作从站 B 的值显示。
        if (frame[0] != expectedUnitId)
            return OperationResult<byte[]>.Fail(
                $"RTU 从站号不匹配：请求 {expectedUnitId}，响应 {frame[0]}",
                KernelErrorCode.ProtocolError);

        byte[] pdu = new byte[frame.Length - 3];
        Buffer.BlockCopy(frame, 1, pdu, 0, pdu.Length);
        return OperationResult<byte[]>.Ok(pdu);
    }

    /// <summary>
    /// 计算 Modbus CRC16（多项式 0xA001，初值 0xFFFF）。
    /// </summary>
    public static ushort ComputeCrc16(byte[] buffer, int offset, int length) {
        ushort crc = 0xFFFF;
        for (int i = offset; i < offset + length; i++) {
            crc ^= buffer[i];
            for (int bit = 0; bit < 8; bit++) {
                bool lsb = (crc & 0x0001) != 0;
                crc >>= 1;
                if (lsb) crc ^= 0xA001;
            }
        }
        return crc;
    }
}
