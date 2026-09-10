// -----------------------------------------------------------------------------
// 文件: VariableScale.cs
// 层级: Hosting.Sdk — 变量的小数位
// 作用: 统一「这个值显示几位小数」，并在整数点位上顺带完成 ×10^n 的换算。
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
// 对操作员来说只有一个概念：<b>这一列显示几位小数</b>。
// 底下分两种情形：
//   整数类型（Int16/UInt16/...）——除了显示，还要真的 ÷ 10^n 与 × 10^n；
//   浮点类型（Float/Double）——值本身已经带小数，只是定几位显示。
// 两者共用一个字段，是因为现场看到的就是同一件事；把它们拆成两个设置项，
// 反而要先判断自己这条是什么类型才知道该填哪个。
//
// 放在 Sdk 而不是某个界面工程里：它和 ValueCodec 是同一类东西——
// 纯粹的取值换算，不碰界面也不碰通讯。Web 与 WPF 两个上位机都要用，
// 各自实现一份必然在四舍五入或负数上分叉。
// -----------------------------------------------------------------------------

using System.Globalization;

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

    /// <summary>
    /// 该数据类型能不能配小数位。
    /// </summary>
    /// <param name="dataType">数据类型名。</param>
    /// <remarks>
    /// 数值类型都可以，含浮点——对浮点它是「显示几位」，对整数它还额外意味着
    /// ×10^n 的换算约定（见 <see cref="Scales"/>）。
    /// Bool / String / Hex 没有小数可言，不提供这一项。
    /// </remarks>
    public static bool Supports(string dataType) =>
        Scales(dataType) || dataType is "Float" or "Double";

    /// <summary>
    /// 该数据类型是否需要 ×10^n 的换算。
    /// </summary>
    /// <param name="dataType">数据类型名。</param>
    /// <remarks>
    /// 只有整数类型。浮点值本身就带小数，再乘 10^n 得到的是另一个数——
    /// 对它们，小数位只影响显示，读写的数值原样透传。
    /// </remarks>
    public static bool Scales(string dataType) =>
        dataType is "Int16" or "UInt16" or "Int32" or "UInt32" or "Int64" or "UInt64";

    /// <summary>这条变量当前是否真的在按小数位处理。</summary>
    /// <param name="dataType">数据类型名。</param>
    /// <param name="decimals">配置的小数位。</param>
    public static bool IsActive(string dataType, int decimals) =>
        decimals > 0 && Supports(dataType);

    /// <summary>把小数位换算成倍率，例如 2 → 100。</summary>
    /// <param name="decimals">小数位，超出范围会被钳住。</param>
    public static decimal Factor(int decimals) =>
        (decimal)Math.Pow(10, Math.Clamp(decimals, 0, MaxDecimals));

    /// <summary>
    /// 把解码结果整理成显示文本。
    /// </summary>
    /// <param name="raw">已解码但未处理小数位的文本。</param>
    /// <param name="dataType">数据类型名。</param>
    /// <param name="decimals">小数位。</param>
    /// <returns>处理后的显示文本；不生效或无法解析时原样返回。</returns>
    /// <remarks>
    /// 整数按 10^n 缩小再补位，浮点只按位数格式化。
    /// <para>
    /// 解析失败时<b>原样返回</b>而不是报错：那多半是设备返回了错误码文本，
    /// 或者类型填错导致解码回落成十六进制串。这两种情况下把原文摆出来，
    /// 比换成一个 "0" 或「换算失败」更有助于判断。
    /// </para>
    /// <para>
    /// 用 <see cref="decimal"/> 而不是 double：504 / 10 在 double 下是
    /// 50.399999999999999，格式化时看不出来，但参与后续比较就会出错。
    /// </para>
    /// </remarks>
    public static string Display(string raw, string dataType, int decimals)
    {
        if (!IsActive(dataType, decimals)) return raw;
        if (string.IsNullOrWhiteSpace(raw)) return raw;

        int d = Math.Clamp(decimals, 0, MaxDecimals);

        // 浮点：值本身已经是工程量，只定显示位数。
        // 解析用 Float 而不是 Integer——ValueCodec 对 Float 输出的是 "34.97" 这种带小数点的文本
        if (!Scales(dataType))
        {
            return decimal.TryParse(raw, NumberStyles.Float, CultureInfo.InvariantCulture, out decimal f)
                ? f.ToString("F" + d, CultureInfo.InvariantCulture)
                : raw;
        }

        if (!decimal.TryParse(raw, NumberStyles.Integer, CultureInfo.InvariantCulture, out decimal v))
            return raw;

        return (v / Factor(d)).ToString("F" + d, CultureInfo.InvariantCulture);
    }

    /// <summary>
    /// 把界面输入的工程量换算回要写入设备的文本。
    /// </summary>
    /// <param name="text">操作员输入的文本，例如 <c>50.4</c>。</param>
    /// <param name="dataType">数据类型名。</param>
    /// <param name="decimals">小数位。</param>
    /// <param name="raw">换算结果，例如 <c>504</c>；浮点与不生效时原样返回。</param>
    /// <param name="error">失败原因；成功时为空串。</param>
    /// <returns>是否换算成功。</returns>
    /// <remarks>
    /// 浮点<b>不换算</b>：小数位对它们只是显示位数，写入时把 50.44 截成 50.4
    /// 等于凭空丢掉设备本来存得下的精度。
    /// <para>
    /// 整数上位数超出配置时四舍五入，不报错：小数位就是这个点位的分辨率，
    /// 设备本来就存不下更细的值。为多打的一位把整次写入拦下来，
    /// 只会让人反复重填，而结果是一样的。写完之后表格里那个读回值
    /// 会显示实际生效的数，四舍五入的事实在那里可见。
    /// </para>
    /// </remarks>
    public static bool TryToRaw(
        string text, string dataType, int decimals, out string raw, out string error)
    {
        raw = text;
        error = string.Empty;

        if (!IsActive(dataType, decimals) || !Scales(dataType)) return true;

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

    /// <summary>供界面显示的说明文本。</summary>
    /// <param name="dataType">数据类型名。</param>
    /// <param name="decimals">小数位。</param>
    /// <returns>说明文本；不生效时为空串。</returns>
    /// <remarks>
    /// 整数与浮点的说法必须不同：前者是「设备里存的数不是你看到的数」，
    /// 后者只是「保留几位」。用同一句话会让人以为浮点点位也被乘过。
    /// </remarks>
    public static string Hint(string dataType, int decimals)
    {
        if (!IsActive(dataType, decimals)) return string.Empty;

        int d = Math.Clamp(decimals, 0, MaxDecimals);

        return Scales(dataType)
            ? d + " 位小数 · 设备里存 ×" + Factor(d).ToString("0", CultureInfo.InvariantCulture)
            : d + " 位小数 · 仅显示位数，不换算";
    }
}
