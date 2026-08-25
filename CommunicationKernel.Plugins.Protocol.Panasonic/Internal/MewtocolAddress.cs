// -----------------------------------------------------------------------------
// 文件: MewtocolAddress.cs
// 层级: Plugins / Panasonic / Internal
// 作用: 松下 MEWTOCOL 地址字符串解析。
// 支持格式:
//   [SS:]area_address
//   SS  = 站号十六进制 1-99（默认 01）
//   area_address:
//     X5 / X00005         → 外部输入触点（位）
//     Y3 / Y00003         → 外部输出触点（位）
//     R100 / R00100       → 内部继电器触点（位，纯字号访问整字）
//     R10A                → 内部继电器位（字10，位A=10）
//     DT100 / D100        → 数据寄存器（字）
//     WR50  / W50         → 链接继电器（字）
// 说明:
//   协议地址解析属于协议语义，必须限定在插件 DLL 内部。
// -----------------------------------------------------------------------------

using System;
using System.Globalization;
using CommunicationKernel.Core.Abstractions.Errors;
using CommunicationKernel.Core.Abstractions.Results;

namespace CommunicationKernel.Plugins.Protocol.Panasonic.Internal;

/// <summary>松下 MEWTOCOL 数据区分类。</summary>
internal enum MewtocolArea { X, Y, R, DT, WR }

/// <summary>松下地址解析结果。</summary>
internal readonly record struct MewtocolAddressInfo(
    byte          Station,   // 站号 1-99（十进制）
    MewtocolArea  Area,
    int           Index,     // 字号或线圈号
    int           BitIndex,  // -1=整字/整触点；0-F=位号
    bool          IsBit);    // true=触点/位，false=数据字

/// <summary>
/// MEWTOCOL 地址解析器，将字符串地址转换为结构化地址信息。
/// </summary>
internal static class MewtocolAddress
{
    /// <summary>MEWTOCOL 站号的协议缺省值（未配置站号时使用）。</summary>
    internal const byte FallbackStation = 1;

    /// <summary>
    /// 将设备级站号原文解析为默认站号。
    /// 空值或非法值一律回落到 <see cref="FallbackStation"/>，
    /// 保证「用户没填站号」不会导致整条路由不可用。
    /// </summary>
    /// <param name="station">RegisterRoute.station 原文，可为 null / 空。</param>
    /// <returns>1-99 范围内的站号。</returns>
    internal static byte ResolveDefaultStation(string? station)
    {
        // 空站号回落到协议缺省值 1，避免整条路由因未填站号而不可用
        if (string.IsNullOrWhiteSpace(station))
            return FallbackStation;

        // MEWTOCOL 站号有效范围 1-99
        return byte.TryParse(station.Trim(), out byte parsed) && parsed is >= 1 and <= 99
            ? parsed
            : FallbackStation;
    }

    /// <summary>解析地址字符串。</summary>
    /// <param name="address">地址字符串，可含可选的 "站号:" 前缀。</param>
    /// <param name="defaultStation">
    /// 地址未带前缀时使用的站号，通常来自设备级站号配置。
    /// 普通场景下操作员只需填写 "DT100"，站号在设备表单中统一配置。
    /// </param>
    internal static OperationResult<MewtocolAddressInfo> Parse(
        string address, byte defaultStation = FallbackStation)
    {
        // 空地址无法映射到任何触点/寄存器
        if (string.IsNullOrWhiteSpace(address))
            return OperationResult<MewtocolAddressInfo>.Fail(
                "address is empty", KernelErrorCode.InvalidArgument);
        try
        {
            // 去掉首尾空白后按区号前缀分流
            return DoParse(address.Trim(), defaultStation);
        }
        catch (Exception ex)
        {
            // ParseInt 对非法数字抛 ArgumentException，在此统一转为 Fail
            return OperationResult<MewtocolAddressInfo>.Fail(
                $"invalid MEWTOCOL address '{address}': {ex.Message}", KernelErrorCode.InvalidArgument);
        }
    }

    // ============================================================================
    // 内部解析
    // ============================================================================

