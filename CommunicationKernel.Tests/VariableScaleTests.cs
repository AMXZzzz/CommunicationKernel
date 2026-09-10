// -----------------------------------------------------------------------------
// 文件: VariableScaleTests.cs
// 层级: 测试
// 作用: 锁住小数位换算的双向一致性。
//
// 为什么值得测:
//   这段换算横跨读写两侧：读回 504 要显示 50.4，输入 50.4 要写下 504。
//   任一侧算错，现场看到的位置就和设备里的差十倍——而两侧都会报成功，
//   没有任何错误提示。这类缺陷只能靠测试拦住。
//
//   另外几处边角都是真会踩到的：负数（反向补偿量）、四舍五入的方向、
//   Float 不该参与换算、以及设备返回错误码文本时不能被换算成一个数字。
// -----------------------------------------------------------------------------

using CommunicationKernel.Hosting.Sdk;

namespace CommunicationKernel.Tests;

[TestClass]
public class VariableScaleTests
{
    // =========================================================================
    // 适用范围
    // =========================================================================

    /// <summary>整数类型支持换算——PLC 寄存器里只有整数，小数点是约定出来的。</summary>
    [TestMethod]
    public void Supports_IntegerTypes_True()
    {
        foreach (string t in new[] { "Int16", "UInt16", "Int32", "UInt32", "Int64", "UInt64" })
            Assert.IsTrue(VariableScale.Supports(t), t + " 应当支持小数位换算");
    }

    /// <summary>
    /// 浮点与非数值类型不支持。
    /// </summary>
    /// <remarks>
    /// Float 自带小数，再乘 10^n 得到的是另一个数。允许它配小数位，
    /// 只会让人以为那是「保留几位显示」——那是格式化，与换算是两回事。
    /// </remarks>
    [TestMethod]
    public void Supports_FloatAndNonNumeric_False()
    {
        foreach (string t in new[] { "Float", "Double", "Bool", "String", "Hex" })
            Assert.IsFalse(VariableScale.Supports(t), t + " 不应支持小数位换算");
    }

    /// <summary>小数位为 0 时不换算，哪怕类型支持。</summary>
    [TestMethod]
    public void IsActive_ZeroDecimals_False()
    {
        Assert.IsFalse(VariableScale.IsActive("Int16", 0));
        Assert.IsTrue(VariableScale.IsActive("Int16", 1));
    }

    // =========================================================================
    // 读：原始整数 → 显示值
    // =========================================================================

    /// <summary>一位小数：设备里的 504 显示成 50.4。</summary>
    [TestMethod]
    public void Display_OneDecimal_DividesByTen()
    {
        Assert.AreEqual("50.4", VariableScale.Display("504", "Int16", 1));
        Assert.AreEqual("200.1", VariableScale.Display("2001", "Int16", 1));
    }

    /// <summary>
    /// 位数补齐：2000 配一位小数显示成 200.0，而不是 200。
    /// </summary>
    /// <remarks>
    /// 补零是必要的：一列数里 200 和 200.0 混着出现，扫视时对不齐，
    /// 也看不出这一列的分辨率是多少。
    /// </remarks>
    [TestMethod]
    public void Display_PadsToConfiguredDecimals()
    {
        Assert.AreEqual("200.0", VariableScale.Display("2000", "Int16", 1));
        Assert.AreEqual("5.00", VariableScale.Display("500", "Int16", 2));
        Assert.AreEqual("0.0250", VariableScale.Display("250", "Int32", 4));
    }

    /// <summary>负数照常换算——反向补偿量、温度都会是负的。</summary>
    [TestMethod]
    public void Display_Negative_Works()
    {
        Assert.AreEqual("-50.4", VariableScale.Display("-504", "Int16", 1));
    }

    /// <summary>不换算时原样返回。</summary>
    [TestMethod]
    public void Display_Inactive_ReturnsRaw()
    {
        Assert.AreEqual("504", VariableScale.Display("504", "Int16", 0));
        Assert.AreEqual("504", VariableScale.Display("504", "Float", 1));
    }

    /// <summary>
    /// 解析不了的文本原样返回，不能变成一个数。
    /// </summary>
    /// <remarks>
    /// 读失败时这一列填的是错误码，类型填错时 ValueCodec 会回落成十六进制串。
    /// 这两种情况下把原文摆出来才有助于判断；换成 "0" 或「换算失败」
    /// 会把唯一的线索抹掉。
    /// </remarks>
    [TestMethod]
    public void Display_NonNumeric_ReturnsUnchanged()
    {
        Assert.AreEqual("TIMEOUT", VariableScale.Display("TIMEOUT", "Int16", 1));
        Assert.AreEqual("00 1F 3A", VariableScale.Display("00 1F 3A", "Int16", 1));
        Assert.AreEqual("--", VariableScale.Display("--", "Int16", 1));
    }

    // =========================================================================
    // 写：显示值 → 原始整数
    // =========================================================================

    /// <summary>输入 50.4、配一位小数，写下去的是 504。</summary>
    [TestMethod]
    public void TryToRaw_OneDecimal_MultipliesByTen()
    {
        Assert.IsTrue(VariableScale.TryToRaw("50.4", "Int16", 1, out string raw, out _));
        Assert.AreEqual("504", raw);
    }

