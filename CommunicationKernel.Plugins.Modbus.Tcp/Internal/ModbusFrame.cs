// -----------------------------------------------------------------------------
// 文件: ModbusFrame.cs
// 层级: Plugins / Modbus.Tcp / Internal
// 作用: Modbus TCP MBAP 帧构建与响应解析工具。
// 协议规范:
//   MBAP Header（7 字节）:
//     [0-1] Transaction ID   — 每请求递增，用于匹配响应
//     [2-3] Protocol ID      — 固定 0x0000
//     [4-5] Length           — 后续字节数（含 Unit ID）
//     [6]   Unit ID          — 从站地址（Slave Address）
//   PDU（功能码 + 数据）:
//     [7]   Function Code
//     [8+]  Data
// 说明:
//   协议帧细节只在本文件内可见，外层禁止感知。
// -----------------------------------------------------------------------------

using System;
using CommunicationKernel.Core.Abstractions.Errors;
using CommunicationKernel.Core.Abstractions.Results;

namespace CommunicationKernel.Plugins.Modbus.Tcp.Internal;

/// <summary>
/// Modbus 功能码常量。
/// </summary>
internal static class ModbusFc
{
    internal const byte ReadCoils             = 0x01;
    internal const byte ReadHoldingRegisters  = 0x03;
    internal const byte WriteSingleCoil       = 0x05;
    internal const byte WriteSingleRegister   = 0x06;
    internal const byte WriteMultipleRegisters = 0x10;
    internal const byte ExceptionMask         = 0x80;
}

/// <summary>
/// Modbus TCP 帧构建与响应解析工具（内部使用，禁止外层引用）。
/// </summary>
internal static class ModbusFrame
{
    // -------------------------------------------------------------------------
    // 帧构建：读
    // -------------------------------------------------------------------------

    /// <summary>
    /// 构建读线圈（FC01）或读保持寄存器（FC03）MBAP 帧。
    /// </summary>
    /// <param name="transactionId">事务 ID。</param>
    /// <param name="unitId">从站 ID。</param>
    /// <param name="isCoil">true=FC01，false=FC03。</param>
    /// <param name="startAddress">起始地址（0 基）。</param>
    /// <param name="quantity">读取数量（线圈数 / 寄存器数）。</param>
    internal static byte[] BuildReadFrame(
        ushort transactionId,
        byte   unitId,
        bool   isCoil,
        ushort startAddress,
        ushort quantity)
    {
        byte fc = isCoil ? ModbusFc.ReadCoils : ModbusFc.ReadHoldingRegisters;

        // PDU = FC(1) + StartAddr(2) + Quantity(2) = 5 字节
        byte[] frame = new byte[12];
        frame[0] = (byte)(transactionId >> 8);
        frame[1] = (byte)(transactionId & 0xFF);
        frame[2] = 0x00; // Protocol ID Hi
        frame[3] = 0x00; // Protocol ID Lo
        frame[4] = 0x00; // Length Hi
        frame[5] = 0x06; // Length Lo：UnitId(1) + PDU(5) = 6
        frame[6] = unitId;
        frame[7] = fc;
        frame[8] = (byte)(startAddress >> 8);
        frame[9] = (byte)(startAddress & 0xFF);
        frame[10] = (byte)(quantity >> 8);
        frame[11] = (byte)(quantity & 0xFF);
        return frame;
    }

    // -------------------------------------------------------------------------
    // 帧构建：写
    // -------------------------------------------------------------------------

    /// <summary>
    /// 构建写单线圈（FC05）MBAP 帧。
    /// ON=0xFF00，OFF=0x0000。
    /// </summary>
    internal static byte[] BuildWriteSingleCoil(
        ushort transactionId, byte unitId, ushort address, bool value)
    {
        byte[] frame = new byte[12];
        Write16(frame, 0, transactionId);
        frame[2] = 0x00; frame[3] = 0x00;
        Write16(frame, 4, 6);
        frame[6] = unitId;
        frame[7] = ModbusFc.WriteSingleCoil;
        Write16(frame, 8, address);
        frame[10] = value ? (byte)0xFF : (byte)0x00;
        frame[11] = 0x00;
        return frame;
    }

    /// <summary>
    /// 构建写单寄存器（FC06）MBAP 帧。
    /// </summary>
    internal static byte[] BuildWriteSingleRegister(
        ushort transactionId, byte unitId, ushort address, ushort value)
    {
        byte[] frame = new byte[12];
        Write16(frame, 0, transactionId);
        frame[2] = 0x00; frame[3] = 0x00;
        Write16(frame, 4, 6);
        frame[6] = unitId;
        frame[7] = ModbusFc.WriteSingleRegister;
        Write16(frame, 8, address);
        Write16(frame, 10, value);
        return frame;
    }

