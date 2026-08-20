// -----------------------------------------------------------------------------
// 文件: ModbusAsciiFrame.cs
// 层级: Plugins / Modbus.Ascii / Internal
// 作用: Modbus ASCII 帧构建与响应解析工具。
// 协议规范:
//   帧格式（ASCII 字节流）:
//     ':' + HEX(SlaveId,2) + HEX(FC,2) + HEX(Data,N*2) + HEX(LRC,2) + CR + LF
//   LRC 计算:
//     对 [SlaveId, FC, Data...] 所有字节求和，取低 8 位，再取二补数（0x100 - sum & 0xFF）
//   HEX 编码:
//     每字节编码为 2 个大写 ASCII 十六进制字符
//   功能码:
//     FC01 读线圈 / FC03 读保持寄存器
//     FC05 写单线圈 / FC06 写单寄存器 / FC10 写多寄存器
// 说明:
//   协议帧细节只在本文件内可见，外层禁止感知。
// -----------------------------------------------------------------------------

using System;
using System.Text;
using CommunicationKernel.Core.Abstractions.Errors;
using CommunicationKernel.Core.Abstractions.Results;

namespace CommunicationKernel.Plugins.Modbus.Ascii.Internal;

/// <summary>
/// Modbus ASCII 帧构建与响应解析工具（内部使用）。
/// </summary>
internal static class ModbusAsciiFrame
{
    // -------------------------------------------------------------------------
    // 功能码常量
    // -------------------------------------------------------------------------
    private const byte FcReadCoils              = 0x01;
    private const byte FcReadHoldingRegisters   = 0x03;
    private const byte FcWriteSingleCoil        = 0x05;
    private const byte FcWriteSingleRegister    = 0x06;
    private const byte FcWriteMultipleRegisters = 0x10;
    private const byte ExceptionMask            = 0x80;

    // ASCII 帧定界符
    private static readonly byte[] CrLf = new byte[] { 0x0D, 0x0A };

    // -------------------------------------------------------------------------
    // 帧构建：读
    // -------------------------------------------------------------------------

    /// <summary>
    /// 构建读线圈（FC01）或读保持寄存器（FC03）ASCII 帧。
    /// </summary>
    internal static byte[] BuildReadFrame(
        byte slaveId, bool isCoil, ushort startAddress, ushort quantity)
    {
        byte fc = isCoil ? FcReadCoils : FcReadHoldingRegisters;
        byte[] pdu = new byte[] {
            slaveId, fc,
            (byte)(startAddress >> 8), (byte)(startAddress & 0xFF),
            (byte)(quantity >> 8),     (byte)(quantity & 0xFF)
        };
        return BuildAsciiFrame(pdu);
    }

    /// <summary>
    /// 构建写单线圈（FC05）ASCII 帧。ON=0xFF00，OFF=0x0000。
    /// </summary>
    internal static byte[] BuildWriteSingleCoil(byte slaveId, ushort address, bool value)
    {
        byte[] pdu = new byte[] {
            slaveId, FcWriteSingleCoil,
            (byte)(address >> 8), (byte)(address & 0xFF),
            value ? (byte)0xFF : (byte)0x00, 0x00
        };
        return BuildAsciiFrame(pdu);
    }

    /// <summary>
    /// 构建写单寄存器（FC06）ASCII 帧。
    /// </summary>
    internal static byte[] BuildWriteSingleRegister(byte slaveId, ushort address, ushort value)
    {
        byte[] pdu = new byte[] {
            slaveId, FcWriteSingleRegister,
            (byte)(address >> 8), (byte)(address & 0xFF),
            (byte)(value >> 8),   (byte)(value & 0xFF)
        };
        return BuildAsciiFrame(pdu);
    }

    /// <summary>
    /// 构建写多寄存器（FC10）ASCII 帧。
    /// </summary>
    internal static byte[] BuildWriteMultipleRegisters(byte slaveId, ushort address, byte[] payload)
    {
        byte[] data = payload.Length % 2 == 0 ? payload : PadToEven(payload);
        int regCount = data.Length / 2;
        // PDU: SlaveId(1)+FC(1)+Addr(2)+RegCount(2)+ByteCount(1)+Data
        byte[] pdu = new byte[7 + data.Length];
        pdu[0] = slaveId;
        pdu[1] = FcWriteMultipleRegisters;
        pdu[2] = (byte)(address >> 8);
        pdu[3] = (byte)(address & 0xFF);
        pdu[4] = (byte)(regCount >> 8);
        pdu[5] = (byte)(regCount & 0xFF);
        pdu[6] = (byte)data.Length;
        Buffer.BlockCopy(data, 0, pdu, 7, data.Length);
        return BuildAsciiFrame(pdu);
    }

