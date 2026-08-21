// -----------------------------------------------------------------------------
// 文件: ModbusAddress.cs
// 层级: Plugins / Modbus.Tcp / Internal
// 作用: Modbus 地址字符串解析。
// 支持格式:
//   40001  / 4x0001  → 保持寄存器，地址 0（4xxxx 偏移）
//   0      ~ 65535   → 保持寄存器，原始 0 基地址
//   coil:0 / 0xxxx   → 线圈，0 基地址
//   [unitId:]address  → 单元号前缀（可选，缺省取设备级站号）
// 说明:
//   协议地址解析属于协议语义，必须限定在插件 DLL 内部。
//   从站地址的常规来源是「设备级站号配置」（RegisterRoute.station），
//   地址前缀仅作为 RS-485 一主多从场景下的逐变量覆盖手段，
//   普通用户在界面上只需填写干净的 "40001"，无需书写 "1:40001"。
// -----------------------------------------------------------------------------
using System;
using CommunicationKernel.Core.Abstractions.Errors;
using CommunicationKernel.Core.Abstractions.Results;

namespace CommunicationKernel.Plugins.Modbus.Tcp.Internal;

/// <summary>Modbus 地址解析结果。</summary>
internal readonly record struct ModbusAddressInfo(
    byte UnitId,
    ushort RegisterAddress,
    bool IsCoil);

/// <summary>
/// Modbus 地址解析器，将字符串地址转换为 (UnitId, RegisterAddress, IsCoil)。
/// </summary>
internal static class ModbusAddress
{
    /// <summary>Modbus 从站地址的协议缺省值（未配置站号时使用）。</summary>
    internal const byte FallbackUnitId = 1;

    /// <summary>
    /// 将设备级站号原文解析为默认从站 ID。
    /// 空值或非法值一律回落到 <see cref="FallbackUnitId"/>，
    /// 保证「用户没填站号」不会导致整条路由不可用。
    /// </summary>
    /// <param name="station">RegisterRoute.station 原文，可为 null / 空。</param>
    /// <returns>1-247 范围内的从站 ID。</returns>
    internal static byte ResolveDefaultUnitId(string? station)
    {
        if (string.IsNullOrWhiteSpace(station))
            return FallbackUnitId;

        // Modbus 从站地址有效范围 1-247，248-255 为保留值
        return byte.TryParse(station.Trim(), out byte parsed) && parsed is >= 1 and <= 247
            ? parsed
            : FallbackUnitId;
    }

    /// <summary>
    /// 解析地址字符串。
    /// </summary>
    /// <param name="address">地址字符串，可含可选的 "从站号:" 前缀。</param>
    /// <param name="defaultUnitId">
    /// 地址未带前缀时使用的从站 ID，通常来自设备级站号配置。
    /// </param>
    /// <returns>解析结果。</returns>
    internal static OperationResult<ModbusAddressInfo> Parse(
        string address, byte defaultUnitId = FallbackUnitId)
    {
        if (string.IsNullOrWhiteSpace(address))
            return OperationResult<ModbusAddressInfo>.Fail(
                "address is empty", KernelErrorCode.InvalidArgument);

        try
        {
            return DoParse(address.Trim(), defaultUnitId);
        }
        catch
        {
            return OperationResult<ModbusAddressInfo>.Fail(
                $"invalid Modbus address: {address}", KernelErrorCode.InvalidArgument);
        }
    }

    private static OperationResult<ModbusAddressInfo> DoParse(string addr, byte defaultUnitId)
    {
        // 缺省取设备级站号；下方的 "N:" 前缀分支可覆盖
        byte unitId = defaultUnitId;

        // 分支1: unitId 前缀 "1:40001" 或 "1:coil:0"
        int colonIdx = addr.IndexOf(':');
        if (colonIdx > 0 && colonIdx < 4)
        {
            string unitPart = addr[..colonIdx];
            if (byte.TryParse(unitPart, out byte parsedUnit))
            {
                unitId = parsedUnit;
                addr = addr[(colonIdx + 1)..];
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
