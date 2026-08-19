using System.Threading;
using System.Threading.Tasks;
using CommunicationDebuggingTools.Business.Device;
using CommunicationDebuggingTools.Business.Plugins;
using CommunicationDebuggingTools.Core.Enums;
using CommunicationDebuggingTools.Core.Models;
using CommunicationDebuggingTools.Tests.Fakes;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace CommunicationDebuggingTools.Tests {
    /// <summary>
    /// DeviceService 连接状态机单测（端口探测可注入，不依赖真实网络）。
    /// </summary>
    [TestClass]
    public class DeviceServiceConnectTests {

        [TestMethod]
        public async Task ConnectAsync_WhenProbeAndProtocolOk_ShouldBeSuccess () {
            var protocol = new FakeProtocol { ConnectResult = true };
            var resolver = new FakeProtocolResolver { ProtocolToReturn = protocol };
            var probe = new FakeTcpProbe { Result = true };
            var svc = new DeviceService(resolver, new FakeDeviceRepository(), probe);

            var device = CreateDevice();
            svc.Add(device);

            bool ok = await svc.ConnectAsync(device.Id, CancellationToken.None);

            Assert.IsTrue(ok);
            Assert.IsTrue(device.IsConnected);
            Assert.AreEqual(DeviceStatusType.Success, device.StatusType);
            Assert.AreEqual(1, protocol.ConnectCallCount);
            Assert.AreEqual(1, probe.CallCount);
            Assert.IsNotNull(svc.GetProtocol(device.Id));
        }

        [TestMethod]
        public async Task ConnectAsync_WhenProbeFails_ShouldBeOffline () {
            var protocol = new FakeProtocol { ConnectResult = true };
            var resolver = new FakeProtocolResolver { ProtocolToReturn = protocol };
            var probe = new FakeTcpProbe { Result = false };
            var svc = new DeviceService(resolver, new FakeDeviceRepository(), probe);

            var device = CreateDevice();
            svc.Add(device);

            bool ok = await svc.ConnectAsync(device.Id, CancellationToken.None);

            Assert.IsFalse(ok);
            Assert.IsFalse(device.IsConnected);
            Assert.AreEqual(DeviceStatusType.Offline, device.StatusType);
            Assert.AreEqual(0, protocol.ConnectCallCount);
            Assert.IsNull(svc.GetProtocol(device.Id));
        }

        [TestMethod]
        public async Task ConnectAsync_WhenProtocolFails_ShouldBeError () {
            var protocol = new FakeProtocol { ConnectResult = false };
            var resolver = new FakeProtocolResolver { ProtocolToReturn = protocol };
            var probe = new FakeTcpProbe { Result = true };
            var svc = new DeviceService(resolver, new FakeDeviceRepository(), probe);

            var device = CreateDevice();
            svc.Add(device);

            bool ok = await svc.ConnectAsync(device.Id, CancellationToken.None);

            Assert.IsFalse(ok);
            Assert.IsFalse(device.IsConnected);
            Assert.AreEqual(DeviceStatusType.Error, device.StatusType);
            Assert.IsNull(svc.GetProtocol(device.Id));
        }

        [TestMethod]
        public async Task ConnectAsync_WhenProtocolMissing_ShouldBeError () {
            var resolver = new FakeProtocolResolver { ProtocolToReturn = null };
            var probe = new FakeTcpProbe { Result = true };
            var svc = new DeviceService(resolver, new FakeDeviceRepository(), probe);

            var device = CreateDevice();
            device.Protocol = "NotExist";
            svc.Add(device);

            bool ok = await svc.ConnectAsync(device.Id, CancellationToken.None);

            Assert.IsFalse(ok);
            Assert.IsFalse(device.IsConnected);
            Assert.AreEqual(DeviceStatusType.Error, device.StatusType);
        }

        [TestMethod]
        public async Task Disconnect_ShouldClearSessionAndOffline () {
            var protocol = new FakeProtocol { ConnectResult = true };
            var resolver = new FakeProtocolResolver { ProtocolToReturn = protocol };
            var probe = new FakeTcpProbe { Result = true };
            var svc = new DeviceService(resolver, new FakeDeviceRepository(), probe);

            var device = CreateDevice();
            svc.Add(device);
            Assert.IsTrue(await svc.ConnectAsync(device.Id, CancellationToken.None));

            svc.Disconnect(device.Id);

            Assert.IsFalse(device.IsConnected);
            Assert.AreEqual(DeviceStatusType.Offline, device.StatusType);
            Assert.IsNull(svc.GetProtocol(device.Id));
            Assert.IsTrue(protocol.DisconnectCallCount >= 1);
        }

        [TestMethod]
        public void Load_ShouldResetRuntimeToOffline () {
            var repo = new FakeDeviceRepository();
            var d = CreateDevice();
            d.IsConnected = true;
            d.StatusType = DeviceStatusType.Error;
            repo.Items.Add(d);

            var svc = new DeviceService(
                new FakeProtocolResolver(),
                repo,
                new FakeTcpProbe());
            svc.Load();

            Assert.AreEqual(1, svc.Devices.Count);
            Assert.IsFalse(svc.Devices[0].IsConnected);
            Assert.AreEqual(DeviceStatusType.Offline, svc.Devices[0].StatusType);
        }

        [TestMethod]
        public void ProtocolResolver_LoadPlugin_ShouldFindModbusTcp () {
            string testsBin = System.AppDomain.CurrentDomain.BaseDirectory;
            string pluginDir = System.IO.Path.GetFullPath(
                System.IO.Path.Combine(testsBin, @"..\..\..\Plugin.ModbusTcp\bin\Debug"));

            if (!System.IO.Directory.Exists(pluginDir)) {
                Assert.Inconclusive("插件目录不存在: " + pluginDir);
                return;
            }

            string[] files = System.IO.Directory.GetFiles(pluginDir, "Plugin.*.dll");
            if (files.Length == 0)
                files = System.IO.Directory.GetFiles(pluginDir, "*Modbus*.dll");
            if (files.Length == 0) {
                Assert.Inconclusive("目录下没有插件 dll: " + pluginDir);
                return;
            }

            var resolver = new ProtocolResolver();
            resolver.LoadFromFolder(pluginDir);
            var names = resolver.GetProtocolNames();
            Assert.IsTrue(names.Contains("Modbus TCP"), "未加载到 Modbus TCP: " + string.Join(",", names));
        }

        private static DeviceInfo CreateDevice () {
            return new DeviceInfo {
                Name = "T1",
                Protocol = "Modbus TCP",
                Ip = "127.0.0.1",
                Port = 502,
                StationNo = 1,
                ExtraSettingsJson = "{}"
            };
        }
    }
}