    // -------------------------------------------------------------------------
    // 响应解析
    // -------------------------------------------------------------------------

    /// <summary>
    /// 解析 FC03 读保持寄存器 ASCII 响应，返回原始字节（大端寄存器顺序）。
    /// </summary>
    internal static OperationResult<byte[]> ParseReadRegistersResponse(byte[] response)
    {
        OperationResult<byte[]> decoded = DecodeFrame(response);
        if (!decoded.Success)
            return OperationResult<byte[]>.Fail(decoded.ErrorMessage, decoded.ErrorCode);

        byte[] pdu = decoded.Value; // [SlaveId, FC, ByteCount, Data...]
        if (pdu.Length < 3)
            return OperationResult<byte[]>.Fail("FC03 ASCII response PDU too short", KernelErrorCode.ProtocolError);

        OperationResult exCheck = CheckException(pdu);
        if (!exCheck.Success)
            return OperationResult<byte[]>.Fail(exCheck.ErrorMessage, exCheck.ErrorCode);

        int byteCount = pdu[2];
        if (pdu.Length < 3 + byteCount)
            return OperationResult<byte[]>.Fail("FC03 ASCII response data truncated", KernelErrorCode.ProtocolError);

        byte[] data = new byte[byteCount];
        Buffer.BlockCopy(pdu, 3, data, 0, byteCount);
        return OperationResult<byte[]>.Ok(data);
    }

    /// <summary>
    /// 解析 FC01 读线圈 ASCII 响应。
    /// </summary>
    internal static OperationResult<byte[]> ParseReadCoilsResponse(byte[] response)
    {
        OperationResult<byte[]> decoded = DecodeFrame(response);
        if (!decoded.Success)
            return OperationResult<byte[]>.Fail(decoded.ErrorMessage, decoded.ErrorCode);

        byte[] pdu = decoded.Value;
        if (pdu.Length < 3)
            return OperationResult<byte[]>.Fail("FC01 ASCII response PDU too short", KernelErrorCode.ProtocolError);

        OperationResult exCheck = CheckException(pdu);
        if (!exCheck.Success)
            return OperationResult<byte[]>.Fail(exCheck.ErrorMessage, exCheck.ErrorCode);

        int byteCount = pdu[2];
        if (pdu.Length < 3 + byteCount)
            return OperationResult<byte[]>.Fail("FC01 ASCII response data truncated", KernelErrorCode.ProtocolError);

        byte[] data = new byte[byteCount];
        Buffer.BlockCopy(pdu, 3, data, 0, byteCount);
        return OperationResult<byte[]>.Ok(data);
    }

    /// <summary>
    /// 解析写响应 ASCII 帧，仅校验异常标志与 LRC。
    /// </summary>
    internal static OperationResult ParseWriteResponse(byte[] response)
    {
        OperationResult<byte[]> decoded = DecodeFrame(response);
        if (!decoded.Success)
            return OperationResult.Fail(decoded.ErrorMessage, decoded.ErrorCode);

        return CheckException(decoded.Value);
    }

    // -------------------------------------------------------------------------
    // LRC 计算
    // -------------------------------------------------------------------------

    /// <summary>
    /// 计算 Modbus LRC：对所有字节求和，取低 8 位，再取二补数。
    /// </summary>
    internal static byte ComputeLrc(byte[] data, int offset, int length)
    {
        int sum = 0;
        for (int i = offset; i < offset + length; i++)
            sum += data[i];
        return (byte)(((~sum) + 1) & 0xFF);
    }

    // -------------------------------------------------------------------------
    // 内部辅助
    // -------------------------------------------------------------------------

    /// <summary>
    /// 将二进制 PDU 编码为完整 Modbus ASCII 帧（':'+ HEX + LRC-HEX + CR + LF）。
    /// <paramref name="pdu"/> 包含 [SlaveId, FC, Data...]，不含 LRC。
    /// </summary>
    private static byte[] BuildAsciiFrame(byte[] pdu)
    {
        byte lrc = ComputeLrc(pdu, 0, pdu.Length);
        // 每字节 2 hex 字符，加上 ':'(1) + LRC hex(2) + CR(1) + LF(1)
        char[] hex = new char[pdu.Length * 2 + 2];
        for (int i = 0; i < pdu.Length; i++)
        {
            hex[i * 2]     = ToHexChar(pdu[i] >> 4);
            hex[i * 2 + 1] = ToHexChar(pdu[i] & 0x0F);
        }
        hex[pdu.Length * 2]     = ToHexChar(lrc >> 4);
        hex[pdu.Length * 2 + 1] = ToHexChar(lrc & 0x0F);

        // ':'(1) + hex + CR + LF
        byte[] frame = new byte[1 + hex.Length * 1 + 2];
        frame[0] = (byte)':';
        for (int i = 0; i < hex.Length; i++)
            frame[1 + i] = (byte)hex[i];
        frame[frame.Length - 2] = 0x0D; // CR
        frame[frame.Length - 1] = 0x0A; // LF
        return frame;
    }

