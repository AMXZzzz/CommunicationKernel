using System;
using System.Text;
using CommunicationDebuggingTools.Core.Enums;
using CommunicationDebuggingTools.Core.Tools;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using Plugin.ModbusTcp;
using Plugin.Panasonic;

namespace CommunicationDebuggingTools.Tests {

    /// <summary>
    /// 编解码 / 字序 / MEWTOCOL 帧快照（不连 PLC）。
    /// </summary>
    [TestClass]
    public class ProtocolCodecToolsTests {

        [TestMethod]
        public void FromWords_Int32_HighWordFirst () {
            // 高字在前：0x0001_0002 → 65538
            ushort[] w = { 0x0001, 0x0002 };
            object v = ProtocolCodecTools.FromWords(
                w, VariableDataType.Int32,
                WordOrder.HighWordFirst, ByteOrder.BigEndian,
                0, StringEncodingKind.Utf8);
            Assert.AreEqual(0x00010002, (int)v);
        }

        [TestMethod]
        public void FromWords_Int32_LowWordFirst () {
            ushort[] w = { 0x0001, 0x0002 };
            object v = ProtocolCodecTools.FromWords(
                w, VariableDataType.Int32,
                WordOrder.LowWordFirst, ByteOrder.BigEndian,
                0, StringEncodingKind.Utf8);
            Assert.AreEqual(0x00020001, (int)v);
        }

        [TestMethod]
        public void ToWords_Float_RoundTrip_HighWordFirst () {
            const float expected = 12.5f;
            ushort[] words = ProtocolCodecTools.ToWords(
                expected, VariableDataType.Float, 0,
                WordOrder.HighWordFirst, ByteOrder.BigEndian,
                StringEncodingKind.Utf8);
            Assert.AreEqual(2, words.Length);
            object back = ProtocolCodecTools.FromWords(
                words, VariableDataType.Float,
                WordOrder.HighWordFirst, ByteOrder.BigEndian,
                0, StringEncodingKind.Utf8);
            Assert.AreEqual(expected, (float)back);
        }

        [TestMethod]
        public void FromWords_String_Utf8_TrimNull () {
            // "AB" → 0x4142 in one word (big endian within register layout of WordsToString)
            ushort[] w = ProtocolCodecTools.ToWords(
                "AB", VariableDataType.String, 4,
                WordOrder.HighWordFirst, ByteOrder.BigEndian,
                StringEncodingKind.Utf8);
            object s = ProtocolCodecTools.FromWords(
                w, VariableDataType.String,
                WordOrder.HighWordFirst, ByteOrder.BigEndian,
                4, StringEncodingKind.Utf8);
            Assert.AreEqual("AB", s as string);
        }
    }

    /// <summary>Modbus 寄存器字序单元测试（不连设备）。</summary>
    [TestClass]
    public class ModbusWordOrderTests {

        [TestMethod]
        public void RegistersToInt32_HighWordFirst () {
            int v = ModbusTcpSession.RegistersToInt32(0x0001, 0x0002, highWordFirst: true);
            Assert.AreEqual(0x00010002, v);
        }

        [TestMethod]
        public void RegistersToInt32_LowWordFirst () {
            int v = ModbusTcpSession.RegistersToInt32(0x0001, 0x0002, highWordFirst: false);
            Assert.AreEqual(0x00020001, v);
        }

        [TestMethod]
        public void Float_RoundTrip_HighWordFirst () {
            const float expected = 3.1415926f;
            ushort high, low;
            ModbusTcpSession.FloatToRegisters(expected, out high, out low, highWordFirst: true);
            float back = ModbusTcpSession.RegistersToFloat(high, low, highWordFirst: true);
            Assert.AreEqual(expected, back);
        }

        [TestMethod]
        public void Float_RoundTrip_LowWordFirst () {
            const float expected = -9.5f;
            ushort high, low;
            ModbusTcpSession.FloatToRegisters(expected, out high, out low, highWordFirst: false);
            float back = ModbusTcpSession.RegistersToFloat(high, low, highWordFirst: false);
            Assert.AreEqual(expected, back);
        }

        [TestMethod]
        public void ParseAddress_Holding40001 () {
            int a = ModbusTcpSession.ParseAddress("40001");
            Assert.AreEqual(0, a);
        }

        [TestMethod]
        public void ParseAddress_ZeroBased () {
            int a = ModbusTcpSession.ParseAddress("100");
            Assert.AreEqual(100, a);
        }
    }

    /// <summary>MEWTOCOL 命令/BCC 帧快照（不连设备）。</summary>
    [TestClass]
    public class MewtocolFrameSnapshotTests {

        [TestMethod]
        public void CalcBcc_XorRoundTrip () {
            string payload = "01#RDD00100D00100";
            string bcc = PanasonicSession.CalcBcc(payload);
            Assert.AreEqual(2, bcc.Length);

            // 手工重算
            byte x = 0;
            foreach (char c in payload)
                x ^= (byte)c;
            Assert.AreEqual(x.ToString("X2"), bcc);
        }

        [TestMethod]
        public void BuildReadDataFrame_Snapshot () {
            // 站号 1，读 DT100 一字 → 命令体 RD + D00100D00100
            var addr = PanasonicSession.ParseAddress("DT100");
            string start = PanasonicSession.FormatDataAddr(addr);
            string end = PanasonicSession.FormatDataAddr(addr);
            Assert.AreEqual("D00100", start);

            string body = "RD" + start + end;
            Assert.AreEqual("RDD00100D00100", body);

            string payload = "01#" + body;
            string frame = "%" + payload + PanasonicSession.CalcBcc(payload) + "\r";
            Assert.IsTrue(frame.StartsWith("%01#RDD00100D00100"));
            Assert.IsTrue(frame.EndsWith("\r"));
            Assert.AreEqual(1 + payload.Length + 2 + 1, frame.Length); // % + payload + BCC + CR
        }

        [TestMethod]
        public void FormatContact_R10A_Snapshot () {
            var a = PanasonicSession.ParseAddress("R10A");
            Assert.AreEqual("R010A", PanasonicSession.FormatContact(a));
            string cmd = "RCS" + PanasonicSession.FormatContact(a);
            Assert.AreEqual("RCSR010A", cmd);
        }

        [TestMethod]
        public void FormatDataAddr_DT200 () {
            var a = PanasonicSession.ParseAddress("DT200");
            Assert.AreEqual("D00200", PanasonicSession.FormatDataAddr(a));
        }
    }

    /// <summary>S7 字符串编解码（纯字节，不连 PLC）。</summary>
    [TestClass]
    public class SiemensS7StringCodecTests {

        [TestMethod]
        public void String_Utf8_RoundTrip_ViaCodecTools () {
            // 与 S7 插件同一套编码入口
            const string text = "测试A";
            ushort[] words = ProtocolCodecTools.ToWords(
                text, VariableDataType.String, 16,
                WordOrder.HighWordFirst, ByteOrder.BigEndian,
                StringEncodingKind.Utf8);
            string back = ProtocolCodecTools.FromWords(
                words, VariableDataType.String,
                WordOrder.HighWordFirst, ByteOrder.BigEndian,
                16, StringEncodingKind.Utf8) as string;
            Assert.AreEqual(text, back);
        }
    }
}
