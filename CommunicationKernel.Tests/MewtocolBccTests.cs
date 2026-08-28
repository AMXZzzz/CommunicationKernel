// -----------------------------------------------------------------------------
// 文件: MewtocolBccTests.cs
// 层级: 测试
// 作用: 锁住 MEWTOCOL 帧的 BCC 计算范围。
//
// 背景（真机上暴露的故障）：
//   接上真实 Panasonic PLC 后，任何读写都返回
//     Route 'B': IO failed: ProtocolError MEWTOCOL error 40: BCC error
//   原因是 BCC 的计算范围漏掉了帧头 '%'，只对 SS + '#' + 命令体求 XOR。
//   规范要求的是「从帧头 % 起、到 BCC 之前为止全部 ASCII 字符的 XOR」。
//
//   后果不是偶发而是必然：每一帧的校验和都恰好差一个 0x25（'%' 的 ASCII），
//   PLC 一律拒收。
//
//   为什么之前没被发现：自测里我们既是发送方也是校验方，两边同样漏掉 '%'，
//   自己发自己收永远对得上。只有接真设备才暴露——因此这里必须钉死
//   <b>具体的字节</b>，而不是"自己算一遍再比一遍"。
// -----------------------------------------------------------------------------

using System.Text;
using CommunicationKernel.Core.Abstractions.Results;
using CommunicationKernel.Core.Protocol.Abstractions;
using CommunicationKernel.Plugins.Protocol.Panasonic;

namespace CommunicationKernel.Tests;

[TestClass]
public class MewtocolBccTests {

    /// <summary>按指定站号造一个 MEWTOCOL 驱动。</summary>
    private static IProtocolDriver Driver(string station) =>
        new MewtocolProtocolDriverFactory().CreateDriver(
            new ProtocolDriverContext { Station = station });

    /// <summary>取出帧的 ASCII 文本形式，便于逐字符断言。</summary>
    private static string TextOf(OperationResult<byte[]> frame) {
        Assert.IsTrue(frame.Success, frame.ErrorMessage);
        return Encoding.ASCII.GetString(frame.Value);
    }

    // =========================================================================
    // 整帧字节
    // =========================================================================

    [TestMethod]
    public void ReadFrame_HasExactExpectedBytes() {
        // DT100 读 4 字节 = 2 个字 → 范围 D00100..D00101
        //
        // 待校验部分：%01#RDD0010000101
        //   '%'=25 '0'=30 '1'=31 '#'=23 'R'=52 'D'=44 'D'=44
        //   '0'=30 '0'=30 '1'=31 '0'=30 '0'=30
        //   '0'=30 '0'=30 '1'=31 '0'=30 '1'=31
        // 逐个异或得 0x54 → "54"
        //
        // 漏掉 '%' 的旧实现会得到 0x71（0x54 ^ 0x25），正是 PLC 报 error 40 的原因。
        string text = TextOf(Driver("1").BuildReadFrame("DT100", 4));

        Assert.AreEqual("%01#RDD001000010154\r", text,
            "整帧必须逐字节一致；BCC 为 54，漏掉帧头 '%' 会算成 71");
    }

    [TestMethod]
    public void ReadFrame_SingleWord_HasExactExpectedBytes() {
        // DT100 读 2 字节 = 1 个字 → 范围 D00100..D00100，BCC = 55
        string text = TextOf(Driver("1").BuildReadFrame("DT100", 2));

        Assert.AreEqual("%01#RDD001000010055\r", text);
    }

    // =========================================================================
    // 计算范围
    // =========================================================================

    [TestMethod]
    public void Bcc_IncludesLeadingPercent() {
        // 直接验证「含 '%'」这条规则本身：
        // 对帧里除 BCC 与 CR 之外的全部字符求 XOR，结果必须等于帧里带的 BCC。
        string text = TextOf(Driver("1").BuildReadFrame("DT100", 4));

        // 去掉结尾 CR 与其前面两位 BCC，剩下的就是待校验部分
        string body = text[..^3];
        string bccInFrame = text[^3..^1];

        byte x = 0;
        foreach (char c in body) x ^= (byte)c;

        Assert.StartsWith("%", body, "待校验部分必须从帧头 '%' 开始");
        Assert.AreEqual(x.ToString("X2"), bccInFrame,
            "BCC 必须等于含 '%' 在内的全部字符的 XOR");
    }

    [TestMethod]
    public void Bcc_ExcludingPercent_WouldDifferByPercentItself() {
        // 反向确认诊断：两种算法的差值恒为 '%' 的 ASCII（0x25）。
        // 这条断言的意义在于——将来若 BCC 又对不上，可以先看差值是不是 0x25，
        // 是的话就还是这个老问题。
        string text = TextOf(Driver("1").BuildReadFrame("DT100", 4));

        string withPercent = text[..^3];
        string withoutPercent = withPercent[1..];

        byte a = 0; foreach (char c in withPercent) a ^= (byte)c;
        byte b = 0; foreach (char c in withoutPercent) b ^= (byte)c;

        Assert.AreEqual(0x25, a ^ b, "两种算法的差值应恰为 '%' 的 ASCII 码");
    }

    // =========================================================================
    // 站号参与校验
    // =========================================================================

    [TestMethod]
    public void Bcc_ChangesWithStation() {
        // 站号在待校验范围内，改站号必然改 BCC。
        // 若哪天 BCC 变得与站号无关，说明计算范围又被改窄了。
        string s1 = TextOf(Driver("1").BuildReadFrame("DT100", 4));
        string s2 = TextOf(Driver("2").BuildReadFrame("DT100", 4));

        StringAssert.StartsWith(s1, "%01#");
        StringAssert.StartsWith(s2, "%02#");
        Assert.AreNotEqual(s1[^3..^1], s2[^3..^1], "不同站号应算出不同的 BCC");
    }

    [TestMethod]
    public void Bcc_IsUppercaseTwoHexDigits() {
        // 小写十六进制会被 PLC 判为校验错——ASCII 'a' 与 'A' 不是一回事
        string text = TextOf(Driver("1").BuildReadFrame("DT100", 4));
        string bcc = text[^3..^1];

        Assert.HasCount(2, bcc);
        foreach (char c in bcc)
            Assert.IsTrue((c >= '0' && c <= '9') || (c >= 'A' && c <= 'F'),
                "BCC 必须是两位大写十六进制，实际得到 " + bcc);
    }
}
