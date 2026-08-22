// -----------------------------------------------------------------------------
// 文件: ModbusAddress.cs
// 层级: 插件层 / 协议
// 作用: 解析 Modbus 地址字符串，得到从站号、数据区与区内 0 基偏移。
// -----------------------------------------------------------------------------

using System.Globalization;
using CommunicationKernel.Core.Abstractions.Errors;
using CommunicationKernel.Core.Abstractions.Results;

namespace CommunicationKernel.Plugins.Protocol.Modbus.Core;

/// <summary>
/// Modbus 地址解析结果。
/// </summary>
/// <param name="UnitId">从站地址。</param>
/// <param name="Area">数据区，仅由地址决定。</param>
/// <param name="RegisterAddress">区内 0 基偏移。</param>
public readonly record struct ModbusAddressInfo(
    byte UnitId,
    ModbusDataArea Area,
    ushort RegisterAddress);

/// <summary>
/// Modbus 地址字符串解析器。
/// </summary>
/// <remarks>
/// 支持的格式：
/// <code>
///   40001 / 4x0001     → 保持寄存器，区内偏移 0
///   30001 / 3x0001     → 输入寄存器，区内偏移 0
///   10001 / 1x0001     → 离散输入，区内偏移 0
///   00001 / 0x0001     → 线圈，区内偏移 0
///   coil:N             → 线圈，区内偏移 N（显式前缀）
///   holding:N / input:N / discrete:N
///   [从站号:]地址      → 可选站号前缀，缺省取设备级站号
/// </code>
/// 裸数字（不带区号前缀、且小于 10000）一律按<b>保持寄存器区内偏移</b>解释，
/// 这是工程现场最常见的写法。
/// </remarks>
public static class ModbusAddress {

    /// <summary>从站地址的协议缺省值（设备未配置站号时使用）。</summary>
    public const byte FallbackUnitId = 1;

    /// <summary>
    /// 将设备级站号原文解析为默认从站 ID。
    /// 空值或越界一律回落到 <see cref="FallbackUnitId"/>，
    /// 避免"用户没填站号"直接导致整条路由不可用。
    /// </summary>
    /// <param name="station">RegisterRoute.station 原文，可为 null 或空。</param>
    public static byte ResolveDefaultUnitId(string? station) {
        // 空站号回落到协议缺省值 1，避免整条路由因未填站号而不可用
        if (string.IsNullOrWhiteSpace(station))
            return FallbackUnitId;

        // 仅接受 1-247（0 为广播、248-255 为保留）；解析失败同样回落
        return byte.TryParse(station.Trim(), NumberStyles.Integer, CultureInfo.InvariantCulture, out byte parsed)
               && parsed is >= ModbusLimits.MinUnitId and <= ModbusLimits.MaxUnitId
            ? parsed
            : FallbackUnitId;
    }

