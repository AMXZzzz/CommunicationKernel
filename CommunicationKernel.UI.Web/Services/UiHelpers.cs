// -----------------------------------------------------------------------------
// 文件: Services/UiHelpers.cs
// 层级: UI 层 — Blazor Server
// 作用: 协议色条等纯展示辅助，不涉及通讯语义。
//
// 为什么不按协议名分支:
//   这里曾按 "siemens" / "s7" / "panasonic" / "mewtocol" / "modbus" 子串选颜色。
//   那不构成协议解析，但让 UI 对协议命名产生了隐性预期——
//   新插件的色条会静默落到默认灰，没人会注意到，也没有任何报错。
//   现改为按 ProtocolId 的稳定哈希分配调色板槽位：
//   UI 不需要认识任何协议名，新插件自动获得一个固定且各异的颜色。
// -----------------------------------------------------------------------------

using System;

namespace CommunicationKernel.UI.Web.Services;

/// <summary>纯展示辅助方法，不含任何通讯或协议语义。</summary>
internal static class UiHelpers
{
    /// <summary>
    /// 调色板槽位数量，必须与 theme.css 中 .proto-bar.p0 … 的定义条数一致。
    /// </summary>
    private const int PaletteSlots = 6;

    /// <summary>FNV-1a 32 位的偏移基准。</summary>
    private const uint FnvOffsetBasis = 2166136261;

    /// <summary>FNV-1a 32 位的质数乘子。</summary>
    private const uint FnvPrime = 16777619;

    /// <summary>
    /// 按协议 ID 取一个稳定的色条 CSS 类名（p0 … p5）。
    /// </summary>
    /// <param name="protocolId">协议 ID；为空时返回首个槽位。</param>
    /// <returns>形如 "p3" 的类名，与 theme.css 中的定义对应。</returns>
    public static string ProtocolBarClass(string? protocolId)
    {
        string p = protocolId ?? string.Empty;
        if (p.Length == 0)
            return "p0";

        return "p" + (StableHash(p) % PaletteSlots).ToString(
            System.Globalization.CultureInfo.InvariantCulture);
    }

    /// <summary>
    /// 计算字符串的 FNV-1a 32 位哈希。
    /// </summary>
    /// <remarks>
    /// <b>不能用 string.GetHashCode()。</b>.NET Core 起它是按进程随机化的，
    /// 同一个协议在每次重启后会换一个颜色——操作员靠颜色记设备，这会让人以为改了配置。
    /// FNV-1a 简单、无外部依赖、跨进程与跨平台完全确定。
    /// </remarks>
    /// <param name="value">待哈希的字符串，按不区分大小写处理。</param>
    private static uint StableHash(string value)
    {
        uint hash = FnvOffsetBasis;

        foreach (char c in value)
        {
            // 统一转小写再入哈希：ProtocolId 的大小写在不同来源可能不一致，
            // 但那显然应当算同一个协议
            char lower = char.ToLowerInvariant(c);

            // FNV-1a：先异或再乘，逐字节处理（char 取低八位足以区分 ASCII 协议名）
            hash ^= (byte)lower;
            hash *= FnvPrime;
        }

        return hash;
    }
}