    /// <summary>
    /// 构建写多寄存器（FC10）MBAP 帧。
    /// <paramref name="payload"/> 长度必须为偶数字节（已按大端寄存器顺序排列）。
    /// </summary>
    internal static byte[] BuildWriteMultipleRegisters(
        ushort transactionId, byte unitId, ushort address, byte[] payload)
    {
        // payload 为寄存器字节序列，必须偶数字节
        byte[] data = payload.Length % 2 == 0 ? payload : PadToEven(payload);
        int regCount   = data.Length / 2;
        int pduLen     = 6 + data.Length;          // FC(1)+Addr(2)+Count(2)+ByteCount(1)+Data
        int frameLen   = 7 + pduLen;               // MBAP(7) + PDU

        byte[] frame = new byte[frameLen];
        Write16(frame, 0, transactionId);
        frame[2] = 0x00; frame[3] = 0x00;
        Write16(frame, 4, (ushort)(pduLen + 1));   // Length = UnitId(1) + PDU
        frame[6] = unitId;
        frame[7] = ModbusFc.WriteMultipleRegisters;
        Write16(frame, 8,  address);
        Write16(frame, 10, (ushort)regCount);
        frame[12] = (byte)data.Length;
        Buffer.BlockCopy(data, 0, frame, 13, data.Length);
        return frame;
    }

    // -------------------------------------------------------------------------
    // 响应解析
    // -------------------------------------------------------------------------

    /// <summary>
    /// 解析 FC03 读保持寄存器响应，返回原始字节（大端，每两字节一个寄存器）。
    /// </summary>
    internal static OperationResult<byte[]> ParseReadRegistersResponse(byte[] response)
    {
        // 分支1：异常响应
        OperationResult exCheck = CheckException(response);
        if (!exCheck.Success)
            return OperationResult<byte[]>.Fail(exCheck.ErrorMessage, exCheck.ErrorCode);

        // 分支2：最小长度（MBAP 7 + FC 1 + ByteCount 1 + 至少 2 字节数据 = 11）
        if (response.Length < 11)
            return OperationResult<byte[]>.Fail(
                "FC03 response too short", KernelErrorCode.ProtocolError);

        int byteCount = response[8];
        if (response.Length < 9 + byteCount)
            return OperationResult<byte[]>.Fail(
                "FC03 response data truncated", KernelErrorCode.ProtocolError);

        var data = new byte[byteCount];
        Buffer.BlockCopy(response, 9, data, 0, byteCount);
        return OperationResult<byte[]>.Ok(data);
    }

    /// <summary>
    /// 解析 FC01 读线圈响应，返回字节数组（每字节 8 个线圈，LSB first）。
    /// </summary>
    internal static OperationResult<byte[]> ParseReadCoilsResponse(byte[] response)
    {
        OperationResult exCheck = CheckException(response);
        if (!exCheck.Success)
            return OperationResult<byte[]>.Fail(exCheck.ErrorMessage, exCheck.ErrorCode);

        if (response.Length < 10)
            return OperationResult<byte[]>.Fail(
                "FC01 response too short", KernelErrorCode.ProtocolError);

        int byteCount = response[8];
        if (response.Length < 9 + byteCount)
            return OperationResult<byte[]>.Fail(
                "FC01 response data truncated", KernelErrorCode.ProtocolError);

        var data = new byte[byteCount];
        Buffer.BlockCopy(response, 9, data, 0, byteCount);
        return OperationResult<byte[]>.Ok(data);
    }

    /// <summary>
    /// 解析写响应（FC05/FC06/FC10），仅校验异常标志。
    /// </summary>
    internal static OperationResult ParseWriteResponse(byte[] response)
        => CheckException(response);

    // -------------------------------------------------------------------------
    // 内部辅助
    // -------------------------------------------------------------------------

    /// <summary>
    /// 检测 Modbus 异常响应（功能码最高位为 1 表示异常）。
    /// </summary>
    private static OperationResult CheckException(byte[] response)
    {
        if (response is null || response.Length < 9)
            return OperationResult.Fail("Modbus response is null or too short", KernelErrorCode.ProtocolError);

        byte fc = response[7];
        if ((fc & ModbusFc.ExceptionMask) != 0)
        {
            byte exCode = response.Length > 8 ? response[8] : (byte)0;
            string msg = MapExceptionCode(exCode);
            return OperationResult.Fail($"Modbus exception 0x{exCode:X2}: {msg}", KernelErrorCode.ProtocolError);
        }

        return OperationResult.Ok;
    }

    /// <summary>
    /// 将 Modbus 异常码映射为可读描述。
    /// </summary>
    private static string MapExceptionCode(byte code) => code switch
    {
        0x01 => "Illegal Function",
        0x02 => "Illegal Data Address",
        0x03 => "Illegal Data Value",
        0x04 => "Slave Device Failure",
        0x05 => "Acknowledge (processing may take long)",
        0x06 => "Slave Device Busy",
        0x08 => "Memory Parity Error",
        0x0A => "Gateway Path Unavailable",
        0x0B => "Gateway Target Device Failed to Respond",
        _    => $"Unknown exception code 0x{code:X2}"
    };

    private static void Write16(byte[] buf, int offset, ushort value)
    {
        buf[offset]     = (byte)(value >> 8);
        buf[offset + 1] = (byte)(value & 0xFF);
    }

    private static byte[] PadToEven(byte[] src)
    {
        byte[] padded = new byte[src.Length + 1];
        Buffer.BlockCopy(src, 0, padded, 0, src.Length);
        return padded;
    }
}
