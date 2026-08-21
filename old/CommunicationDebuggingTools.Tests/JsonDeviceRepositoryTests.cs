using System.Collections.Generic;
using System.IO;
using CommunicationDebuggingTools.Business.Persistence;
using CommunicationDebuggingTools.Core.Models;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace CommunicationDebuggingTools.Tests {
    [TestClass]
    public class JsonDeviceRepositoryTests {
        [TestMethod]
        public void SaveAndLoad_RoundTrip_PreservesStationAndExtra () {
            string path = Path.Combine(Path.GetTempPath(), "cdt_devices_" + Path.GetRandomFileName() + ".json");
            try {
                var repo = new JsonDeviceRepository(path);
                var d = new DeviceInfo {
                    Name = "A",
                    Protocol = "Modbus TCP",
                    Ip = "10.0.0.1",
                    Port = 502,
                    StationNo = 7,
                    ExtraSettingsJson = "{\"rack\":0}"
                };
                d.IsConnected = true;
                d.StatusType = CommunicationDebuggingTools.Core.Enums.DeviceStatusType.Success;

                repo.SaveAll(new List<DeviceInfo> { d });
                IList<DeviceInfo> loaded = repo.LoadAll();

                Assert.AreEqual(1, loaded.Count);
                Assert.AreEqual("A", loaded[0].Name);
                Assert.AreEqual(7, loaded[0].StationNo);
                Assert.AreEqual("{\"rack\":0}", loaded[0].ExtraSettingsJson);
                // 运行时状态不信任落盘：仓储映射为离线
                Assert.IsFalse(loaded[0].IsConnected);
            } finally {
                if (File.Exists(path)) File.Delete(path);
            }
        }

        [TestMethod]
        public void Load_LegacyArray_StillWorks () {
            string path = Path.Combine(Path.GetTempPath(), "cdt_devices_legacy_" + Path.GetRandomFileName() + ".json");
            try {
                File.WriteAllText(path,
                    "[{\"Id\":\"abc\",\"Name\":\"Old\",\"Protocol\":\"Siemens S7\",\"Ip\":\"1.2.3.4\",\"Port\":102,\"StationNo\":1,\"ExtraSettingsJson\":\"{}\"}]");
                var repo = new JsonDeviceRepository(path);
                IList<DeviceInfo> loaded = repo.LoadAll();
                Assert.AreEqual(1, loaded.Count);
                Assert.AreEqual("Old", loaded[0].Name);
                Assert.AreEqual("Siemens S7", loaded[0].Protocol);
            } finally {
                if (File.Exists(path)) File.Delete(path);
            }
        }

        [TestMethod]
        public void Load_LegacyUnitId_MapsToStationNo () {
            string path = Path.Combine(Path.GetTempPath(), "cdt_devices_unit_" + Path.GetRandomFileName() + ".json");
            try {
                File.WriteAllText(path,
                    "[{\"Id\":\"x\",\"Name\":\"U\",\"Protocol\":\"Modbus TCP\",\"Ip\":\"127.0.0.1\",\"Port\":502,\"UnitId\":9}]");
                var repo = new JsonDeviceRepository(path);
                IList<DeviceInfo> loaded = repo.LoadAll();
                Assert.AreEqual(1, loaded.Count);
                Assert.AreEqual(9, loaded[0].StationNo);
            } finally {
                if (File.Exists(path)) File.Delete(path);
            }
        }
    }
}
