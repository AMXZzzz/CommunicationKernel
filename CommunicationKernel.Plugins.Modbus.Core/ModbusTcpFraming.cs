using CommunicationKernel.Core.Abstractions.Errors;
using CommunicationKernel.Core.Abstractions.Results;

namespace CommunicationKernel.Plugins.Modbus.Core;

/// <summary>
/// Modbus TCP 封装：MBAP 头 + PDU。
/// </summary>
/// <remarks>
/// MBAP 头共 7 字节：
/// <code>
///   [0-1] Transaction ID  事务 ID，用于把响应与请求配对
///   [2-3] Protocol ID     固定 0x0000
///   [4-5] Length          其后字节数（含 Unit ID）
///   [6]   Unit ID         从站地址
/// </code>
/// 第 5-6 字节的 Length 字段使本协议具备确定性帧长，无需任何时序猜测。
/// </remarks>
public static class ModbusTcpFraming {

    /// <summary>MBAP 头长度。</summary>
    public const int HeaderLength = 7;

    /// <summary>Length 字段之前的固定部分长度（事务 ID + 协议 ID + 长度字段本身）。</summary>
    private const int LengthFieldEnd = 6;

    /// <summary>
    /// 将 PDU 封装为完整 MBAP 帧。
    /// </summary>
    public static byte[] Wrap(ushort transactionId, byte unitId, byte[] pdu) {
        byte[] frame = new byte[HeaderLength + pdu.Length];
        ModbusPdu.WriteUInt16(frame, 0, transactionId);
        ModbusPdu.WriteUInt16(frame, 2, 0);                                   // Protocol ID 固定 0
        ModbusPdu.WriteUInt16(frame, 4, (ushort)(pdu.Length + 1));            // Length = UnitId(1) + PDU
        frame[6] = unitId;
        Buffer.BlockCopy(pdu, 0, frame, HeaderLength, pdu.Length);
        return frame;
    }

    /// <summary>
    /// 帧长探测：读满 6 字节 MBAP 前缀后即可由 Length 字段确定总长。
    /// </summary>
    public static bool TryGetFrameLength(ReadOnlySpan<byte> received, out int totalLength) {
        totalLength = 0;

        // Length 字段位于 [4-5]，需先收到 6 字节
        if (received.Length < LengthFieldEnd)
            return false;

        int declared = (received[4] << 8) | received[5];

        // Length 至少要覆盖 Unit ID 与功能码
        if (declared < 2) {
            totalLength = -1;
            return true;
        }

        totalLength = LengthFieldEnd + declared;
        return true;
    }

    /// <summary>
    /// 校验 MBAP 头并剥离外层封装，返回 PDU。
    /// </summary>
    /// <param name="frame">完整 MBAP 响应帧。</param>
    /// <param name="expectedTransactionId">请求所用事务 ID。</param>
    /// <param name="expectedUnitId">请求所用从站号。</param>
    /// <remarks>
    /// 事务 ID 存在的唯一目的就是配对。历史实现认真地做了线程安全自增并写入请求，
    /// 却从不在解析时读取它——一旦发生粘连或迟到响应，
    /// 上一次请求的数据会被当作本次结果返回，且全程 Success = true。
    /// </remarks>
    public static OperationResult<byte[]> Unwrap(
        byte[]? frame, ushort expectedTransactionId, byte expectedUnitId) {

        if (frame is null || frame.Length < HeaderLength + 1)
            return OperationResult<byte[]>.Fail(
                $"Modbus TCP 响应过短：至少需要 {HeaderLength + 1} 字节，实际 {frame?.Length ?? 0} 字节",
                KernelErrorCode.ProtocolError);

        ushort transactionId = (ushort)((frame[0] << 8) | frame[1]);
        if (transactionId != expectedTransactionId)
            return OperationResult<byte[]>.Fail(
                $"事务 ID 不匹配：请求 {expectedTransactionId}，响应 {transactionId}（响应错位或迟到）",
                KernelErrorCode.ProtocolError);

        ushort protocolId = (ushort)((frame[2] << 8) | frame[3]);
        if (protocolId != 0)
            return OperationResult<byte[]>.Fail(
                $"协议 ID 应为 0，实际 {protocolId}", KernelErrorCode.ProtocolError);

        if (frame[6] != expectedUnitId)
            return OperationResult<byte[]>.Fail(
                $"从站号不匹配：请求 {expectedUnitId}，响应 {frame[6]}",
                KernelErrorCode.ProtocolError);

        int declared = (frame[4] << 8) | frame[5];
        int pduLength = declared - 1;                 // 扣除 Unit ID
        if (pduLength <= 0 || HeaderLength + pduLength > frame.Length)
            return OperationResult<byte[]>.Fail(
                $"MBAP 长度字段与实际帧长不符：声明 PDU {pduLength} 字节，实际可用 {frame.Length - HeaderLength} 字节",
                KernelErrorCode.ProtocolError);

        byte[] pdu = new byte[pduLength];
        Buffer.BlockCopy(frame, HeaderLength, pdu, 0, pduLength);
        return OperationResult<byte[]>.Ok(pdu);
    }
}
