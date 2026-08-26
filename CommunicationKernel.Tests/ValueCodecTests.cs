// -----------------------------------------------------------------------------
// 文件: ValueCodecTests.cs
// 层级: 测试
// 作用: 锁住变量值的字节序换算。
//
// 背景（真实复现过）：
//   Web 端曾用 BitConverter 直接编解码，在 x86 上即小端。
//   写入 8 到 Modbus 保持寄存器，Mbslave 里显示的是 2048——0x0008 与 0x0800。
//   更隐蔽的是读路径同样按小端解，两个错误互相抵消：
//   自己写完自己读回来还是 8，看起来完全正常，只有拿真设备比对才暴露。
//
//   三个协议插件出来的字节都已经是大端（Modbus 寄存器本就是网络序、
//   S7 原生大端、MEWTOCOL 插件内部已 SwapBytes 转成大端），
//   所以 ABCD 才是正确基准。WPF 端一直是显式大端、恰好正确，
//   两份实现同源不同命——现已统一收敛到 EngineHost.Sdk，两个 UI 共用这一份。
// -----------------------------------------------------------------------------

using System;
using CommunicationKernel.EngineHost.Sdk;

namespace CommunicationKernel.Tests;

[TestClass]
public class ValueCodecTests {

    // =========================================================================
    // 大端基准：与协议插件产出的字节序一致
    // =========================================================================

    [TestMethod]
    public void Decode_Int16_BigEndian_IsDefault() {
        // 0x0008 大端 = 8。若按小端解会得到 2048。
        Assert.AreEqual("8", ValueCodec.Decode(new byte[] { 0x00, 0x08 }, "Int16"));
    }

    [TestMethod]
    public void Encode_Int16_ProducesBigEndianBytes() {
        // 这正是原缺陷：曾产出 08 00，PLC 按大端读成 2048
        Assert.IsTrue(ValueCodec.TryEncode("8", "Int16", 2, out byte[] data, out string err), err);
        CollectionAssert.AreEqual(new byte[] { 0x00, 0x08 }, data,
            "写入必须是大端 00 08；产出 08 00 会让 PLC 收到 2048");
    }

    [TestMethod]
    public void Encode_ThenDecode_RoundTrips_AndMatchesWire() {
        // 往返一致是必要条件，但不充分——原缺陷正是「往返一致但线上是错的」。
        // 所以这里同时断言中间的字节形态。
        Assert.IsTrue(ValueCodec.TryEncode("4660", "UInt16", 2, out byte[] data, out _));
        CollectionAssert.AreEqual(new byte[] { 0x12, 0x34 }, data);
        Assert.AreEqual("4660", ValueCodec.Decode(data, "UInt16"));
    }

    [TestMethod]
    public void Decode_Int32_BigEndian() {
        // 0x12345678
        Assert.AreEqual("305419896",
            ValueCodec.Decode(new byte[] { 0x12, 0x34, 0x56, 0x78 }, "Int32"));
    }

    [TestMethod]
    public void Decode_Float_BigEndian() {
        // 0x42C80000 = 100.0f（IEEE754 大端）
        Assert.AreEqual("100",
            ValueCodec.Decode(new byte[] { 0x42, 0xC8, 0x00, 0x00 }, "Float"));
    }

    // =========================================================================
    // 四种排列
    // =========================================================================

    [TestMethod]
    public void Decode_AllOrders_OfSame32BitValue() {
        // 目标值 0x12345678：四种排列下设备寄存器里的实际字节各不相同，
        // 配对了字节序才能解出同一个数
        const string expected = "305419896";

        Assert.AreEqual(expected,
            ValueCodec.Decode(new byte[] { 0x12, 0x34, 0x56, 0x78 }, "Int32", ByteOrder.ABCD));
        Assert.AreEqual(expected,
            ValueCodec.Decode(new byte[] { 0x56, 0x78, 0x12, 0x34 }, "Int32", ByteOrder.CDAB));
        Assert.AreEqual(expected,
            ValueCodec.Decode(new byte[] { 0x34, 0x12, 0x78, 0x56 }, "Int32", ByteOrder.BADC));
        Assert.AreEqual(expected,
            ValueCodec.Decode(new byte[] { 0x78, 0x56, 0x34, 0x12 }, "Int32", ByteOrder.DCBA));
    }

    [TestMethod]
    public void Encode_AllOrders_ProduceMatchingWireBytes() {
        // 编码方向必须与解码方向严格互逆，否则会出现
        //「界面读着对、写下去错」这类只影响写的单向缺陷
        CollectionAssert.AreEqual(new byte[] { 0x12, 0x34, 0x56, 0x78 }, Encode(ByteOrder.ABCD));
        CollectionAssert.AreEqual(new byte[] { 0x56, 0x78, 0x12, 0x34 }, Encode(ByteOrder.CDAB));
        CollectionAssert.AreEqual(new byte[] { 0x34, 0x12, 0x78, 0x56 }, Encode(ByteOrder.BADC));
        CollectionAssert.AreEqual(new byte[] { 0x78, 0x56, 0x34, 0x12 }, Encode(ByteOrder.DCBA));

        static byte[] Encode(ByteOrder order) {
            Assert.IsTrue(ValueCodec.TryEncode("305419896", "Int32", 4, out byte[] d, out string e, order), e);
            return d;
        }
    }

