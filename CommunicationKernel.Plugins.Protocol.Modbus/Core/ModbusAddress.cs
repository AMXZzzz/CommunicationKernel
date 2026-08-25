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
/// </code>
/// 裸数字（不带区号前缀、且小于 10000）一律按<b>保持寄存器区内偏移</b>解释，
/// 这是工程现场最常见的写法。
/// <para>
/// <b>地址里不接受站号前缀。</b>曾支持过 <c>1:40001</c> 这种写法，现已明确拒绝——
/// 站号是<b>设备级</b>属性，只能在设备配置里填，理由见 <see cref="Parse"/>。
/// </para>
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
    /// <param name="address">
    /// 地址字符串。<b>不接受站号前缀</b>——站号请在设备配置里填。
    /// </param>
    /// <param name="defaultUnitId">从站 ID，来自设备级配置。</param>
    /// <remarks>
    /// <para>
    /// <b>为什么禁止在地址里写站号。</b>站号是 <c>RouteKey</c> 的组成部分
    /// （协议 + 介质 + 地址 + 端口 + <b>站号</b>），一条路由标识的就是「某台设备的某个从站」。
    /// 允许地址覆盖站号会同时破坏三件事：
    /// </para>
    /// <list type="number">
    ///   <item>
    ///     <b>路由身份失效。</b>同一条路由会悄悄读写两个不同的物理从站，
    ///     而路由表、状态灯、在线判定全都只认一个。某个从站掉线不会反映到任何地方。
    ///   </item>
    ///   <item>
    ///     <b>绕过并发调度。</b>写串行化与串口帧间静默都按 RouteKey 归组。
    ///     地址里换个站号，等于让这些变量跳出该路由的调度组，
    ///     在共享 RS-485 总线上直接制造帧冲突。
    ///   </item>
    ///   <item>
    ///     <b>两个真相来源。</b>设备配置里填了站号 1、变量地址写 <c>2:40001</c>，
    ///     界面上这台设备显示站号 1，实际却在读站号 2。这种不一致没有任何报错。
    ///   </item>
    /// </list>
    /// <para>
    /// 因此遇到 <c>N:</c> 前缀一律<b>明确拒绝</b>而非静默忽略：
    /// 静默把 <c>2:40001</c> 当成别的地址去解析，会让既有配置悄悄改变读写目标——
    /// 那比报错危险得多。多站链路请为每个从站建一条独立路由。
    /// </para>
    /// </remarks>
    public static OperationResult<ModbusAddressInfo> Parse(
        string? address, byte defaultUnitId = FallbackUnitId) {

        // 空地址无法映射到任何线圈/寄存器
        if (string.IsNullOrWhiteSpace(address))
            return Fail("地址为空");

        // 站号一律取设备级配置，地址无权覆盖
        string text = address.Trim();
        byte unitId = defaultUnitId;

        // ── 拒绝已废弃的站号前缀 "N:" ──
        // 只拦"冒号前是纯数字"的形式，coil:5 / holding:5 这类区号前缀不受影响
        int colon = text.IndexOf(':');
        if (colon > 0 && colon <= 3
            && byte.TryParse(text[..colon], NumberStyles.Integer, CultureInfo.InvariantCulture, out _)) {
            return Fail(
                $"地址 '{text}' 不接受站号前缀。站号是设备属性，请在设备配置的「站号」里填写；" +
                "多个从站请各建一条路由。");
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

    /// <summary>解析区内偏移，必须是能放进 PDU 16 位地址字段的十进制整数。</summary>
    /// <returns>解析是否成功；失败时 <paramref name="offset"/> 为 0。</returns>
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

    /// <summary>构造解析成功结果的简写。</summary>
    private static OperationResult<ModbusAddressInfo> Ok(byte unitId, ModbusDataArea area, ushort offset)
        => OperationResult<ModbusAddressInfo>.Ok(new ModbusAddressInfo(unitId, area, offset));

    /// <summary>构造解析失败结果的简写；地址格式问题一律归为 InvalidArgument。</summary>
    private static OperationResult<ModbusAddressInfo> Fail(string message)
        => OperationResult<ModbusAddressInfo>.Fail(message, KernelErrorCode.InvalidArgument);
}
