using System.Threading;
using CommunicationDebuggingTools.Business.Device;
using CommunicationDebuggingTools.Business.Variable;
using CommunicationDebuggingTools.Core.Models;
using CommunicationDebuggingTools.Tests.Fakes;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace CommunicationDebuggingTools.Tests {
    /// <summary>轮询引擎启停与幂等（不依赖真实 PLC）。</summary>
    [TestClass]
    public class PollingEngineTests {
        [TestMethod]
        public void StartStop_IsIdempotent () {
            var protocol = new FakeProtocol { ConnectResult = true };
            var devices = new DeviceService(
                new FakeProtocolResolver { ProtocolToReturn = protocol },
                new FakeDeviceRepository(),
                new FakeTcpProbe { Result = true });
            var vars = new VariableService(devices, new FakeVariableRepository());
            var engine = new PollingEngine(vars, devices);

            Assert.IsFalse(engine.IsRunning);
            engine.Start();
            Assert.IsTrue(engine.IsRunning);
            engine.Start(); // 二次启动无副作用
            Assert.IsTrue(engine.IsRunning);

            engine.Stop();
            Assert.IsFalse(engine.IsRunning);
            engine.Stop(); // 二次停止无副作用
            Assert.IsFalse(engine.IsRunning);
        }

        [TestMethod]
        public void Start_WithPollingVariable_CanStopCleanly () {
            var protocol = new FakeProtocol { ConnectResult = true, ReadResult = true, ReadValue = (short)1 };
            var devices = new DeviceService(
                new FakeProtocolResolver { ProtocolToReturn = protocol },
                new FakeDeviceRepository(),
                new FakeTcpProbe { Result = true });
            var device = new DeviceInfo {
                Name = "P",
                Protocol = "Modbus TCP",
                Ip = "127.0.0.1",
                Port = 502
            };
            devices.Add(device);
            // 不真正 Connect，引擎应跳过未连接设备或读失败后仍可 Stop
            var vars = new VariableService(devices, new FakeVariableRepository());
            vars.Add(new VariableItem {
                DeviceId = device.Id,
                Name = "v1",
                Address = "0",
                IsPollingEnabled = true,
                ScanRateMs = 100
            });

            var engine = new PollingEngine(vars, devices);
            engine.Start();
            Thread.Sleep(150);
            engine.Stop();
            Assert.IsFalse(engine.IsRunning);
        }
    }
}
