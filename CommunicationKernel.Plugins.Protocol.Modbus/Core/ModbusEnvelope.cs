// -----------------------------------------------------------------------------
// 文件: ModbusEnvelope.cs
// 层级: 插件层 / 协议
// 作用: 抽象 TCP/RTU/ASCII 三种外层封装（MBAP / CRC16 / LRC），供驱动注入。
// -----------------------------------------------------------------------------

using CommunicationKernel.Communication.Transport.Abstractions;
using CommunicationKernel.Core.Abstractions.Results;

namespace CommunicationKernel.Plugins.Protocol.Modbus.Core;

/// <summary>
/// 一次已封装的 Modbus 请求：待发送的完整帧 + 校验响应所需的关联信息。
/// </summary>
/// <param name="Frame">待发送的完整帧（含外层封装）。</param>
/// <param name="UnitId">请求的从站号，用于响应配对校验。</param>
/// <param name="TransactionId">
/// 事务 ID。仅 Modbus TCP 使用；RTU 与 ASCII 恒为 0 并忽略此字段。
/// </param>
public readonly record struct ModbusFramedRequest(
    byte[] Frame,
    byte UnitId,
    ushort TransactionId);

/// <summary>
/// Modbus 外层封装抽象。
/// </summary>
/// <remarks>
/// 这是 TCP / RTU / ASCII 三种 Modbus 变体<b>唯一</b>的真实差异：
/// <list type="bullet">
///   <item>TCP —— MBAP 头（事务 ID + 长度字段）</item>
///   <item>RTU —— 从站号 + PDU + CRC16</item>
///   <item>ASCII —— ':' + 十六进制文本 + LRC + CRLF</item>
/// </list>
/// 其余全部语义（地址模型、PDU、异常映射、上限校验）三者完全共用。
/// </remarks>
public abstract class ModbusEnvelope {

    /// <summary>本封装形式对应的帧完整性判定回调，交给传输层使用。</summary>
    public abstract TryGetFrameLength FrameProbe { get; }

    /// <summary>将 PDU 封装为可发送的完整帧。</summary>
    public abstract ModbusFramedRequest Wrap(byte unitId, byte[] pdu);

    /// <summary>校验外层封装并剥离，返回响应 PDU。</summary>
    public abstract OperationResult<byte[]> Unwrap(byte[] frame, ModbusFramedRequest request);
}

// ============================================================================
// Modbus TCP：MBAP
// ============================================================================

/// <summary>Modbus TCP 的 MBAP 封装。事务 ID 由本实例线程安全自增。</summary>
public sealed class ModbusTcpEnvelope : ModbusEnvelope {
    private int _transactionIdCounter;

    /// <inheritdoc />
    public override TryGetFrameLength FrameProbe => ModbusTcpFraming.TryGetFrameLength;

    /// <inheritdoc />
    public override ModbusFramedRequest Wrap(byte unitId, byte[] pdu) {
        // 事务 ID 自增后写入 MBAP，响应时必须按此配对，防止粘包/迟到帧错位
        ushort tid = NextTransactionId();
        return new ModbusFramedRequest(ModbusTcpFraming.Wrap(tid, unitId, pdu), unitId, tid);
    }

    /// <inheritdoc />
    public override OperationResult<byte[]> Unwrap(byte[] frame, ModbusFramedRequest request)
        => ModbusTcpFraming.Unwrap(frame, request.TransactionId, request.UnitId);

    /// <summary>取下一个事务 ID（0 保留，从 1 开始循环）。</summary>
    private ushort NextTransactionId() {
        // 截到 16 位；0 在部分网关会被当成“无事务”而跳过，因此跳过 0
        int next = Interlocked.Increment(ref _transactionIdCounter) & 0xFFFF;
        return (ushort)(next == 0 ? 1 : next);
    }
}

// ============================================================================
// Modbus RTU：CRC16
// ============================================================================

/// <summary>Modbus RTU 的 CRC16 封装。</summary>
public sealed class ModbusRtuEnvelope : ModbusEnvelope {
    /// <inheritdoc />
    public override TryGetFrameLength FrameProbe => ModbusRtuFraming.TryGetFrameLength;

    /// <inheritdoc />
    public override ModbusFramedRequest Wrap(byte unitId, byte[] pdu)
        => new(ModbusRtuFraming.Wrap(unitId, pdu), unitId, 0);

    /// <inheritdoc />
    public override OperationResult<byte[]> Unwrap(byte[] frame, ModbusFramedRequest request)
        => ModbusRtuFraming.Unwrap(frame, request.UnitId);
}

// ============================================================================
// Modbus ASCII：LRC
// ============================================================================

/// <summary>Modbus ASCII 的 LRC 文本封装。</summary>
public sealed class ModbusAsciiEnvelope : ModbusEnvelope {
    /// <inheritdoc />
    public override TryGetFrameLength FrameProbe => ModbusAsciiFraming.TryGetFrameLength;

    /// <inheritdoc />
    public override ModbusFramedRequest Wrap(byte unitId, byte[] pdu)
        => new(ModbusAsciiFraming.Wrap(unitId, pdu), unitId, 0);

    /// <inheritdoc />
    public override OperationResult<byte[]> Unwrap(byte[] frame, ModbusFramedRequest request)
        => ModbusAsciiFraming.Unwrap(frame, request.UnitId);
}
