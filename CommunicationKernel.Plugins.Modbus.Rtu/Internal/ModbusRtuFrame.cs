// -----------------------------------------------------------------------------
// 文件: ModbusRtuFrame.cs
// 层级: Plugins / Modbus.Rtu / Internal
// 作用: Modbus RTU 帧构建与响应解析工具。
// 协议规范:
//   帧格式: [SlaveId(1)] [FC(1)] [Data(N)] [CRC16-Lo(1)] [CRC16-Hi(1)]
//   CRC16: 多项式 0xA001（LSB first，Modbus 标准多项式）
//   帧边界: 依赖 3.5 字符静默时间（由底层串口/传输层保证）
//
//   功能码:
//     FC01 读线圈 / FC03 读保持寄存器
//     FC05 写单线圈 / FC06 写单寄存器 / FC10 写多寄存器
// 说明:
//   协议帧细节只在本文件内可见，外层禁止感知。
// -----------------------------------------------------------------------------

using System;
using CommunicationKernel.Core.Abstractions.Errors;
using CommunicationKernel.Core.Abstractions.Results;

namespace CommunicationKernel.Plugins.Modbus.Rtu.Internal;

/// <summary>
/// Modbus RTU 帧构建与响应解析工具（内部使用，禁止外层引用）。
/// </summary>
internal static class ModbusRtuFrame
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

    // -------------------------------------------------------------------------
    // 帧构建：读
    // -------------------------------------------------------------------------

    /// <summary>
    /// 构建读线圈（FC01）或读保持寄存器（FC03）RTU 帧。
    /// </summary>
    internal static byte[] BuildReadFrame(
        byte slaveId, bool isCoil, ushort startAddress, ushort quantity)
    {
        byte fc = isCoil ? FcReadCoils : FcReadHoldingRegisters;
        // PDU: FC(1) + StartAddr(2) + Quantity(2)
        byte[] pdu = new byte[5];
        pdu[0] = fc;
        Write16(pdu, 1, startAddress);
        Write16(pdu, 3, quantity);
        return BuildFrame(slaveId, pdu);
    }

    /// <summary>
    /// 构建写单线圈（FC05）RTU 帧。ON=0xFF00，OFF=0x0000。
    /// </summary>
    internal static byte[] BuildWriteSingleCoil(byte slaveId, ushort address, bool value)
    {
        byte[] pdu = new byte[5];
        pdu[0] = FcWriteSingleCoil;
        Write16(pdu, 1, address);
        pdu[3] = value ? (byte)0xFF : (byte)0x00;
        pdu[4] = 0x00;
        return BuildFrame(slaveId, pdu);
    }

    /// <summary>
    /// 构建写单寄存器（FC06）RTU 帧。
    /// </summary>
    internal static byte[] BuildWriteSingleRegister(byte slaveId, ushort address, ushort value)
    {
        byte[] pdu = new byte[5];
        pdu[0] = FcWriteSingleRegister;
        Write16(pdu, 1, address);
        Write16(pdu, 3, value);
        return BuildFrame(slaveId, pdu);
    }

    /// <summary>
    /// 构建写多寄存器（FC10/0x10）RTU 帧。
    /// <paramref name="payload"/> 必须为偶数字节（按大端寄存器顺序）。
    /// </summary>
    internal static byte[] BuildWriteMultipleRegisters(byte slaveId, ushort address, byte[] payload)
    {
        byte[] data = payload.Length % 2 == 0 ? payload : PadToEven(payload);
        int regCount = data.Length / 2;
        // PDU: FC(1)+Addr(2)+Count(2)+ByteCount(1)+Data
        byte[] pdu = new byte[6 + data.Length];
        pdu[0] = FcWriteMultipleRegisters;
        Write16(pdu, 1, address);
        Write16(pdu, 3, (ushort)regCount);
        pdu[5] = (byte)data.Length;
        Buffer.BlockCopy(data, 0, pdu, 6, data.Length);
        return BuildFrame(slaveId, pdu);
    }

    // -------------------------------------------------------------------------
    // 响应解析
    // -------------------------------------------------------------------------

    /// <summary>
    /// 解析 FC03 读保持寄存器响应。
    /// </summary>
    internal static OperationResult<byte[]> ParseReadRegistersResponse(byte[] response)
    {
        // 最小长度: SlaveId(1)+FC(1)+ByteCount(1)+Data(≥2)+CRC(2) = 7
        OperationResult exCheck = CheckException(response, minLength: 7);
        if (!exCheck.Success)
            return OperationResult<byte[]>.Fail(exCheck.ErrorMessage, exCheck.ErrorCode);

        int byteCount = response[2];
        if (response.Length < 5 + byteCount)
            return OperationResult<byte[]>.Fail("FC03 response data truncated", KernelErrorCode.ProtocolError);

        // 验证 CRC（除最后 2 字节外的所有字节）
        OperationResult crcResult = VerifyCrc(response);
        if (!crcResult.Success) return OperationResult<byte[]>.Fail(crcResult.ErrorMessage, crcResult.ErrorCode);

        byte[] data = new byte[byteCount];
        Buffer.BlockCopy(response, 3, data, 0, byteCount);
        return OperationResult<byte[]>.Ok(data);
    }

    /// <summary>
    /// 解析 FC01 读线圈响应。
    /// </summary>
    internal static OperationResult<byte[]> ParseReadCoilsResponse(byte[] response)
    {
        OperationResult exCheck = CheckException(response, minLength: 6);
        if (!exCheck.Success)
            return OperationResult<byte[]>.Fail(exCheck.ErrorMessage, exCheck.ErrorCode);

        int byteCount = response[2];
        if (response.Length < 5 + byteCount)
            return OperationResult<byte[]>.Fail("FC01 response data truncated", KernelErrorCode.ProtocolError);

        OperationResult crcResult = VerifyCrc(response);
        if (!crcResult.Success) return OperationResult<byte[]>.Fail(crcResult.ErrorMessage, crcResult.ErrorCode);

        byte[] data = new byte[byteCount];
        Buffer.BlockCopy(response, 3, data, 0, byteCount);
        return OperationResult<byte[]>.Ok(data);
    }

    /// <summary>
    /// 解析写响应（FC05/FC06/FC10），验证异常标志与 CRC。
    /// </summary>
    internal static OperationResult ParseWriteResponse(byte[] response)
    {
        // 最小: SlaveId(1)+FC(1)+Addr(2)+Value(2)+CRC(2) = 8
        OperationResult exCheck = CheckException(response, minLength: 8);
        if (!exCheck.Success) return exCheck;
        return VerifyCrc(response);
    }

    // -------------------------------------------------------------------------
    // CRC16（Modbus，多项式 0xA001，LSB first）
    // -------------------------------------------------------------------------

    /// <summary>
    /// 计算 Modbus CRC16（0xA001 多项式，从 offset=0 到 length 字节）。
    /// </summary>
    internal static ushort ComputeCrc(byte[] data, int length)
    {
        ushort crc = 0xFFFF;
        for (int i = 0; i < length; i++)
        {
            crc ^= data[i];
            for (int bit = 0; bit < 8; bit++)
            {
                if ((crc & 0x0001) != 0)
                {
                    crc >>= 1;
                    crc ^= 0xA001;
                }
                else
                {
                    crc >>= 1;
                }
            }
        }
        return crc;
    }

    // -------------------------------------------------------------------------
    // 内部辅助
    // -------------------------------------------------------------------------

    /// <summary>
    /// 将 PDU 包装为完整 RTU 帧：[SlaveId] [PDU...] [CRC-Lo] [CRC-Hi]。
    /// </summary>
    private static byte[] BuildFrame(byte slaveId, byte[] pdu)
    {
        // frame = SlaveId(1) + PDU(N) + CRC(2)
        byte[] frame = new byte[1 + pdu.Length + 2];
        frame[0] = slaveId;
        Buffer.BlockCopy(pdu, 0, frame, 1, pdu.Length);

        ushort crc = ComputeCrc(frame, 1 + pdu.Length);
        // CRC 以小端序追加（Lo byte 先）
        frame[1 + pdu.Length]     = (byte)(crc & 0xFF);
        frame[1 + pdu.Length + 1] = (byte)(crc >> 8);
        return frame;
    }

    /// <summary>
    /// 校验响应帧 CRC：取 response 除最后 2 字节计算，与末 2 字节（小端）对比。
    /// </summary>
    private static OperationResult VerifyCrc(byte[] response)
    {
        if (response.Length < 4)
            return OperationResult.Fail("RTU response too short to verify CRC", KernelErrorCode.ProtocolError);

        int dataLen = response.Length - 2;
        ushort expected = ComputeCrc(response, dataLen);
        ushort actual   = (ushort)(response[dataLen] | (response[dataLen + 1] << 8));

        if (expected != actual)
            return OperationResult.Fail(
                $"CRC mismatch: expected 0x{expected:X4} got 0x{actual:X4}", KernelErrorCode.ProtocolError);

        return OperationResult.Ok;
    }

    private static OperationResult CheckException(byte[] response, int minLength)
    {
        if (response is null || response.Length < minLength)
            return OperationResult.Fail("Modbus RTU response too short", KernelErrorCode.ProtocolError);

        byte fc = response[1];
        if ((fc & ExceptionMask) != 0)
        {
            byte exCode = response.Length > 2 ? response[2] : (byte)0;
            string msg = MapExceptionCode(exCode);
            return OperationResult.Fail($"Modbus RTU exception 0x{exCode:X2}: {msg}", KernelErrorCode.ProtocolError);
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
