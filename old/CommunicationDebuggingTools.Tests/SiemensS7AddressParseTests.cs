using Microsoft.VisualStudio.TestTools.UnitTesting;
using Plugin.SiemensS7;

using System;

namespace CommunicationDebuggingTools.Tests {
    /// <summary>
    /// Siemens S7 地址解析单测（不连 PLC）。
    /// 需：Tests 引用 Plugin.SiemensS7，且 InternalsVisibleTo 已配置。
    /// </summary>
    [TestClass]
    public class SiemensS7AddressParseTests {
        [TestMethod]
        public void Parse_DbxBit_ShouldSetDbAndBit () {
            var a = SiemensS7Session.ParseAddress("DB1.DBX0.0");

            Assert.AreEqual('D', a.Area);
            Assert.AreEqual(1, a.DbNumber);
            Assert.AreEqual(0, a.ByteOffset);
            Assert.AreEqual(0, a.Bit);
            Assert.AreEqual(S7TransportSize.Bit, a.Size);
        }

        [TestMethod]
        public void Parse_DbxBit_HighBit_ShouldWork () {
            var a = SiemensS7Session.ParseAddress("DB10.DBX2.7");

            Assert.AreEqual(10, a.DbNumber);
            Assert.AreEqual(2, a.ByteOffset);
            Assert.AreEqual(7, a.Bit);
            Assert.AreEqual(S7TransportSize.Bit, a.Size);
        }

        [TestMethod]
        public void Parse_Dbb_ShouldBeByte () {
            var a = SiemensS7Session.ParseAddress("DB1.DBB4");

            Assert.AreEqual('D', a.Area);
            Assert.AreEqual(1, a.DbNumber);
            Assert.AreEqual(4, a.ByteOffset);
            Assert.AreEqual(-1, a.Bit);
            Assert.AreEqual(S7TransportSize.Byte, a.Size);
        }

        [TestMethod]
        public void Parse_Dbw_ShouldBeWord () {
            var a = SiemensS7Session.ParseAddress("DB2.DBW10");

            Assert.AreEqual(2, a.DbNumber);
            Assert.AreEqual(10, a.ByteOffset);
            Assert.AreEqual(S7TransportSize.Word, a.Size);
        }

        [TestMethod]
        public void Parse_Dbd_ShouldBeDWord () {
            var a = SiemensS7Session.ParseAddress("DB1.DBD0");

            Assert.AreEqual(0, a.ByteOffset);
            Assert.AreEqual(S7TransportSize.DWord, a.Size);
        }

        [TestMethod]
        public void Parse_MBit_ShouldMapAreaM () {
            var a = SiemensS7Session.ParseAddress("M10.3");

            Assert.AreEqual('M', a.Area);
            Assert.AreEqual(10, a.ByteOffset);
            Assert.AreEqual(3, a.Bit);
            Assert.AreEqual(S7TransportSize.Bit, a.Size);
        }

        [TestMethod]
        public void Parse_MB_MW_MD () {
            var b = SiemensS7Session.ParseAddress("MB0");
            Assert.AreEqual(S7TransportSize.Byte, b.Size);
            Assert.AreEqual(0, b.ByteOffset);

            var w = SiemensS7Session.ParseAddress("MW2");
            Assert.AreEqual(S7TransportSize.Word, w.Size);
            Assert.AreEqual(2, w.ByteOffset);

            var d = SiemensS7Session.ParseAddress("MD4");
            Assert.AreEqual(S7TransportSize.DWord, d.Size);
            Assert.AreEqual(4, d.ByteOffset);
        }

        [TestMethod]
        public void Parse_GermanE_ShouldMapToI () {
            var a = SiemensS7Session.ParseAddress("E0.1");
            Assert.AreEqual('I', a.Area);
            Assert.AreEqual(0, a.ByteOffset);
            Assert.AreEqual(1, a.Bit);
        }

        [TestMethod]
        public void Parse_GermanA_ShouldMapToQ () {
            var a = SiemensS7Session.ParseAddress("A1.0");
            Assert.AreEqual('Q', a.Area);
            Assert.AreEqual(1, a.ByteOffset);
            Assert.AreEqual(0, a.Bit);
        }

        [TestMethod]
        public void Parse_IgnoreCaseAndSpaces () {
            var a = SiemensS7Session.ParseAddress(" db1.dbx0.0 ");
            Assert.AreEqual(1, a.DbNumber);
            Assert.AreEqual(S7TransportSize.Bit, a.Size);
        }

        [TestMethod]
        [ExpectedException(typeof(ArgumentException))]
        public void Parse_Empty_ShouldThrow () {
            SiemensS7Session.ParseAddress("");
        }

        [TestMethod]
        [ExpectedException(typeof(ArgumentException))]
        public void Parse_BitOutOfRange_ShouldThrow () {
            SiemensS7Session.ParseAddress("DB1.DBX0.8");
        }

        [TestMethod]
        [ExpectedException(typeof(ArgumentException))]
        public void Parse_InvalidDb_ShouldThrow () {
            SiemensS7Session.ParseAddress("DB1.XYZ0");
        }
    }

    /// <summary>
    /// 插件目录加载 Siemens S7（可选，路径按本机输出目录调整）。
    /// </summary>
    [TestClass]
    public class SiemensS7PluginLoadTests {
        [TestMethod]
        public void ProtocolResolver_LoadPlugin_ShouldFindSiemensS7 () {
            string testsBin = System.AppDomain.CurrentDomain.BaseDirectory;
            string pluginDir = System.IO.Path.GetFullPath(
                System.IO.Path.Combine(testsBin, @"..\..\..\Plugin.SiemensS7\bin\Debug"));

            if (!System.IO.Directory.Exists(pluginDir)) {
                Assert.Inconclusive("插件目录不存在: " + pluginDir);
                return;
            }

            string[] files = System.IO.Directory.GetFiles(pluginDir, "Plugin.*.dll");
            if (files.Length == 0) {
                // 若程序集名不是 Plugin. 前缀，按实际 dll 名改搜索
                files = System.IO.Directory.GetFiles(pluginDir, "*SiemensS7*.dll");
            }

            Assert.IsTrue(files.Length > 0, "未找到 S7 插件 dll: " + pluginDir);

            var resolver = new CommunicationDebuggingTools.Business.Plugins.ProtocolResolver();
            resolver.LoadFromFolder(pluginDir);

            var names = resolver.GetProtocolNames();
            Assert.IsTrue(
                names.Contains("Siemens S7"),
                "未加载到 Siemens S7，当前: " + string.Join(",", names));
        }
    }
}