    private static OperationResult<MewtocolAddressInfo> DoParse(string raw, byte defaultStation)
    {
        // 站号一律取设备级配置，地址无权覆盖
        byte station = defaultStation;

        // ── 拒绝已废弃的站号前缀 "NN:" ──
        // 站号是 RouteKey 的组成部分，一条路由标识的就是「某台设备的某个站」。
        // 允许地址覆盖会让同一条路由悄悄读写两个物理站，
        // 而路由表、状态灯与串口帧间静默全都只认一个——在共享 RS-485 上直接制造帧冲突。
        // 明确拒绝而非静默忽略：静默改变读写目标比报错危险得多。
        int colonIdx = raw.IndexOf(':');
        if (colonIdx > 0 && colonIdx <= 2 && byte.TryParse(raw[..colonIdx], out _))
        {
            return OperationResult<MewtocolAddressInfo>.Fail(
                $"地址 '{raw}' 不接受站号前缀。站号是设备属性，请在设备配置的「站号」里填写；" +
                "多站链路请为每个站各建一条路由。",
                KernelErrorCode.InvalidArgument);
        }

        // 区号匹配不区分大小写
        string a = raw.ToUpperInvariant();

        // 分支2：X 区（外部输入触点）
        if (a.StartsWith('X'))
            return Ok(station, MewtocolArea.X, ParseInt(a[1..]), -1, true);

        // 分支3：Y 区（外部输出触点）
        if (a.StartsWith('Y'))
            return Ok(station, MewtocolArea.Y, ParseInt(a[1..]), -1, true);

        // 分支4：R 区（内部继电器，含位访问）
        if (a.StartsWith('R'))
            return ParseR(station, a[1..]);

        // 分支5：DT 区（数据寄存器，字）
        if (a.StartsWith("DT"))
            return Ok(station, MewtocolArea.DT, ParseInt(a[2..]), -1, false);

        // 分支6：WR 区（链接继电器，字），允许简写 W
        if (a.StartsWith("WR"))
            return Ok(station, MewtocolArea.WR, ParseInt(a[2..]), -1, false);
        if (a.StartsWith('W'))
            return Ok(station, MewtocolArea.WR, ParseInt(a[1..]), -1, false);

        // 分支7：D 单字母缩写 = DT
        if (a.StartsWith('D'))
            return Ok(station, MewtocolArea.DT, ParseInt(a[1..]), -1, false);

        return OperationResult<MewtocolAddressInfo>.Fail(
            $"unknown area prefix in '{raw}'", KernelErrorCode.InvalidArgument);
    }

    /// <summary>
    /// 解析 R 区地址：纯数字=整触点，末尾 A-F=位号。
    /// 示例：R100 → word=100,bit=-1；R10A → word=10,bit=10。
    /// </summary>
    private static OperationResult<MewtocolAddressInfo> ParseR(byte station, string body)
    {
        // R 后面必须有字号
        if (body.Length == 0)
            return OperationResult<MewtocolAddressInfo>.Fail(
                "R address body is empty", KernelErrorCode.InvalidArgument);

        char last = body[^1];

        // 末位为十六进制字母 A-F → 位访问
        if (last is >= 'A' and <= 'F')
        {
            int wordIdx = ParseInt(body[..^1]);
            int bitIdx  = Convert.ToInt32(last.ToString(), 16);
            return Ok(station, MewtocolArea.R, wordIdx, bitIdx, true);
        }

        // 纯数字 → 整触点访问
        return Ok(station, MewtocolArea.R, ParseInt(body), -1, true);
    }

    /// <summary>构造地址解析成功结果的简写。</summary>
    private static OperationResult<MewtocolAddressInfo> Ok(
        byte station, MewtocolArea area, int index, int bitIndex, bool isBit)
        => OperationResult<MewtocolAddressInfo>.Ok(
            new MewtocolAddressInfo(station, area, index, bitIndex, isBit));

    /// <summary>把地址里的十进制数字段转成整数；非法输入返回 -1 交由调用方拒绝。</summary>
    /// <remarks>用 InvariantCulture：地址是协议文本，不受运行机器的区域设置影响。</remarks>
    private static int ParseInt(string s)
    {
        // 非法或负数无法作为 MEWTOCOL 字号/触点号
        if (!int.TryParse(s, NumberStyles.Integer, CultureInfo.InvariantCulture, out int v) || v < 0)
            throw new ArgumentException($"invalid number: '{s}'");
        return v;
    }
}
