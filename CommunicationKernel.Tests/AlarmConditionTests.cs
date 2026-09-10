// -----------------------------------------------------------------------------
// 文件: AlarmConditionTests.cs
// 层级: 测试
// 作用: 锁住报警条件的判定。
//
// 为什么值得测:
//   判反了不会有任何提示——设备好好的却一直报警，或者真出事了一声不吭。
//   两种都要等到现场出问题才暴露，而那时已经晚了。
//
//   最要紧的是<b>三态</b>：判不了必须是 null，绝不能退化成 false。
//   退化成 false 的话，一条因为读不到值而失效的规则，在界面上会和
//   「一切正常」长得一模一样。
// -----------------------------------------------------------------------------

using CommunicationKernel.Hosting.Sdk;

namespace CommunicationKernel.Tests;

[TestClass]
public class AlarmConditionTests
{
    // =========================================================================
    // 位判断
    // =========================================================================

    /// <summary>报警字的第 N 位。0x0005 = 二进制 101，第 0、2 位置起。</summary>
    [TestMethod]
    public void BitSet_ReadsTheRightBit()
    {
        Assert.IsTrue(AlarmOpInfo.Evaluate(AlarmOp.BitSet, "0", "5"));
        Assert.IsFalse(AlarmOpInfo.Evaluate(AlarmOp.BitSet, "1", "5"));
        Assert.IsTrue(AlarmOpInfo.Evaluate(AlarmOp.BitSet, "2", "5"));
        Assert.IsFalse(AlarmOpInfo.Evaluate(AlarmOp.BitSet, "3", "5"));
    }

    /// <summary>BitClear 是 BitSet 的反面，用于「就绪信号消失」这类反逻辑。</summary>
    [TestMethod]
    public void BitClear_IsTheInverse()
    {
        Assert.IsFalse(AlarmOpInfo.Evaluate(AlarmOp.BitClear, "0", "5"));
        Assert.IsTrue(AlarmOpInfo.Evaluate(AlarmOp.BitClear, "1", "5"));
    }

    /// <summary>高位也要能取到——16 位报警字的第 15 位是常用的「严重故障」。</summary>
    [TestMethod]
    public void BitSet_HighBit()
    {
        Assert.IsTrue(AlarmOpInfo.Evaluate(AlarmOp.BitSet, "15", "32768"));
        Assert.IsFalse(AlarmOpInfo.Evaluate(AlarmOp.BitSet, "14", "32768"));
    }

    /// <summary>
    /// 值带小数点时判不了位。
    /// </summary>
    /// <remarks>
    /// 该点位配了小数位，显示值成了 "50.4"，取它的第几位毫无意义。
    /// 这种情况必须交回 null——当成 false 的话，界面上会显示成「这条没触发」，
    /// 而真相是这条规则从来就没能判过。
    /// </remarks>
    [TestMethod]
    public void Bit_OnScaledValue_Unknown()
    {
        Assert.IsNull(AlarmOpInfo.Evaluate(AlarmOp.BitSet, "0", "50.4"));
    }

    /// <summary>位号填错（非数字、超范围）时判不了。</summary>
    [TestMethod]
    public void Bit_BadOperand_Unknown()
    {
        Assert.IsNull(AlarmOpInfo.Evaluate(AlarmOp.BitSet, "abc", "5"));
        Assert.IsNull(AlarmOpInfo.Evaluate(AlarmOp.BitSet, "-1", "5"));
        Assert.IsNull(AlarmOpInfo.Evaluate(AlarmOp.BitSet, "64", "5"));
        Assert.IsNull(AlarmOpInfo.Evaluate(AlarmOp.BitSet, "", "5"));
    }

    // =========================================================================
    // 通断
    // =========================================================================

    /// <summary>ValueCodec 把 Bool 解码成 ON / OFF。</summary>
    [TestMethod]
    public void IsOn_AcceptsCodecOutput()
    {
        Assert.IsTrue(AlarmOpInfo.Evaluate(AlarmOp.IsOn, "", "ON"));
        Assert.IsFalse(AlarmOpInfo.Evaluate(AlarmOp.IsOn, "", "OFF"));
        Assert.IsFalse(AlarmOpInfo.Evaluate(AlarmOp.IsOff, "", "ON"));
        Assert.IsTrue(AlarmOpInfo.Evaluate(AlarmOp.IsOff, "", "OFF"));
    }

    /// <summary>手改过的配置或别的类型也可能给出 1/0 与 true/false，都认。</summary>
    [TestMethod]
    public void IsOn_AcceptsNumericAndBooleanText()
    {
        Assert.IsTrue(AlarmOpInfo.Evaluate(AlarmOp.IsOn, "", "1"));
        Assert.IsFalse(AlarmOpInfo.Evaluate(AlarmOp.IsOn, "", "0"));
        Assert.IsTrue(AlarmOpInfo.Evaluate(AlarmOp.IsOn, "", "true"));
        Assert.IsTrue(AlarmOpInfo.Evaluate(AlarmOp.IsOn, "", "2"));
    }

    /// <summary>
    /// 认不出的值判不了，绝不能当成 OFF。
    /// </summary>
    /// <remarks>
    /// 当成 OFF 的话，一条 IsOff 的报警会凭空成立——设备明明没事，
    /// 现场却收到一条「断开」报警，而根本查不出为什么。
    /// </remarks>
    [TestMethod]
    public void IsOff_OnGarbage_Unknown()
    {
        Assert.IsNull(AlarmOpInfo.Evaluate(AlarmOp.IsOff, "", "TIMEOUT"));
        Assert.IsNull(AlarmOpInfo.Evaluate(AlarmOp.IsOn, "", "00 1F"));
    }