    [TestMethod]
    public void Int16_OnlyByteSwapMatters() {
        // 16 位值只有两个字节，字交换（CDAB）无从谈起，
        // 效果应与大端相同；字节交换（BADC）才会反过来
        Assert.AreEqual("8", ValueCodec.Decode(new byte[] { 0x00, 0x08 }, "Int16", ByteOrder.ABCD));
        Assert.AreEqual("8", ValueCodec.Decode(new byte[] { 0x00, 0x08 }, "Int16", ByteOrder.CDAB));
        Assert.AreEqual("8", ValueCodec.Decode(new byte[] { 0x08, 0x00 }, "Int16", ByteOrder.BADC));
        Assert.AreEqual("8", ValueCodec.Decode(new byte[] { 0x08, 0x00 }, "Int16", ByteOrder.DCBA));
    }

    // =========================================================================
    // 不参与字节序换算的类型
    // =========================================================================

    [TestMethod]
    public void Bool_And_Hex_AreNotReordered() {
        // Bool 只看首字节；Hex 要的就是原始排列，翻转会让人看不到真实报文
        Assert.AreEqual("ON", ValueCodec.Decode(new byte[] { 0x01 }, "Bool", ByteOrder.DCBA));

        Assert.IsTrue(ValueCodec.TryEncode("12 34", "Hex", 2, out byte[] hex, out _, ByteOrder.DCBA));
        CollectionAssert.AreEqual(new byte[] { 0x12, 0x34 }, hex);
    }

    // =========================================================================
    // String：真正的文本类型，不再是 Hex 的别名
    // =========================================================================

    [TestMethod]
    public void String_IsDecodedAsText_NotHex() {
        // 早先 String 只是 Hex 的别名，选它会得到 "48 45 4C 4C 4F"
        byte[] hello = { 0x48, 0x45, 0x4C, 0x4C, 0x4F };   // "HELLO"
        Assert.AreEqual("HELLO", ValueCodec.Decode(hello, "String"));
        Assert.AreEqual("48 45 4C 4C 4F", ValueCodec.Decode(hello, "Hex"));
    }

    [TestMethod]
    public void String_StopsAtNul_AndTrimsPadding() {
        // PLC 里是定长区域，内容短于区域时以 NUL 或空格补齐。
        // 不裁掉的话界面上会跟着一串不可见字符。
        byte[] padded = { 0x4F, 0x4B, 0x00, 0x00, 0x00, 0x00 };
        Assert.AreEqual("OK", ValueCodec.Decode(padded, "String"));

        byte[] spaces = { 0x4F, 0x4B, 0x20, 0x20 };
        Assert.AreEqual("OK", ValueCodec.Decode(spaces, "String"));
    }

    [TestMethod]
    public void String_ByteSwapOrders_SwapCharPairs_NotWholeText() {
        // 一个寄存器装两个字符。字节交换的设备表现为「每两个字符颠倒」，
        // 而不是整段文本倒过来——整段反转不是任何真实设备的行为。
        //
        // "HELLO\0" = 48 45 | 4C 4C | 4F 00，逐寄存器交换后设备里是：
        //             45 48 | 4C 4C | 00 4F
        // 注意最后一对是 4F 00 → 00 4F：交换是逐寄存器统一做的，
        // 不会因为那一半是填充就跳过。
        byte[] swapped = { 0x45, 0x48, 0x4C, 0x4C, 0x00, 0x4F };
        Assert.AreEqual("HELLO", ValueCodec.Decode(swapped, "String", ByteOrder.BADC));

        // 字序（CDAB）对字符串无意义：字符串本就按地址顺序连续读出
        byte[] plain = { 0x48, 0x45, 0x4C, 0x4C, 0x4F, 0x00 };
        Assert.AreEqual("HELLO", ValueCodec.Decode(plain, "String", ByteOrder.CDAB));
    }

    [TestMethod]
    public void String_Encode_PadsToDeclaredLength_WithNul() {
        Assert.IsTrue(ValueCodec.TryEncode("OK", "String", 6, out byte[] data, out string err), err);
        CollectionAssert.AreEqual(new byte[] { 0x4F, 0x4B, 0x00, 0x00, 0x00, 0x00 }, data);
    }

    [TestMethod]
    public void String_Encode_RejectsOverflow_InsteadOfTruncating() {
        // 静默截断会让操作员以为整串都写进去了
        Assert.IsFalse(ValueCodec.TryEncode("ABCDEFGH", "String", 4, out _, out string err));
        StringAssert.Contains(err, "超出声明长度");
    }

    [TestMethod]
    public void String_Encode_PadsToEvenLength_WhenLengthUnspecified() {
        // 寄存器是 16 位的，写奇数个字节协议层无法表达
        Assert.IsTrue(ValueCodec.TryEncode("ABC", "String", 0, out byte[] data, out _));
        Assert.HasCount(4, data);
        CollectionAssert.AreEqual(new byte[] { 0x41, 0x42, 0x43, 0x00 }, data);
    }

