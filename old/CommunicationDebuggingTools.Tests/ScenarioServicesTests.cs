using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using CommunicationDebuggingTools.Core.Interfaces;
using CommunicationDebuggingTools.Core.Models;
using CommunicationDebuggingTools.Scenarios;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace CommunicationDebuggingTools.Tests {

    /// <summary>Scenarios 层场景服务测试：批量添加与一键批量写入。</summary>
    [TestClass]
    public sealed class ScenarioServicesTests {

        // ── 轻量 Fake：仅覆盖场景服务用到的成员 ──
        private sealed class StubVariableService : IVariableService {
            public ObservableCollection<VariableItem> Variables { get; } = new ObservableCollection<VariableItem>();
            public List<string> WrittenIds { get; } = new List<string>();
            public object LastWrittenValue { get; private set; }
            public string FailForVariableId { get; set; }

            public void Load () { }
            public void Save () { }
            public void Add (VariableItem item) {
                if (item == null) throw new ArgumentNullException(nameof(item));
                if (string.IsNullOrEmpty(item.Id)) item.Id = Guid.NewGuid().ToString("N");
                Variables.Add(item);
            }
            public void Update (VariableItem item) { }
            public void Remove (string id) { }

            public Task<OperationResult> ReadAsync (string variableId, CancellationToken cancellationToken)
                => Task.FromResult(OperationResult.Ok);

            public Task<OperationResult> WriteAsync (string variableId, object value, CancellationToken cancellationToken) {
                WrittenIds.Add(variableId);
                LastWrittenValue = value;
                if (variableId == FailForVariableId)
                    return Task.FromResult(OperationResult.ProtocolError("模拟失败"));
                return Task.FromResult(OperationResult.Ok);
            }

            public Task ReadByDeviceAsync (string deviceId, CancellationToken cancellationToken)
                => Task.CompletedTask;
        }

        private sealed class StubDeviceService : IDeviceService {
            public ObservableCollection<DeviceInfo> Devices { get; } = new ObservableCollection<DeviceInfo>();
            public void Load () { }
            public void Save () { }
            public void Add (DeviceInfo device) { }
            public void Update (DeviceInfo device) { }
            public void Remove (string id) { }
            public Task<bool> ConnectAsync (string id, CancellationToken ct) => Task.FromResult(true);
            public void Disconnect (string id) { }
            public void DisconnectAll () { }
            public void CheckConnections () { }
            public IProtocol GetProtocol (string deviceId) => null;
            public void ReportCommSuccess (string deviceId) { }
            public void ReportCommError (string deviceId) { }
        }

        // ── VariableBatchService ──

        [TestMethod]
        public void AddBatch_ShouldAttachDeviceId_AndAddAll () {
            var vars = new StubVariableService();
            var svc = new VariableBatchService(vars);

            int added = svc.AddBatch("dev-1", new List<VariableItem> {
                new VariableItem { Name = "宽度" },
                null,
                new VariableItem { Name = "速度" }
            });

            Assert.AreEqual(2, added);
            Assert.AreEqual(2, vars.Variables.Count);
            Assert.IsTrue(vars.Variables.All(v => v.DeviceId == "dev-1"));
        }

        [TestMethod]
        public void AddBatch_WhenDeviceIdMissing_ReturnsZero () {
            var vars = new StubVariableService();
            var svc = new VariableBatchService(vars);

            Assert.AreEqual(0, svc.AddBatch(null, new List<VariableItem> { new VariableItem() }));
            Assert.AreEqual(0, vars.Variables.Count);
        }

        // ── BulkWriteService ──

        [TestMethod]
        public async Task WriteToAllDevices_ShouldWriteMatchingVariables_PerDevice () {
            var vars = new StubVariableService();
            vars.Add(new VariableItem { Id = "v1", DeviceId = "d1", Name = "宽度" });
            vars.Add(new VariableItem { Id = "v2", DeviceId = "d2", Name = "宽度" });
            vars.Add(new VariableItem { Id = "v3", DeviceId = "d1", Name = "速度" });
            var svc = new BulkWriteService(new StubDeviceService(), vars);

            IReadOnlyList<BulkWriteResult> results = await svc.WriteToAllDevicesAsync("宽度", 500);

            Assert.AreEqual(2, results.Count);
            Assert.IsTrue(results.All(r => r.Success));
            CollectionAssert.AreEquivalent(new[] { "v1", "v2" }, vars.WrittenIds.ToArray());
            Assert.AreEqual(500, vars.LastWrittenValue);
        }

        [TestMethod]
        public async Task WriteToAllDevices_SingleFailure_ShouldNotStopOthers () {
            var vars = new StubVariableService { FailForVariableId = "v1" };
            vars.Add(new VariableItem { Id = "v1", DeviceId = "d1", Name = "宽度" });
            vars.Add(new VariableItem { Id = "v2", DeviceId = "d2", Name = "宽度" });
            var svc = new BulkWriteService(new StubDeviceService(), vars);

            IReadOnlyList<BulkWriteResult> results = await svc.WriteToAllDevicesAsync("宽度", 500);

            Assert.AreEqual(2, results.Count);
            Assert.AreEqual(1, results.Count(r => r.Success));
            Assert.AreEqual(1, results.Count(r => !r.Success));
        }

        [TestMethod]
        public async Task WriteToAllDevices_WhenNameBlank_ReturnsEmpty () {
            var svc = new BulkWriteService(new StubDeviceService(), new StubVariableService());
            IReadOnlyList<BulkWriteResult> results = await svc.WriteToAllDevicesAsync("  ", 1);
            Assert.AreEqual(0, results.Count);
        }
    }
}
