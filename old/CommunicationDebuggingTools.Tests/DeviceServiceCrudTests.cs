using System;
using System.Threading;
using System.Threading.Tasks;
using CommunicationDebuggingTools.Business.Device;
using CommunicationDebuggingTools.Core.Enums;
using CommunicationDebuggingTools.Core.Models;
using CommunicationDebuggingTools.Tests.Fakes;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace CommunicationDebuggingTools.Tests {
    [TestClass]
    public class DeviceServiceCrudTests {

        [TestMethod]
        public void Add_ShouldGenerateId_AndPersistToRepository () {
            var repo = new FakeDeviceRepository();
            var svc = new DeviceService(new FakeProtocolResolver(), repo, new FakeTcpProbe());

            var device = CreateDevice();
            device.Id = null;

            svc.Add(device);

            Assert.AreEqual(1, svc.Devices.Count);
            Assert.AreEqual(1, repo.Items.Count);
            Assert.IsFalse(string.IsNullOrWhiteSpace(svc.Devices[0].Id));
            Assert.AreEqual(svc.Devices[0].Id, repo.Items[0].Id);
        }

        [TestMethod]
        public async Task Update_WhenConnectionConfigChanged_ShouldDisconnectSession () {
            var protocol = new FakeProtocol { ConnectResult = true };
            var resolver = new FakeProtocolResolver { ProtocolToReturn = protocol };
            var svc = new DeviceService(resolver, new FakeDeviceRepository(), new FakeTcpProbe { Result = true });

            var device = CreateDevice();
            svc.Add(device);
            bool connected = await svc.ConnectAsync(device.Id, CancellationToken.None);
            Assert.IsTrue(connected);
            Assert.IsNotNull(svc.GetProtocol(device.Id));

            var updated = new DeviceInfo {
                Id = device.Id,
                Name = device.Name,
                Model = device.Model,
                Protocol = device.Protocol,
                Ip = "127.0.0.2",
                Port = device.Port,
                StationNo = device.StationNo,
                ExtraSettingsJson = device.ExtraSettingsJson,
                Lane = device.Lane,
                ByteOrder = device.ByteOrder,
                WordOrder = device.WordOrder,
                StringEncoding = device.StringEncoding
            };

            svc.Update(updated);

            Assert.IsNull(svc.GetProtocol(device.Id));
            Assert.IsFalse(device.IsConnected);
            Assert.AreEqual(DeviceStatusType.Offline, device.StatusType);
            Assert.AreEqual("127.0.0.2", device.Ip);
        }

        [TestMethod]
        public async Task Remove_ShouldDeleteFromCollection_AndRepository () {
            var protocol = new FakeProtocol { ConnectResult = true };
            var resolver = new FakeProtocolResolver { ProtocolToReturn = protocol };
            var repo = new FakeDeviceRepository();
            var svc = new DeviceService(resolver, repo, new FakeTcpProbe { Result = true });

            var device = CreateDevice();
            svc.Add(device);
            await svc.ConnectAsync(device.Id, CancellationToken.None);

            svc.Remove(device.Id);

            Assert.AreEqual(0, svc.Devices.Count);
            Assert.AreEqual(0, repo.Items.Count);
            Assert.IsNull(svc.GetProtocol(device.Id));
        }

        private static DeviceInfo CreateDevice () {
            return new DeviceInfo {
                Id = Guid.NewGuid().ToString("N"),
                Name = "D1",
                Protocol = "Modbus TCP",
                Ip = "127.0.0.1",
                Port = 502,
                StationNo = 1,
                ExtraSettingsJson = "{}"
            };
        }
    }
}
