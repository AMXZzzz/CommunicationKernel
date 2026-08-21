using System.Threading;
using System.Threading.Tasks;
using CommunicationDebuggingTools.Business.Device;
using CommunicationDebuggingTools.Business.Variable;
using CommunicationDebuggingTools.Core.Enums;
using CommunicationDebuggingTools.Core.Models;
using CommunicationDebuggingTools.Tests.Fakes;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace CommunicationDebuggingTools.Tests {

    /// <summary>
    /// VariableService 读写单测（不连真实 PLC）。
    /// 通过 AttachSessionForTest 绕过 TcpProbe，直接挂 FakeProtocol 会话。
    /// </summary>
    [TestClass]
    public class VariableServiceReadWriteTests {

        private FakeProtocol    _protocol;
        private DeviceService   _devices;
        private VariableService _variables;
        private DeviceInfo      _device;
        private VariableItem    _variable;

        [TestInitialize]
        public void Setup () {
            _protocol = new FakeProtocol {
                ConnectResult = true,
                IsConnected = true,   // 绕过 ConnectAsync，直接预置为已连接
                ReadResult = true,
                ReadValue = (short)55,
                WriteResult = true
            };

            _devices = new DeviceService(
                new FakeProtocolResolver { ProtocolToReturn = _protocol },
                new FakeDeviceRepository());

            _device = new DeviceInfo {
                Name = "PLC1",
                Protocol = "Modbus TCP",
                Ip = "127.0.0.1",
                Port = 502,
                StationNo = 1
            };
            _devices.Add(_device);

            // 不走 ConnectAsync（避免 TcpProbe）：直接标记已连接并注入会话
            _devices.AttachSessionForTest(_device.Id, _protocol);

            _variables = new VariableService(_devices, new FakeVariableRepository());

            _variable = new VariableItem {
                DeviceId = _device.Id,
                Name = "V1",
                Address = "40001",
                DataType = VariableDataType.Int16,
                Access = VariableAccess.ReadWrite
            };
            _variables.Add(_variable);
        }

        // ── 读 ─────────────────────────────────────────────────

        [TestMethod]
        public async Task ReadAsync_WhenOk_ReturnsSuccess_AndFillsLastValue () {
            OperationResult result = await _variables.ReadAsync(_variable.Id, CancellationToken.None);

            Assert.IsTrue(result.Success);
            Assert.AreEqual(OperationErrorCode.None, result.ErrorCode);
            Assert.AreEqual((short)55, _variable.LastValue);
            Assert.AreEqual(DataQuality.Good, _variable.Quality);
            Assert.AreEqual(1, _protocol.ReadCallCount);
            Assert.AreEqual("40001", _protocol.LastReadRequest.Address);
        }

        [TestMethod]
        public async Task ReadAsync_WhenWriteOnly_ReturnsFail_AccessDenied () {
            _variable.Access = VariableAccess.WriteOnly;

            OperationResult result = await _variables.ReadAsync(_variable.Id, CancellationToken.None);

            Assert.IsFalse(result.Success);
            Assert.AreEqual(OperationErrorCode.AccessDenied, result.ErrorCode);
            Assert.AreEqual("只写变量不可读", _variable.LastError);
            Assert.AreEqual(0, _protocol.ReadCallCount);   // 未发出请求
        }

        [TestMethod]
        public async Task ReadAsync_WhenProtocolFails_ReturnsFail_ProtocolError () {
            _protocol.ReadResult = false;

            OperationResult result = await _variables.ReadAsync(_variable.Id, CancellationToken.None);

            Assert.IsFalse(result.Success);
            Assert.AreEqual(OperationErrorCode.ProtocolError, result.ErrorCode);
            Assert.IsFalse(string.IsNullOrWhiteSpace(result.ErrorMessage));
            Assert.AreEqual(DataQuality.Bad, _variable.Quality);
        }

        [TestMethod]
        public async Task ReadAsync_WhenDeviceDisconnected_ReturnsFail_DeviceNotConnected () {
            _devices.Disconnect(_device.Id);

            OperationResult result = await _variables.ReadAsync(_variable.Id, CancellationToken.None);

            Assert.IsFalse(result.Success);
            Assert.AreEqual(OperationErrorCode.DeviceNotConnected, result.ErrorCode);
            Assert.AreEqual(0, _protocol.ReadCallCount);   // 未发出请求
        }

        [TestMethod]
        public async Task ReadAsync_WhenVariableNotFound_ReturnsFail_VariableNotFound () {
            OperationResult result =
                await _variables.ReadAsync("non_existent_id", CancellationToken.None);

            Assert.IsFalse(result.Success);
            Assert.AreEqual(OperationErrorCode.VariableNotFound, result.ErrorCode);
        }

        // ── 写 ─────────────────────────────────────────────────

        [TestMethod]
        public async Task WriteAsync_WhenOk_ReturnsSuccess_AndUpdatesLastValue () {
            OperationResult result =
                await _variables.WriteAsync(_variable.Id, (short)99, CancellationToken.None);

            Assert.IsTrue(result.Success);
            Assert.AreEqual(OperationErrorCode.None, result.ErrorCode);
            Assert.AreEqual((short)99, _variable.LastValue);
            Assert.AreEqual(DataQuality.Good, _variable.Quality);
            Assert.AreEqual(1, _protocol.WriteCallCount);
            Assert.AreEqual((short)99, _protocol.LastWriteRequest.Value);
        }

        [TestMethod]
        public async Task WriteAsync_WhenReadOnly_ReturnsFail_AccessDenied () {
            _variable.Access = VariableAccess.ReadOnly;

            OperationResult result =
                await _variables.WriteAsync(_variable.Id, (short)1, CancellationToken.None);

            Assert.IsFalse(result.Success);
            Assert.AreEqual(OperationErrorCode.AccessDenied, result.ErrorCode);
            Assert.AreEqual("只读变量不可写", _variable.LastError);
            Assert.AreEqual(0, _protocol.WriteCallCount);
        }

        [TestMethod]
        public async Task WriteAsync_WhenProtocolFails_ReturnsFail_ProtocolError () {
            _protocol.WriteResult = false;

            OperationResult result =
                await _variables.WriteAsync(_variable.Id, (short)5, CancellationToken.None);

            Assert.IsFalse(result.Success);
            Assert.AreEqual(OperationErrorCode.ProtocolError, result.ErrorCode);
            Assert.IsFalse(string.IsNullOrWhiteSpace(result.ErrorMessage));
        }

        [TestMethod]
        public async Task WriteAsync_WhenDeviceDisconnected_ReturnsFail_DeviceNotConnected () {
            _devices.Disconnect(_device.Id);

            OperationResult result =
                await _variables.WriteAsync(_variable.Id, (short)1, CancellationToken.None);

            Assert.IsFalse(result.Success);
            Assert.AreEqual(OperationErrorCode.DeviceNotConnected, result.ErrorCode);
            Assert.AreEqual(0, _protocol.WriteCallCount);
        }

        // ── LastError 同步写入（供 UI 绑定使用）─────────────────

        [TestMethod]
        public async Task ReadAsync_WhenFails_VariableLastError_IsPopulated () {
            _protocol.ReadResult = false;

            await _variables.ReadAsync(_variable.Id, CancellationToken.None);

            // VariableItem.LastError 仍同步更新，UI 绑定可直接显示
            Assert.IsFalse(string.IsNullOrWhiteSpace(_variable.LastError));
        }

        [TestMethod]
        public async Task WriteAsync_WhenOk_VariableLastError_IsCleared () {
            // 先让一次读失败，产生 LastError
            _protocol.ReadResult = false;
            await _variables.ReadAsync(_variable.Id, CancellationToken.None);
            Assert.IsFalse(string.IsNullOrWhiteSpace(_variable.LastError));

            // 写成功后 LastError 不再是失败信息（被成功结果覆盖）
            _protocol.ReadResult = true;
            _protocol.WriteResult = true;
            await _variables.WriteAsync(_variable.Id, (short)1, CancellationToken.None);

            Assert.AreEqual(string.Empty, _variable.LastError);
        }
    }
}