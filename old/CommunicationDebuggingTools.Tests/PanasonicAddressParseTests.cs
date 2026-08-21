using System;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using Plugin.Panasonic;


namespace CommunicationDebuggingTools.Tests {
    /// <summary>
    /// 松下地址解析单测（不连 PLC）。
    /// </summary>
    [TestClass]
    public class PanasonicAddressParseTests {
        [TestMethod]
        public void Parse_X0_ShouldBeBit () {
            var a = PanasonicSession.ParseAddress("X0");
            Assert.AreEqual(PanasonicArea.X, a.Area);
            Assert.AreEqual(0, a.Index);
            Assert.IsTrue(a.IsBit);
        }

        [TestMethod]
        public void Parse_Y10_ShouldBeBit () {
            var a = PanasonicSession.ParseAddress("Y10");
            Assert.AreEqual(PanasonicArea.Y, a.Area);
            Assert.AreEqual(10, a.Index);
            Assert.IsTrue(a.IsBit);
        }

        [TestMethod]
        public void Parse_R100_Decimal () {
            var a = PanasonicSession.ParseAddress("R100");
            Assert.AreEqual(PanasonicArea.R, a.Area);
            Assert.AreEqual(100, a.Index);
            Assert.IsTrue(a.IsBit);
        }

        [TestMethod]
        public void Parse_DT100_ShouldBeWordArea () {
            var a = PanasonicSession.ParseAddress("DT100");
            Assert.AreEqual(PanasonicArea.DT, a.Area);
            Assert.AreEqual(100, a.Index);
            Assert.IsFalse(a.IsBit);
        }

        [TestMethod]
        public void Parse_WR0 () {
            var a = PanasonicSession.ParseAddress("WR0");
            Assert.AreEqual(PanasonicArea.WR, a.Area);
            Assert.AreEqual(0, a.Index);
            Assert.IsFalse(a.IsBit);
        }


        [TestMethod]
        public void Parse_IgnoreCaseAndSpaces () {
            var a = PanasonicSession.ParseAddress(" r10a ");
            Assert.AreEqual(10, a.Index);
            Assert.AreEqual(0xA, a.BitIndex);
        }

        [TestMethod]
        [ExpectedException(typeof(ArgumentException))]
        public void Parse_Empty_ShouldThrow () {
            PanasonicSession.ParseAddress("");
        }

        [TestMethod]
        [ExpectedException(typeof(ArgumentException))]
        public void Parse_UnknownPrefix_ShouldThrow () {
            PanasonicSession.ParseAddress("ZZ0");
        }

        [TestMethod]
        public void Parse_R10A_ShouldBeWordAndBit () {
            var a = PanasonicSession.ParseAddress("R10A");
            Assert.AreEqual(PanasonicArea.R, a.Area);
            Assert.AreEqual(10, a.Index);
            Assert.AreEqual(0xA, a.BitIndex);
            Assert.IsTrue(a.IsBit);
            Assert.AreEqual("R010A", PanasonicSession.FormatContact(a));
        }

        [TestMethod]
        public void Parse_R1A_ShouldBeWord1BitA () {
            var a = PanasonicSession.ParseAddress("R1A");
            Assert.AreEqual(PanasonicArea.R, a.Area);
            Assert.AreEqual(1, a.Index);
            Assert.AreEqual(0xA, a.BitIndex);
            Assert.IsTrue(a.IsBit);
            Assert.AreEqual("R001A", PanasonicSession.FormatContact(a));
        }

        [TestMethod]
        public void Parse_R100_ShouldBeDecimalContact () {
            var a = PanasonicSession.ParseAddress("R100");
            Assert.AreEqual(100, a.Index);
            Assert.AreEqual(-1, a.BitIndex);
            Assert.AreEqual("R00100", PanasonicSession.FormatContact(a));
        }

    }

    /// <summary>可选：加载松下插件 dll。</summary>
    [TestClass]
    public class PanasonicPluginLoadTests {
        [TestMethod]
        public void ProtocolResolver_LoadPlugin_ShouldFindPanasonic () {
            string testsBin = System.AppDomain.CurrentDomain.BaseDirectory;
            string pluginDir = System.IO.Path.GetFullPath(
                System.IO.Path.Combine(testsBin, @"..\..\..\Plugin.Panasonic\bin\Debug"));

            if (!System.IO.Directory.Exists(pluginDir)) {
                Assert.Inconclusive("插件目录不存在: " + pluginDir);
                return;
            }

            var files = System.IO.Directory.GetFiles(pluginDir, "Plugin.*.dll");
            if (files.Length == 0)
                files = System.IO.Directory.GetFiles(pluginDir, "*Panasonic*.dll");

            Assert.IsTrue(files.Length > 0, "未找到松下插件: " + pluginDir);

            var resolver = new CommunicationDebuggingTools.Business.Plugins.ProtocolResolver();
            resolver.LoadFromFolder(pluginDir);

            var names = resolver.GetProtocolNames();
            Assert.IsTrue(
                names.Contains("Panasonic MEWTOCOL"),
                "未加载到 Panasonic MEWTOCOL: " + string.Join(",", names));
        }
    }
}