    /// <summary>
    /// 解析地址字符串。
    /// </summary>
    /// <param name="address">地址字符串，可含可选的 "从站号:" 前缀。</param>
    /// <param name="defaultUnitId">未带站号前缀时使用的从站 ID（来自设备级配置）。</param>
    public static OperationResult<ModbusAddressInfo> Parse(
        string? address, byte defaultUnitId = FallbackUnitId) {

        // 空地址无法映射到任何线圈/寄存器
        if (string.IsNullOrWhiteSpace(address))
            return Fail("地址为空");

        // 去掉首尾空白；站号缺省取设备级配置，后面的 "N:" 前缀可覆盖
        string text = address.Trim();
        byte unitId = defaultUnitId;

        // ── 可选站号前缀 "N:"，仅当冒号前是纯数字时才认定为站号 ──
        // （必须与 "coil:5" 这类区号前缀区分开）
        int colon = text.IndexOf(':');
        // 冒号落在第 1-3 位才可能是 "1:" / "12:" / "247:"，排除 "coil:5"
        if (colon > 0 && colon <= 3) {
            string head = text[..colon];
            // 冒号前必须是纯数字才认定为站号，否则留给后面的区号前缀解析
            if (byte.TryParse(head, NumberStyles.Integer, CultureInfo.InvariantCulture, out byte parsedUnit)) {
                // 从站号必须落在 1-247：0 是广播（本实现不支持），248+ 为保留
                if (parsedUnit is < ModbusLimits.MinUnitId or > ModbusLimits.MaxUnitId)
                    return Fail($"从站地址 {parsedUnit} 越界，有效范围 {ModbusLimits.MinUnitId}-{ModbusLimits.MaxUnitId}");

                // 剥掉站号前缀，后续只解析数据区与偏移
                unitId = parsedUnit;
                text   = text[(colon + 1)..].Trim();
                // "1:" 这种只有站号没有地址，无法定位寄存器
                if (text.Length == 0)
                    return Fail("站号前缀之后缺少地址");
            }
        }

        // ── 显式区号前缀 ──
        foreach ((string prefix, ModbusDataArea area) in NamedPrefixes) {
            // 前缀按长度降序排列，避免 holding: 被更短前缀遮蔽
            if (!text.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
                continue;

            // coil:N 的 N 是区内 0 基偏移，不是传统 1 起算编号
            string body = text[prefix.Length..];
            return TryParseOffset(body, out ushort namedOffset)
                ? Ok(unitId, area, namedOffset)
                : Fail($"'{body}' 不是有效的区内偏移（0-65535）");
        }

        // ── 4x / 3x / 1x / 0x 形式（如 4x0001） ──
        if (text.Length >= 3
            && (text[1] == 'x' || text[1] == 'X')
            && TryMapAreaDigit(text[0], out ModbusDataArea xArea)) {

            string body = text[2..];
            // 4x 后面必须是十进制序号（1-65535）
            if (!TryParseOffset(body, out ushort xOffset))
                return Fail($"'{body}' 不是有效的区内偏移（0-65535）");

            // 4x0001 表示区内第 1 个寄存器，偏移为 0
            return xOffset == 0
                ? Fail($"'{text}' 的编号从 1 起算，0 无效")
                : Ok(unitId, xArea, (ushort)(xOffset - 1));
        }

        // ── 纯数字：按传统编号或裸偏移解释 ──
        if (!int.TryParse(text, NumberStyles.Integer, CultureInfo.InvariantCulture, out int numeric))
            return Fail($"'{text}' 不是有效的 Modbus 地址");

        // 负数无法映射到 ushort 区内偏移
        if (numeric < 0)
            return Fail("地址不能为负数");

        // 按字符串长度区分 5/6 位传统编号与裸偏移
        return MapNumeric(unitId, numeric, text);
    }

    /// <summary>该文本是否为全数字（用于识别传统定长编号）。</summary>
    private static bool IsAllDigits(string text) {
        // 逐字符检查，避免 "40001 " 这类夹杂空白被当成传统编号
        foreach (char c in text) {
            if (c is < '0' or > '9') return false;
        }
        // 空串不是数字地址
        return text.Length > 0;
    }

    // =========================================================================
    // 内部实现
    // =========================================================================

    /// <summary>显式区号前缀，按长度降序匹配以避免前缀互相遮蔽。</summary>
    private static readonly (string Prefix, ModbusDataArea Area)[] NamedPrefixes = {
        ("discrete:", ModbusDataArea.DiscreteInput),
        ("holding:",  ModbusDataArea.HoldingRegister),
        ("input:",    ModbusDataArea.InputRegister),
        ("coil:",     ModbusDataArea.Coil)
    };

    /// <summary>将 5 位传统编号的首位数字映射到数据区。</summary>
    private static bool TryMapAreaDigit(char digit, out ModbusDataArea area) {
        // 0=线圈 1=离散输入 3=输入寄存器 4=保持寄存器；2 不是标准区号
        switch (digit) {
            case '0': area = ModbusDataArea.Coil;            return true;
            case '1': area = ModbusDataArea.DiscreteInput;   return true;
            case '3': area = ModbusDataArea.InputRegister;   return true;
            case '4': area = ModbusDataArea.HoldingRegister; return true;
            default:  area = ModbusDataArea.HoldingRegister; return false;
        }
    }

    /// <summary>
    /// 解释纯数字地址。
    /// </summary>
    /// <remarks>
    /// 传统 Modbus 编号是<b>定长</b>的（5 位，部分厂商用 6 位），首位为区号、其余为从 1 起算的序号。
    /// 因此必须按<b>字符串长度</b>判定，不能按数值大小——
    /// <c>"00001"</c> 的数值是 1，若按数值判定会落进"裸偏移"分支被当成保持寄存器，
    /// 而它实际是线圈 0 号。
    /// 其余长度的纯数字按保持寄存器区内 0 基偏移解释（工程现场最常见的写法）。
    /// </remarks>
    private static OperationResult<ModbusAddressInfo> MapNumeric(byte unitId, int numeric, string original) {
        // 定长传统编号：5 位（如 40001）或 6 位（如 400001）
        if (IsAllDigits(original) && original.Length is 5 or 6) {
            // 5 位除 10000、6 位除 100000，分离区号与从 1 起算的序号
            int divisor   = original.Length == 5 ? 10000 : 100000;
            int areaDigit = numeric / divisor;
            int ordinal   = numeric % divisor;

            // 区号只认 0/1/3/4，2xxxx 不是标准 Modbus 数据区
            if (!TryMapAreaDigit((char)('0' + areaDigit), out ModbusDataArea area))
                return Fail($"'{original}' 的区号 {areaDigit} 无效，有效区号为 0/1/3/4");

            // 传统编号从 1 起算，40000 这种序号 0 无效
            if (ordinal == 0)
                return Fail($"'{original}' 的序号从 1 起算，0 无效");

            // 转成区内 0 基偏移交给 PDU 使用
            return Ok(unitId, area, (ushort)(ordinal - 1));
        }

        // 裸偏移：保持寄存器区，0 基
        return numeric > ushort.MaxValue
            ? Fail($"地址 {numeric} 超出 0-65535 范围")
            : Ok(unitId, ModbusDataArea.HoldingRegister, (ushort)numeric);
    }

    private static bool TryParseOffset(string body, out ushort offset) {
        offset = 0;
        // 区内偏移必须是十进制整数
        if (!int.TryParse(body.Trim(), NumberStyles.Integer, CultureInfo.InvariantCulture, out int value))
            return false;
        // 负值或超过 65535 都无法放入 PDU 的 16 位地址字段
        if (value is < 0 or > ushort.MaxValue)
            return false;

        offset = (ushort)value;
        return true;
    }

    private static OperationResult<ModbusAddressInfo> Ok(byte unitId, ModbusDataArea area, ushort offset)
        => OperationResult<ModbusAddressInfo>.Ok(new ModbusAddressInfo(unitId, area, offset));

    private static OperationResult<ModbusAddressInfo> Fail(string message)
        => OperationResult<ModbusAddressInfo>.Fail(message, KernelErrorCode.InvalidArgument);
}
