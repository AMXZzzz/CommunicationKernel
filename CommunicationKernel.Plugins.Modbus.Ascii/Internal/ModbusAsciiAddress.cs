// -----------------------------------------------------------------------------
// 文件: ModbusAsciiAddress.cs
// 层级: Plugins / Modbus.Ascii / Internal
// 作用: Modbus ASCII 地址字符串解析。
// 格式与 RTU 相同:
//   [slaveId:]address  /  coil:N  /  40001+  /  0xxxx（线圈）
// -----------------------------------------------------------------------------

using System;
using CommunicationKernel.Core.Abstractions.Errors;
using CommunicationKernel.Core.Abstractions.Results;

namespace CommunicationKernel.Plugins.Modbus.Ascii.Internal;

/// <summary>Modbus ASCII 地址解析结果。</summary>
internal readonly record struct ModbusAsciiAddressInfo(
    byte SlaveId,
    ushort RegisterAddress,
    bool IsCoil);

/// <summary>
/// Modbus ASCII 地址解析器。
/// </summary>
internal static class ModbusAsciiAddress
{
    /// <summary>Modbus 从站地址的协议缺省值（未配置站号时使用）。</summary>
    internal const byte FallbackSlaveId = 1;

    /// <summary>
    /// 将设备级站号原文解析为默认从站 ID。
    /// 空值或非法值一律回落到 <see cref="FallbackSlaveId"/>。
    /// </summary>
    /// <param name="station">RegisterRoute.station 原文，可为 null / 空。</param>
    internal static byte ResolveDefaultSlaveId(string? station)
    {
        if (string.IsNullOrWhiteSpace(station))
            return FallbackSlaveId;

        // Modbus 从站地址有效范围 1-247
        return byte.TryParse(station.Trim(), out byte parsed) && parsed is >= 1 and <= 247
            ? parsed
            : FallbackSlaveId;
    }

    /// <param name="address">地址字符串，可含可选的 "从站号:" 前缀。</param>
    /// <param name="defaultSlaveId">地址未带前缀时使用的从站 ID（来自设备级站号）。</param>
    internal static OperationResult<ModbusAsciiAddressInfo> Parse(
        string address, byte defaultSlaveId = FallbackSlaveId)
    {
        if (string.IsNullOrWhiteSpace(address))
            return OperationResult<ModbusAsciiAddressInfo>.Fail(
                "address is empty", KernelErrorCode.InvalidArgument);

        try
        {
            return DoParse(address.Trim(), defaultSlaveId);
        }
        catch
        {
            return OperationResult<ModbusAsciiAddressInfo>.Fail(
                $"invalid Modbus ASCII address: {address}", KernelErrorCode.InvalidArgument);
        }
    }

    private static OperationResult<ModbusAsciiAddressInfo> DoParse(string addr, byte defaultSlaveId)
    {
        // 缺省取设备级站号；下方的 "N:" 前缀分支可覆盖（RS-485 一主多从）
        byte slaveId = defaultSlaveId;

        int colonIdx = addr.IndexOf(':');
        if (colonIdx > 0 && colonIdx < 4)
        {
            string slavePart = addr[..colonIdx];
            if (byte.TryParse(slavePart, out byte parsedSlave))
            {
                slaveId = parsedSlave;
                addr = addr[(colonIdx + 1)..];
            }
        }

        if (addr.StartsWith("coil:", StringComparison.OrdinalIgnoreCase))
        {
            ushort coilAddr = ushort.Parse(addr[5..]);
            return OperationResult<ModbusAsciiAddressInfo>.Ok(new ModbusAsciiAddressInfo(slaveId, coilAddr, true));
        }

        if (!int.TryParse(addr, out int numericAddr))
            return OperationResult<ModbusAsciiAddressInfo>.Fail(
                $"non-numeric address: {addr}", KernelErrorCode.InvalidArgument);

        if (numericAddr >= 40001)
            return OperationResult<ModbusAsciiAddressInfo>.Ok(
                new ModbusAsciiAddressInfo(slaveId, (ushort)(numericAddr - 40001), false));

        if (numericAddr is >= 0 and <= 9999 && addr.Length >= 4 && addr[0] == '0')
            return OperationResult<ModbusAsciiAddressInfo>.Ok(
                new ModbusAsciiAddressInfo(slaveId, (ushort)numericAddr, true));

        if (numericAddr is >= 0 and <= 65535)
            return OperationResult<ModbusAsciiAddressInfo>.Ok(
                new ModbusAsciiAddressInfo(slaveId, (ushort)numericAddr, false));

        return OperationResult<ModbusAsciiAddressInfo>.Fail(
            $"address out of range: {addr}", KernelErrorCode.InvalidArgument);
    }
}
