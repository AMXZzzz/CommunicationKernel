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
    internal static OperationResult<ModbusAsciiAddressInfo> Parse(string address)
    {
        if (string.IsNullOrWhiteSpace(address))
            return OperationResult<ModbusAsciiAddressInfo>.Fail(
                "address is empty", KernelErrorCode.InvalidArgument);

        try
        {
            return DoParse(address.Trim());
        }
        catch
        {
            return OperationResult<ModbusAsciiAddressInfo>.Fail(
                $"invalid Modbus ASCII address: {address}", KernelErrorCode.InvalidArgument);
        }
    }

    private static OperationResult<ModbusAsciiAddressInfo> DoParse(string addr)
    {
        byte slaveId = 1;

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
