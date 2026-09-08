// -----------------------------------------------------------------------------
// 文件: ByteOrderRoundTripTests.cs
// 层级: 测试
// 作用: 钉住字节序在「存盘字符串 ↔ 枚举」之间往返不失真。
//
// 为什么值得单独锁：
//   字节序错了<b>不会报任何错</b>，只会让 Int32 / Float 显示成一个数量级
//   完全不对的数，写入时同样如此。整条链路上有三处会把它写坏，
//   而每一处都不会抛异常：
//
//     1. 界面下拉存的是「CDAB — 字交换」这样的显示文本。若把整串写进
//        devices.json，ParseOrder 解析失败并<b>静默回落 ABCD</b>——
//        表现是"选了 CDAB 却没生效"，操作员会以为是设备问题。
//     2. 配置文件被手工编辑成小写或写错，同样静默回落。
//     3. DeviceRecord → DeviceInfo 的多个映射入口漏抄这个字段，
//        表现是"改了能保存、重开又变回 ABCD"。
//
//   回落到 ABCD 本身是正确取舍（总比整台设备读不出来强），但正因为它安静，
//   前两条才必须由测试来兜。
//
// WPF 层无法被本测试项目引用（net8.0 vs net8.0-windows），
// 因此这里锁的是 SDK 侧的解析契约，界面那一半靠手工冒烟。
// -----------------------------------------------------------------------------

using CommunicationKernel.Hosting.Sdk;

namespace CommunicationKernel.Tests;

[TestClass]
public class ByteOrderRoundTripTests {

    // =========================================================================
    // 四个合法取值必须原样往返
    // =========================================================================

    [TestMethod]
    [DataRow("ABCD", ByteOrder.ABCD)]
    [DataRow("CDAB", ByteOrder.CDAB)]
    [DataRow("BADC", ByteOrder.BADC)]
    [DataRow("DCBA", ByteOrder.DCBA)]
    public void AllFourCodes_RoundTrip (string code, ByteOrder expected) {
        Assert.AreEqual(expected, ValueCodec.ParseOrder(code));
        Assert.AreEqual(code, expected.ToString(),
            "枚举名必须与存盘字符串逐字一致，否则存进去的值再也读不回来");
    }

    // =========================================================================
    // 大小写与空白：手工编辑配置文件时的常见形态
    // =========================================================================

    [TestMethod]
    [DataRow("cdab")]
    [DataRow("CdAb")]
    public void CaseInsensitive (string code) {
        Assert.AreEqual(ByteOrder.CDAB, ValueCodec.ParseOrder(code),
            "手工编辑 devices.json 时大小写不该改变行为");
    }

    // =========================================================================
    // 非法输入一律回落大端，绝不抛异常
    // =========================================================================

    [TestMethod]
    [DataRow(null)]
    [DataRow("")]
    [DataRow("   ")]
    [DataRow("ABDC")]                 // 手滑打错的近似串
    [DataRow("CDAB — 字交换")]        // ★ 把界面显示文本整串存了进去
    [DataRow("big-endian")]
    public void InvalidInput_FallsBackToAbcd (string? code) {
        // 回落而不是抛异常：配置写坏时整台设备读不出来，比读到大端值更糟。
        // 但这条"安静"正是上面那些坑能潜伏的原因，所以界面侧必须只存四字母代码。
        Assert.AreEqual(ByteOrder.ABCD, ValueCodec.ParseOrder(code!));
    }

    // =========================================================================
    // 字节序确实改变解释结果——否则前面几条测的只是字符串游戏
    // =========================================================================

    [TestMethod]
    public void OrderActuallyChangesDecodedValue () {
        // 0x0000_0001，大端解释为 1
        byte[] data = { 0x00, 0x00, 0x00, 0x01 };

        string abcd = ValueCodec.Decode(data, "Int32", ByteOrder.ABCD);
        string cdab = ValueCodec.Decode(data, "Int32", ByteOrder.CDAB);

        Assert.AreEqual("1", abcd);
        Assert.AreNotEqual(abcd, cdab,
            "字交换必须给出不同结果；若相同说明 ByteOrder 根本没被采用——" +
            "那正是 WPF 侧此前的状态：参数一路默认到底，配错也看不出来");
    }

    [TestMethod]
    public void Float_OrderMatters () {
        // 1.0f 的大端字节
        byte[] data = { 0x3F, 0x80, 0x00, 0x00 };

        Assert.AreNotEqual(
            ValueCodec.Decode(data, "Float", ByteOrder.ABCD),
            ValueCodec.Decode(data, "Float", ByteOrder.CDAB),
            "浮点是现场最容易踩字节序的类型：变频器的频率、温度多是 Float");
    }
}
