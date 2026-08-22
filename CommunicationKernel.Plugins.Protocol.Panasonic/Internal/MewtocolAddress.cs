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
        if (string.IsNullOrWhiteSpace(address))
            return OperationResult<MewtocolAddressInfo>.Fail(
                "address is empty", KernelErrorCode.InvalidArgument);
        try
        {
            return DoParse(address.Trim(), defaultStation);
        }
        catch (Exception ex)
        {
            return OperationResult<MewtocolAddressInfo>.Fail(
                $"invalid MEWTOCOL address '{address}': {ex.Message}", KernelErrorCode.InvalidArgument);
        }
    }

    private static OperationResult<MewtocolAddressInfo> DoParse(string raw, byte defaultStation)
    {
        // 缺省取设备级站号；下方的 "NN:" 前缀分支可覆盖
        byte station = defaultStation;

        // 分支1：站号前缀 "01:DT100" 或 "05:R10A"（可选，仅多站链路需要）
        int colonIdx = raw.IndexOf(':');
        if (colonIdx > 0 && colonIdx <= 2)
        {
            if (byte.TryParse(raw[..colonIdx], out byte parsedStation) && parsedStation is >= 1 and <= 99)
            {
                station = parsedStation;
                raw     = raw[(colonIdx + 1)..];
            }
        }

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

    private static OperationResult<MewtocolAddressInfo> Ok(
        byte station, MewtocolArea area, int index, int bitIndex, bool isBit)
        => OperationResult<MewtocolAddressInfo>.Ok(
            new MewtocolAddressInfo(station, area, index, bitIndex, isBit));

    private static int ParseInt(string s)
    {
        if (!int.TryParse(s, NumberStyles.Integer, CultureInfo.InvariantCulture, out int v) || v < 0)
            throw new ArgumentException($"invalid number: '{s}'");
        return v;
    }
}