    [TestMethod]
    public void String_RoundTrips_UnderEveryOrder() {
        foreach (ByteOrder order in Enum.GetValues<ByteOrder>()) {
            Assert.IsTrue(ValueCodec.TryEncode("PLC-01", "String", 8, out byte[] d, out string e, order), e);
            Assert.AreEqual("PLC-01", ValueCodec.Decode(d, "String", order),
                $"{order}: 字符串编解码不互逆");
        }
    }

    // =========================================================================
    // 位宽较大的整型 / 浮点
    // =========================================================================

    [TestMethod]
    public void Int64_And_Double_DecodeAsBigEndian() {
        // 这三种类型 codec 一直支持，但 Web 的下拉框漏了，选不到
        Assert.AreEqual("1",
            ValueCodec.Decode(new byte[] { 0, 0, 0, 0, 0, 0, 0, 1 }, "Int64"));

        Assert.AreEqual("18446744073709551615",
            ValueCodec.Decode(new byte[] { 0xFF, 0xFF, 0xFF, 0xFF, 0xFF, 0xFF, 0xFF, 0xFF }, "UInt64"));

        // 0x4059000000000000 = 100.0d
        Assert.AreEqual("100",
            ValueCodec.Decode(new byte[] { 0x40, 0x59, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00 }, "Double"));
    }

    [TestMethod]
    public void DefaultLength_CoversEveryOfferedType() {
        // 界面下拉里能选到的每一种类型都必须有合理的默认字节数，
        // 否则新增变量时「读取字节数」会默认成 2 而读回半截数据
        Assert.AreEqual(1,  ValueCodec.DefaultLength("Bool"));
        Assert.AreEqual(2,  ValueCodec.DefaultLength("Int16"));
        Assert.AreEqual(2,  ValueCodec.DefaultLength("UInt16"));
        Assert.AreEqual(4,  ValueCodec.DefaultLength("Int32"));
        Assert.AreEqual(4,  ValueCodec.DefaultLength("UInt32"));
        Assert.AreEqual(4,  ValueCodec.DefaultLength("Float"));
        Assert.AreEqual(8,  ValueCodec.DefaultLength("Int64"));
        Assert.AreEqual(8,  ValueCodec.DefaultLength("UInt64"));
        Assert.AreEqual(8,  ValueCodec.DefaultLength("Double"));
        Assert.AreEqual(16, ValueCodec.DefaultLength("String"));
        Assert.AreEqual(16, ValueCodec.DefaultLength("Hex"));

        Assert.IsTrue(ValueCodec.IsVariableLength("String"));
        Assert.IsTrue(ValueCodec.IsVariableLength("Hex"));
        Assert.IsFalse(ValueCodec.IsVariableLength("Int32"));
    }

    // =========================================================================
    // 两条编码入口必须等价
    // =========================================================================

    [TestMethod]
    public void TryEncode_And_TryEncodeValue_AgreeOnEveryType() {
        // Web 走文本入口（TryEncode），WPF 走强类型入口（TryEncodeValue）。
        // 两者一旦分叉，就会重演「一个 UI 写对、另一个写错」的老问题，
        // 所以这里逐类型断言它们产出完全相同的字节。
        AssertSame("Int16",  "-1234",      (short)-1234);
        AssertSame("UInt16", "4660",       (ushort)4660);
        AssertSame("Int32",  "305419896",  305419896);
        AssertSame("UInt32", "3735928559", 3735928559u);
        AssertSame("Int64",  "-1",         -1L);
        AssertSame("UInt64", "18446744073709551615", ulong.MaxValue);
        AssertSame("Float",  "3.14",       3.14f);
        AssertSame("Double", "2.718281828", 2.718281828d);
        AssertSame("Bool",   "1",          true);

        static void AssertSame(string type, string text, object value) {
            foreach (ByteOrder order in Enum.GetValues<ByteOrder>()) {
                Assert.IsTrue(ValueCodec.TryEncode(text, type, 0, out byte[] fromText, out string e1, order), e1);
                Assert.IsTrue(ValueCodec.TryEncodeValue(value, type, 0, out byte[] fromValue, out string e2, order), e2);
                CollectionAssert.AreEqual(fromText, fromValue,
                    $"{type} / {order}: 文本入口与强类型入口产出的字节不一致——两个 UI 会写出不同的值");
            }
        }
    }

    [TestMethod]
    public void ParseOrder_FallsBackToBigEndian_OnUnknownInput() {
        // 老配置文件里没有该字段，反序列化会得到 null/空——必须落到大端，
        // 而不是枚举默认值恰好正确这种巧合
        Assert.AreEqual(ByteOrder.ABCD, ValueCodec.ParseOrder(null));
        Assert.AreEqual(ByteOrder.ABCD, ValueCodec.ParseOrder(""));
        Assert.AreEqual(ByteOrder.ABCD, ValueCodec.ParseOrder("垃圾值"));
        Assert.AreEqual(ByteOrder.CDAB, ValueCodec.ParseOrder("cdab"));
    }
}