    // =========================================================================
    // 值比较
    // =========================================================================

    /// <summary>大小比较按数值，不按字典序。</summary>
    /// <remarks>按字典序的话 "9" &gt; "10" 会成立，气压报警就全乱了。</remarks>
    [TestMethod]
    public void LessGreater_CompareNumerically()
    {
        Assert.IsTrue(AlarmOpInfo.Evaluate(AlarmOp.Less, "0.45", "0.32"));
        Assert.IsFalse(AlarmOpInfo.Evaluate(AlarmOp.Less, "0.45", "0.52"));
        Assert.IsTrue(AlarmOpInfo.Evaluate(AlarmOp.Greater, "9", "10"));
        Assert.IsFalse(AlarmOpInfo.Evaluate(AlarmOp.Greater, "10", "9"));
    }

    /// <summary>负数照常——温度、补偿量都会是负的。</summary>
    [TestMethod]
    public void Less_Negative()
    {
        Assert.IsTrue(AlarmOpInfo.Evaluate(AlarmOp.Less, "-5", "-10"));
        Assert.IsFalse(AlarmOpInfo.Evaluate(AlarmOp.Less, "-10", "-5"));
    }

    /// <summary>
    /// 相等按数值比，位数不同不影响。
    /// </summary>
    /// <remarks>
    /// "50.0" 与 "50" 是同一个读数，差别只来自小数位怎么配。
    /// 纯文本比较会把它们判成不等，而那种不等查起来毫无头绪。
    /// </remarks>
    [TestMethod]
    public void Equal_IgnoresTrailingZeros()
    {
        Assert.IsTrue(AlarmOpInfo.Evaluate(AlarmOp.Equal, "50", "50.0"));
        Assert.IsTrue(AlarmOpInfo.Evaluate(AlarmOp.Equal, "50.00", "50"));
        Assert.IsFalse(AlarmOpInfo.Evaluate(AlarmOp.NotEqual, "50", "50.0"));
    }

    /// <summary>两边都不是数时按文本比，忽略大小写。</summary>
    [TestMethod]
    public void Equal_FallsBackToText()
    {
        Assert.IsTrue(AlarmOpInfo.Evaluate(AlarmOp.Equal, "on", "ON"));
        Assert.IsFalse(AlarmOpInfo.Evaluate(AlarmOp.Equal, "ON", "OFF"));
    }

    /// <summary>对比值填的不是数时，大小比较判不了。</summary>
    [TestMethod]
    public void Less_BadOperand_Unknown()
    {
        Assert.IsNull(AlarmOpInfo.Evaluate(AlarmOp.Less, "abc", "5"));
        Assert.IsNull(AlarmOpInfo.Evaluate(AlarmOp.Less, "5", "abc"));
    }

    // =========================================================================
    // 读不到值
    // =========================================================================

    /// <summary>
    /// 没有读值时一律判不了。
    /// </summary>
    /// <remarks>
    /// "--" 是尚未读取，空串是没有这条变量，错误码是读失败。
    /// 三种都不是数据。这一条是整个文件里最要紧的：
    /// 把它们当成 false，等于让一批失效的规则在界面上装成「一切正常」。
    /// </remarks>
    [TestMethod]
    public void NoReading_AlwaysUnknown()
    {
        foreach (AlarmOp op in AlarmOpInfo.All)
        {
            Assert.IsNull(AlarmOpInfo.Evaluate(op, "0", ""), op + " 空值应判不了");
            Assert.IsNull(AlarmOpInfo.Evaluate(op, "0", "   "), op + " 空白应判不了");
            Assert.IsNull(AlarmOpInfo.Evaluate(op, "0", "--"), op + " 未读取应判不了");
        }
    }

    // =========================================================================
    // 适用范围
    // =========================================================================

    /// <summary>线圈只给通断，整数才有位比较，浮点只能比大小。</summary>
    [TestMethod]
    public void For_MatchesDataType()
    {
        CollectionAssert.AreEqual(
            new[] { AlarmOp.IsOn, AlarmOp.IsOff }, AlarmOpInfo.For("Bool"));

        Assert.IsTrue(AlarmOpInfo.Applies(AlarmOp.BitSet, "Int16"));
        Assert.IsFalse(AlarmOpInfo.Applies(AlarmOp.BitSet, "Float"));
        Assert.IsFalse(AlarmOpInfo.Applies(AlarmOp.BitSet, "Bool"));
        Assert.IsFalse(AlarmOpInfo.Applies(AlarmOp.Less, "String"));
        Assert.IsTrue(AlarmOpInfo.Applies(AlarmOp.Equal, "Hex"));
    }

    /// <summary>通断不需要操作数，其余都要。</summary>
    [TestMethod]
    public void NeedsOperand_OnlyForComparisons()
    {
        Assert.IsFalse(AlarmOpInfo.NeedsOperand(AlarmOp.IsOn));
        Assert.IsFalse(AlarmOpInfo.NeedsOperand(AlarmOp.IsOff));
        Assert.IsTrue(AlarmOpInfo.NeedsOperand(AlarmOp.BitSet));
        Assert.IsTrue(AlarmOpInfo.NeedsOperand(AlarmOp.Less));
    }

    /// <summary>条件文本要能读懂，它会出现在日志与只读展示里。</summary>
    [TestMethod]
    public void Text_IsReadable()
    {
        Assert.AreEqual("bit 3 = 1", AlarmOpInfo.Text(AlarmOp.BitSet, "3"));
        Assert.AreEqual("< 0.45", AlarmOpInfo.Text(AlarmOp.Less, "0.45"));
        Assert.AreEqual("= ON", AlarmOpInfo.Text(AlarmOp.IsOn, ""));
    }
}
