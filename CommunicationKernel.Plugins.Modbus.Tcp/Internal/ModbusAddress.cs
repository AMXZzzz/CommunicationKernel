// -----------------------------------------------------------------------------
// 文件: ModbusAddress.cs
// 层级: Plugins / Modbus.Tcp / Internal
// 作用: Modbus 地址字符串解析。
// 支持格式:
//   40001  / 4x0001  → 保持寄存器，地址 0（4xxxx 偏移）
//   0      ~ 65535   → 保持寄存器，原始 0 基地址
//   coil:0 / 0xxxx   → 线圈，0 基地址
//   [unitId:]address  → 单元号前缀（默认 1）
// 说明:
//   协议地址解析属于协议语义，必须限定在插件 DLL 内部。
// -----------------------------------------------------------------------------
using System;
using CommunicationKernel.Core.Abstractions.Errors;
using CommunicationKernel.Core.Abstractions.Results;

namespace CommunicationKernel.Plugins.Modbus.Tcp.Internal;

/// <summary>Modbus 地址解析结果。</summary>
internal readonly record struct ModbusAddressInfo(
    byte   UnitId,
    ushort RegisterAddress,
    bool   IsCoil);

/// <summary>
/// Modbus 地址解析器，将字符串地址转换为 (UnitId, RegisterAddress, IsCoil)。
/// </summary>
internal static class ModbusAddress
{
    /// <summary>
    /// 解析地址字符串。
    /// </summary>
    /// <param name="address">地址字符串。</param>
    /// <returns>解析结果。</returns>
    internal static OperationResult<ModbusAddressInfo> Parse(string address)
    {
        if (string.IsNullOrWhiteSpace(address))
            return OperationResult<ModbusAddressInfo>.Fail(
                "address is empty", KernelErrorCode.InvalidArgument);

        try
        {
            return DoParse(address.Trim());
        }
        catch
        {
            return OperationResult<ModbusAddressInfo>.Fail(
                $"invalid Modbus address: {address}", KernelErrorCode.InvalidArgument);
        }
    }

    private static OperationResult<ModbusAddressInfo> DoParse(string addr)
    {
        byte unitId = 1;

        // 分支1: unitId 前缀 "1:40001" 或 "1:coil:0"
        int colonIdx = addr.IndexOf(':');
        if (colonIdx > 0 && colonIdx < 4)
        {
            string unitPart = addr[..colonIdx];
            if (byte.TryParse(unitPart, out byte parsedUnit))
            {
                unitId = parsedUnit;
                addr   = addr[(colonIdx + 1)..];
            }
        }

        // 分支2: 显式线圈前缀 "coil:N" / "0xxxx"
        if (addr.StartsWith("coil:", StringComparison.OrdinalIgnoreCase))
        {
            ushort coilAddr = ushort.Parse(addr[5..]);
            return OperationResult<ModbusAddressInfo>.Ok(new ModbusAddressInfo(unitId, coilAddr, true));
        }

        if (!int.TryParse(addr, out int numericAddr))
            return OperationResult<ModbusAddressInfo>.Fail(
                $"non-numeric address: {addr}", KernelErrorCode.InvalidArgument);

        // 分支3: 4xxxx / 40001+ → 保持寄存器（0 基偏移）
        if (numericAddr >= 40001)
            return OperationResult<ModbusAddressInfo>.Ok(
                new ModbusAddressInfo(unitId, (ushort)(numericAddr - 40001), false));

        // 分支4: 0xxxx（0-9999）→ 线圈
        if (numericAddr is >= 0 and <= 9999 && addr.Length >= 4 && addr[0] == '0')
            return OperationResult<ModbusAddressInfo>.Ok(
                new ModbusAddressInfo(unitId, (ushort)numericAddr, true));

        // 分支5: 裸数字 → 保持寄存器 0 基地址
        if (numericAddr is >= 0 and <= 65535)
            return OperationResult<ModbusAddressInfo>.Ok(
                new ModbusAddressInfo(unitId, (ushort)numericAddr, false));

        return OperationResult<ModbusAddressInfo>.Fail(
            $"address out of range: {addr}", KernelErrorCode.InvalidArgument);
    }
}