    /// <summary>
    /// 解码 ASCII 帧为 PDU 字节数组，同时验证 ':' 前缀、CR/LF 结尾、LRC。
    /// </summary>
    private static OperationResult<byte[]> DecodeFrame(byte[] frame)
    {
        if (frame is null || frame.Length < 9)
            return OperationResult<byte[]>.Fail("ASCII frame too short", KernelErrorCode.ProtocolError);

        if (frame[0] != (byte)':')
            return OperationResult<byte[]>.Fail("ASCII frame missing ':' start delimiter", KernelErrorCode.ProtocolError);

        if (frame[frame.Length - 2] != 0x0D || frame[frame.Length - 1] != 0x0A)
            return OperationResult<byte[]>.Fail("ASCII frame missing CR LF terminator", KernelErrorCode.ProtocolError);

        // ASCII HEX 内容区域: frame[1 .. frame.Length-3]（不含':'和CRLF）
        int hexLen = frame.Length - 3; // 去掉 ':' 和 CRLF
        if (hexLen % 2 != 0)
            return OperationResult<byte[]>.Fail("ASCII frame hex length not even", KernelErrorCode.ProtocolError);

        int byteCount = hexLen / 2; // 含 LRC
        byte[] bytes = new byte[byteCount];
        for (int i = 0; i < byteCount; i++)
        {
            int hi = FromHexChar((char)frame[1 + i * 2]);
            int lo = FromHexChar((char)frame[1 + i * 2 + 1]);
            if (hi < 0 || lo < 0)
                return OperationResult<byte[]>.Fail("ASCII frame contains invalid hex character", KernelErrorCode.ProtocolError);
            bytes[i] = (byte)((hi << 4) | lo);
        }

        // 验证 LRC：PDU 字节（不含末尾 LRC 字节）的 LRC 应等于 bytes[byteCount-1]
        byte expectedLrc = ComputeLrc(bytes, 0, byteCount - 1);
        if (expectedLrc != bytes[byteCount - 1])
            return OperationResult<byte[]>.Fail(
                $"LRC mismatch: expected 0x{expectedLrc:X2} got 0x{bytes[byteCount - 1]:X2}",
                KernelErrorCode.ProtocolError);

        // 返回 PDU（去掉末尾 LRC 字节）
        byte[] pdu = new byte[byteCount - 1];
        Buffer.BlockCopy(bytes, 0, pdu, 0, pdu.Length);
        return OperationResult<byte[]>.Ok(pdu);
    }

    private static OperationResult CheckException(byte[] pdu)
    {
        // pdu[0]=SlaveId, pdu[1]=FC
        if (pdu.Length < 2)
            return OperationResult.Fail("ASCII PDU too short to read FC", KernelErrorCode.ProtocolError);

        byte fc = pdu[1];
        if ((fc & ExceptionMask) != 0)
        {
            byte exCode = pdu.Length > 2 ? pdu[2] : (byte)0;
            return OperationResult.Fail(
                $"Modbus ASCII exception 0x{exCode:X2}: {MapExceptionCode(exCode)}",
                KernelErrorCode.ProtocolError);
        }

        return OperationResult.Ok;
    }

    private static string MapExceptionCode(byte code) => code switch
    {
        0x01 => "Illegal Function",
        0x02 => "Illegal Data Address",
        0x03 => "Illegal Data Value",
        0x04 => "Slave Device Failure",
        0x05 => "Acknowledge",
        0x06 => "Slave Device Busy",
        0x08 => "Memory Parity Error",
        0x0A => "Gateway Path Unavailable",
        0x0B => "Gateway Target Device Failed to Respond",
        _    => $"Unknown exception 0x{code:X2}"
    };

    private static char ToHexChar(int nibble) => (char)(nibble < 10 ? '0' + nibble : 'A' + nibble - 10);

    private static int FromHexChar(char c)
    {
        if (c >= '0' && c <= '9') return c - '0';
        if (c >= 'A' && c <= 'F') return c - 'A' + 10;
        if (c >= 'a' && c <= 'f') return c - 'a' + 10;
        return -1;
    }

    private static byte[] PadToEven(byte[] src)
    {
        byte[] padded = new byte[src.Length + 1];
        Buffer.BlockCopy(src, 0, padded, 0, src.Length);
        return padded;
    }
}
