// -----------------------------------------------------------------------------
// 文件: ModbusRtuAddress.cs
// 层级: Plugins / Modbus.Rtu / Internal
// 作用: Modbus RTU 地址字符串解析。
// 支持格式:
//   40001  / 4x0001  → 保持寄存器，地址 0（4xxxx 偏移）
//   0      ~ 65535   → 保持寄存器，原始 0 基地址
//   coil:0 / 0xxxx   → 线圈，0 基地址
//   [slaveId:]address → 从站号前缀（默认 1）
// -----------------------------------------------------------------------------

using System;
using CommunicationKernel.Core.Abstractions.Errors;
using CommunicationKernel.Core.Abstractions.Results;

namespace CommunicationKernel.Plugins.Modbus.Rtu.Internal;

/// <summary>Modbus RTU 地址解析结果。</summary>
internal readonly record struct ModbusRtuAddressInfo(
    byte SlaveId,
    ushort RegisterAddress,
    bool IsCoil);

/// <summary>
/// Modbus RTU 地址解析器，将字符串地址转换为 (SlaveId, RegisterAddress, IsCoil)。
/// </summary>
internal static class ModbusRtuAddress
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
    internal static OperationResult<ModbusRtuAddressInfo> Parse(
        string address, byte defaultSlaveId = FallbackSlaveId)
    {
        if (string.IsNullOrWhiteSpace(address))
            return OperationResult<ModbusRtuAddressInfo>.Fail(
                "address is empty", KernelErrorCode.InvalidArgument);

        try
        {
            return DoParse(address.Trim(), defaultSlaveId);
        }
        catch
        {
            return OperationResult<ModbusRtuAddressInfo>.Fail(
                $"invalid Modbus RTU address: {address}", KernelErrorCode.InvalidArgument);
        }
    }

    private static OperationResult<ModbusRtuAddressInfo> DoParse(string addr, byte defaultSlaveId)
    {
        // 缺省取设备级站号；下方的 "N:" 前缀分支可覆盖（RS-485 一主多从）
        byte slaveId = defaultSlaveId;

        // 分支1: slaveId 前缀 "1:40001" 或 "1:coil:0"
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

        // 分支2: 显式线圈前缀 "coil:N"
        if (addr.StartsWith("coil:", StringComparison.OrdinalIgnoreCase))
        {
            ushort coilAddr = ushort.Parse(addr[5..]);
            return OperationResult<ModbusRtuAddressInfo>.Ok(new ModbusRtuAddressInfo(slaveId, coilAddr, true));
        }

        if (!int.TryParse(addr, out int numericAddr))
            return OperationResult<ModbusRtuAddressInfo>.Fail(
                $"non-numeric address: {addr}", KernelErrorCode.InvalidArgument);

        // 分支3: 4xxxx / 40001+ → 保持寄存器（0 基偏移）
        if (numericAddr >= 40001)
            return OperationResult<ModbusRtuAddressInfo>.Ok(
                new ModbusRtuAddressInfo(slaveId, (ushort)(numericAddr - 40001), false));

        // 分支4: 0xxxx（0-9999）→ 线圈
        if (numericAddr is >= 0 and <= 9999 && addr.Length >= 4 && addr[0] == '0')
            return OperationResult<ModbusRtuAddressInfo>.Ok(
                new ModbusRtuAddressInfo(slaveId, (ushort)numericAddr, true));

        // 分支5: 裸数字 → 保持寄存器 0 基地址
        if (numericAddr is >= 0 and <= 65535)
            return OperationResult<ModbusRtuAddressInfo>.Ok(
                new ModbusRtuAddressInfo(slaveId, (ushort)numericAddr, false));

        return OperationResult<ModbusRtuAddressInfo>.Fail(
            $"address out of range: {addr}", KernelErrorCode.InvalidArgument);
    }
}