    /// <summary>整数输入也要按小数位放大：填 50 写下去是 500，不是 50。</summary>
    [TestMethod]
    public void TryToRaw_IntegerInput_StillScaled()
    {
        Assert.IsTrue(VariableScale.TryToRaw("50", "Int16", 1, out string raw, out _));
        Assert.AreEqual("500", raw);
    }

    /// <summary>负数照常。</summary>
    [TestMethod]
    public void TryToRaw_Negative_Works()
    {
        Assert.IsTrue(VariableScale.TryToRaw("-50.4", "Int16", 1, out string raw, out _));
        Assert.AreEqual("-504", raw);
    }

    /// <summary>
    /// 多填的位数四舍五入，且向远离零的方向舍。
    /// </summary>
    /// <remarks>
    /// 小数位就是这个点位的分辨率，设备本来就存不下更细的值。
    /// 为多打的一位把整次写入拦下来，只会让人反复重填，而结果是一样的。
    /// <para>
    /// 用 AwayFromZero 而不是 .NET 默认的银行家舍入：现场按四舍五入理解，
    /// 而银行家舍入会把 50.45 舍成 50.4，与手算对不上，且很难解释。
    /// </para>
    /// </remarks>
    [TestMethod]
    public void TryToRaw_ExtraDigits_RoundsAwayFromZero()
    {
        Assert.IsTrue(VariableScale.TryToRaw("50.44", "Int16", 1, out string down, out _));
        Assert.AreEqual("504", down);

        Assert.IsTrue(VariableScale.TryToRaw("50.45", "Int16", 1, out string up, out _));
        Assert.AreEqual("505", up);

        Assert.IsTrue(VariableScale.TryToRaw("-50.45", "Int16", 1, out string neg, out _));
        Assert.AreEqual("-505", neg);
    }

    /// <summary>不换算时原样透传，交给 ValueCodec 去校验。</summary>
    [TestMethod]
    public void TryToRaw_Inactive_PassesThrough()
    {
        Assert.IsTrue(VariableScale.TryToRaw("ON", "Bool", 0, out string raw, out _));
        Assert.AreEqual("ON", raw);

        Assert.IsTrue(VariableScale.TryToRaw("1.5", "Float", 2, out string f, out _));
        Assert.AreEqual("1.5", f);
    }

    /// <summary>非数值输入要拦下来，并且错误里要带上填的那个词。</summary>
    [TestMethod]
    public void TryToRaw_NonNumeric_FailsWithInput()
    {
        Assert.IsFalse(VariableScale.TryToRaw("abc", "Int16", 1, out _, out string error));
        StringAssert.Contains(error, "abc");
    }

    /// <summary>空输入要拦下来，而不是当成 0 写进设备。</summary>
    [TestMethod]
    public void TryToRaw_Blank_Fails()
    {
        Assert.IsFalse(VariableScale.TryToRaw("   ", "Int16", 1, out _, out string error));
        Assert.AreNotEqual(string.Empty, error);
    }

    // =========================================================================
    // 往返
    // =========================================================================

    /// <summary>
    /// 读写两侧必须互为逆运算。
    /// </summary>
    /// <remarks>
    /// 这是本文件最要紧的一条：任一侧算错，界面上看到的值和设备里的就差十倍，
    /// 而两侧都会报成功。逐个小数位都走一遍，避免只有 1 位被测到。
    /// </remarks>
    [TestMethod]
    public void RoundTrip_DisplayThenWrite_Stable()
    {
        for (int d = 1; d <= VariableScale.MaxDecimals; d++)
        {
            string raw = "504";
            string shown = VariableScale.Display(raw, "Int32", d);

            Assert.IsTrue(VariableScale.TryToRaw(shown, "Int32", d, out string back, out _));
            Assert.AreEqual(raw, back, d + " 位小数往返后应当回到原值");
        }
    }

    /// <summary>用户输入 → 写下的整数 → 再显示回来，应当等于用户输入。</summary>
    [TestMethod]
    public void RoundTrip_WriteThenDisplay_Stable()
    {
        Assert.IsTrue(VariableScale.TryToRaw("50.4", "Int16", 1, out string raw, out _));
        Assert.AreEqual("50.4", VariableScale.Display(raw, "Int16", 1));
    }

    // =========================================================================
    // 倍率与说明
    // =========================================================================

    /// <summary>倍率就是 10 的小数位次方，并对超范围的输入钳位。</summary>
    /// <remarks>
    /// 钳位是为了防住手改配置文件的情况：Decimals 写成 9 时不应当算出
    /// 一个十亿倍的系数，那会让写入值直接溢出而错误看不出根由。
    /// </remarks>
    [TestMethod]
    public void Factor_ClampsToMaxDecimals()
    {
        Assert.AreEqual(1m, VariableScale.Factor(0));
        Assert.AreEqual(10m, VariableScale.Factor(1));
        Assert.AreEqual(10000m, VariableScale.Factor(VariableScale.MaxDecimals));
        Assert.AreEqual(10000m, VariableScale.Factor(99));
        Assert.AreEqual(1m, VariableScale.Factor(-3));
    }

    /// <summary>说明文本只在真的换算时才有内容。</summary>
    [TestMethod]
    public void Hint_OnlyWhenActive()
    {
        Assert.AreEqual(string.Empty, VariableScale.Hint("Int16", 0));
        Assert.AreEqual(string.Empty, VariableScale.Hint("Float", 2));
        StringAssert.Contains(VariableScale.Hint("Int16", 1), "10");
    }
}
