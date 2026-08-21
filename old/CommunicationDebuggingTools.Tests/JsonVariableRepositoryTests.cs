using System.Collections.Generic;
using System.IO;
using CommunicationDebuggingTools.Business.Persistence;
using CommunicationDebuggingTools.Core.Enums;
using CommunicationDebuggingTools.Core.Models;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace CommunicationDebuggingTools.Tests {
    [TestClass]
    public class JsonVariableRepositoryTests {
        [TestMethod]
        public void SaveAndLoad_RoundTrip_PreservesFields_ClearsRuntime () {
            string path = Path.Combine(Path.GetTempPath(), "cdt_vars_" + Path.GetRandomFileName() + ".json");
            try {
                var repo = new JsonVariableRepository(path);
                var v = new VariableItem {
                    DeviceId = "dev1",
                    Name = "Speed",
                    Address = "DT100",
                    DataType = VariableDataType.Int16,
                    Access = VariableAccess.ReadWrite,
                    Unit = "rpm",
                    Category = "监控数据",
                    Description = "主轴",
                    ScanRateMs = 500,
                    IsPollingEnabled = true,
                    LastValue = 99,
                    Quality = DataQuality.Good,
                    LastError = "should-not-persist"
                };
                repo.SaveAll(new List<VariableItem> { v });
                IList<VariableItem> loaded = repo.LoadAll();
                Assert.AreEqual(1, loaded.Count);
                Assert.AreEqual("Speed", loaded[0].Name);
                Assert.AreEqual("DT100", loaded[0].Address);
                Assert.AreEqual("rpm", loaded[0].Unit);
                Assert.AreEqual(500, loaded[0].ScanRateMs);
                Assert.IsTrue(loaded[0].IsPollingEnabled);
                Assert.IsNull(loaded[0].LastValue);
                Assert.AreEqual(DataQuality.Bad, loaded[0].Quality);
            } finally {
                if (File.Exists(path)) File.Delete(path);
            }
        }

        [TestMethod]
        public void Load_LegacyArray_StillWorks () {
            string path = Path.Combine(Path.GetTempPath(), "cdt_vars_legacy_" + Path.GetRandomFileName() + ".json");
            try {
                File.WriteAllText(path,
                    "[{\"Id\":\"a1\",\"DeviceId\":\"d1\",\"Name\":\"X\",\"Address\":\"0\",\"DataType\":0,\"Access\":2,\"Length\":0}]");
                var repo = new JsonVariableRepository(path);
                IList<VariableItem> loaded = repo.LoadAll();
                Assert.AreEqual(1, loaded.Count);
                Assert.AreEqual("X", loaded[0].Name);
                Assert.AreEqual("0", loaded[0].Address);
            } finally {
                if (File.Exists(path)) File.Delete(path);
            }
        }
    }
}
