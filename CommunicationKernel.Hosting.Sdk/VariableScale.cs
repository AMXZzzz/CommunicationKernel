// -----------------------------------------------------------------------------
// 文件: Services/Shared/VariableScale.cs
// 层级: UI 层 — 变量的小数位换算
// 作用: PLC 里存整数，界面上按工程量显示与输入。
//
// 为什么需要：
//   PLC 的寄存器多半只有整数。要表达「200.1 mm」这类带小数的量，
//   现场惯例是把小数点<b>约定</b>掉——寄存器里存 2001，双方都知道是一位小数。
//   这个约定只存在于图纸和人的脑子里，设备本身不带任何标记。
//
//   把它落到变量定义上（Decimals），换算就集中在一处：
//   读回 504 → 显示 50.4；输入 50.4 → 写下 504。
//   不集中的话，页面手动读、后台轮询、MES 卡片、报警判断……每处都要自己乘除，
//   漏掉一处就会出现「同一个点位在两个页面上差十倍」，而且两边都不报错。
//
// 只对整数类型生效：Float / Double 自带小数，再乘 10 就成了另一个数；
// Bool / String / Hex 更没有「小数位」可言。
// -----------------------------------------------------------------------------

using System.Globalization;

// 放在 Sdk 而不是某个界面工程里：它和 ValueCodec 是同一类东西——
// 纯粹的取值换算，不碰界面也不碰通讯。Web 与 WPF 两个上位机都要用，
// 各自实现一份必然在四舍五入或负数上分叉。
namespace CommunicationKernel.Hosting.Sdk;

/// <summary>变量小数位的换算与格式化。</summary>
public static class VariableScale
{
    /// <summary>
    /// 允许的最大小数位。
    /// </summary>
    /// <remarks>
    /// 四位已经覆盖现场绝大多数场景（0.1 mm 到 0.0001 的比例系数）。
    /// 再多会让 Int16 的量程只剩 ±3.2——填得进去，一到现场就溢出。
    /// </remarks>
    public const int MaxDecimals = 4;

    /// <summary>该数据类型是否支持小数位换算。</summary>
    /// <param name="dataType">数据类型名。</param>
    /// <remarks>
    /// 只有整数类型。Float / Double 本身就带小数，再乘 10^n 得到的是另一个数；
    /// 允许它们配小数位，只会让人以为「Float 配了 1 位就会显示一位小数」——
    /// 那是格式化，不是换算，两回事。
    /// </remarks>
    public static bool Supports(string dataType) =>
        dataType is "Int16" or "UInt16" or "Int32" or "UInt32" or "Int64" or "UInt64";

    /// <summary>这条变量当前是否真的在做换算。</summary>
    /// <param name="dataType">数据类型名。</param>
    /// <param name="decimals">配置的小数位。</param>
    public static bool IsActive(string dataType, int decimals) =>
        decimals > 0 && Supports(dataType);

    /// <summary>把小数位换算成倍率，例如 2 → 100。</summary>
    /// <param name="decimals">小数位，超出范围会被钳住。</param>
    public static decimal Factor(int decimals) =>
        (decimal)Math.Pow(10, Math.Clamp(decimals, 0, MaxDecimals));

    /// <summary>
    /// 把设备里的原始整数换算成显示文本。
    /// </summary>
    /// <param name="raw">已解码但未换算的文本，通常是一串整数。</param>
    /// <param name="dataType">数据类型名。</param>
    /// <param name="decimals">小数位。</param>
    /// <returns>换算后的显示文本；不换算或无法解析时原样返回。</returns>
    /// <remarks>
    /// 解析失败时<b>原样返回</b>而不是报错：那多半是设备返回了错误码文本，
    /// 或者类型填错导致解码回落成十六进制串。这两种情况下把原文摆出来，
    /// 比换成一个 "0" 或 "换算失败" 更有助于判断。
    /// <para>
    /// 用 <see cref="decimal"/> 而不是 double：504 / 10 在 double 下是
    /// 50.399999999999999，格式化时看不出来，但参与后续比较就会出错。
    /// </para>
    /// </remarks>
    public static string Display(string raw, string dataType, int decimals)
    {
        if (!IsActive(dataType, decimals)) return raw;
        if (string.IsNullOrWhiteSpace(raw)) return raw;

        if (!decimal.TryParse(raw, NumberStyles.Integer, CultureInfo.InvariantCulture, out decimal v))
            return raw;

        int d = Math.Clamp(decimals, 0, MaxDecimals);
        return (v / Factor(d)).ToString("F" + d, CultureInfo.InvariantCulture);
    }

    /// <summary>
    /// 把界面输入的工程量换算回设备里的原始整数。
    /// </summary>
    /// <param name="text">操作员输入的文本，例如 <c>50.4</c>。</param>
    /// <param name="dataType">数据类型名。</param>
    /// <param name="decimals">小数位。</param>
    /// <param name="raw">换算结果，例如 <c>504</c>。</param>
    /// <param name="error">失败原因；成功时为空串。</param>
    /// <returns>是否换算成功。</returns>
    /// <remarks>
    /// 位数超出配置时<b>四舍五入</b>，不报错：小数位就是这个点位的分辨率，
    /// 设备本来就存不下更细的值。为多打的一位把整次写入拦下来，
    /// 只会让人反复重填，而结果是一样的。
    /// <para>
    /// 写完之后表格里那个读回值会显示实际生效的数，四舍五入的事实在那里可见。
    /// </para>
    /// </remarks>
    public static bool TryToRaw(
        string text, string dataType, int decimals, out string raw, out string error)
    {
        raw = text;
        error = string.Empty;

        if (!IsActive(dataType, decimals)) return true;

        if (string.IsNullOrWhiteSpace(text))
        {
            error = "请输入数值";
            return false;
        }

        if (!decimal.TryParse(
                text.Trim(), NumberStyles.Float, CultureInfo.InvariantCulture, out decimal v))
        {
            error = "「" + text.Trim() + "」不是数值。该点位按 " + decimals + " 位小数换算，请填例如 50.4 这样的值。";
            return false;
        }

        int d = Math.Clamp(decimals, 0, MaxDecimals);
        decimal scaled = Math.Round(v * Factor(d), 0, MidpointRounding.AwayFromZero);

        raw = scaled.ToString(CultureInfo.InvariantCulture);
        return true;
    }

    /// <summary>供界面显示的换算说明，例如「1 位小数 · 设备里存 ×10」。</summary>
    /// <param name="dataType">数据类型名。</param>
    /// <param name="decimals">小数位。</param>
    /// <returns>说明文本；不换算时为空串。</returns>
    public static string Hint(string dataType, int decimals)
    {
        if (!IsActive(dataType, decimals)) return string.Empty;

        int d = Math.Clamp(decimals, 0, MaxDecimals);
        return d + " 位小数 · 设备里存 ×" + Factor(d).ToString("0", CultureInfo.InvariantCulture);
    }
}
