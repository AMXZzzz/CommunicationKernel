using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using CommunicationDebuggingTools.Core.Enums;
using CommunicationDebuggingTools.Core.Interfaces;
using CommunicationDebuggingTools.Core.Logging;
using CommunicationDebuggingTools.Core.Models;

namespace CommunicationDebuggingTools.Business.Variable {

    /// <summary>
    /// 变量业务：配置持久化 + 通过设备已建立的 IProtocol 会话读写。
    /// 不解析 Address，不解释协议；只组装 ProtocolDataMessage 并调用插件。
    /// </summary>
    public class VariableService : IVariableService {

        private readonly IDeviceService    _devices;
        private readonly IVariableRepository _repository;
        private readonly IAppLogger         _log;

        public ObservableCollection<VariableItem> Variables { get; private set; }

        /// <summary>UI 同步上下文（构造时在 UI 线程捕获），用于回写 LastValue。</summary>
        private readonly SynchronizationContext _uiCtx;

        public VariableService (
            IDeviceService devices,
            IVariableRepository repository,
            IAppLogger logger = null) {
            if (devices == null) throw new ArgumentNullException(nameof(devices));
            if (repository == null) throw new ArgumentNullException(nameof(repository));
            _devices = devices;
            _repository = repository;
            _log = logger;
            Variables = new ObservableCollection<VariableItem>();
            // App 在 UI 线程 BuildServiceProvider，此处可拿到 DispatcherSynchronizationContext
            _uiCtx = SynchronizationContext.Current;
        }

        // ── 持久化 ──────────────────────────────────
        public void Load () {
            Variables.Clear();
            IList<VariableItem> list = _repository.LoadAll();
            if (list != null)
                foreach (VariableItem v in list) Variables.Add(v);
        }

        public void Save () => _repository.SaveAll(Variables.ToList());

        public void Add (VariableItem item) {
            if (item == null) throw new ArgumentNullException(nameof(item));
            Normalize(item);
            ValidateForUpsert(item, null);
            if (string.IsNullOrWhiteSpace(item.Id))
                item.Id = Guid.NewGuid().ToString("N");
            if (Variables.Any(x => x != null && x.Id == item.Id))
                throw new InvalidOperationException("变量 Id 已存在: " + item.Id);
            Variables.Add(item);
            Save();
        }

        public void Update (VariableItem item) {
            if (item == null) throw new ArgumentNullException(nameof(item));
            Normalize(item);
            VariableItem old = FindRequired(item.Id);
            ValidateForUpsert(item, item.Id);
            old.DeviceId = item.DeviceId;
            old.Name = item.Name;
            old.Address = item.Address;
            old.DataType = item.DataType;
            old.Access = item.Access;
            old.Length = item.Length;
            old.Unit = item.Unit;
            old.Category = item.Category;
            old.Description = item.Description;
            Save();
        }

        public void Remove (string id) {
            VariableItem v = Variables.FirstOrDefault(x => x.Id == id);
            if (v == null) return;
            Variables.Remove(v);
            Save();
        }

        // ── 读写 ────────────────────────────────────

        public async Task<OperationResult> ReadAsync (
            string variableId,
            CancellationToken cancellationToken) {

            VariableItem v;
            try { v = FindRequired(variableId); } catch (Exception ex) { return OperationResult.VariableNotFound(variableId + " " + ex.Message); }

            if (v.Access == VariableAccess.WriteOnly) {
                var r = OperationResult.AccessDenied("只写变量不可读");
                SetBad(v, r.ErrorMessage); return r;
            }

            IProtocol protocol; DeviceInfo device; string err;
            if (!TryGetProtocol(v.DeviceId, out protocol, out device, out err)) {
                var code = err.Contains("不存在") ? OperationErrorCode.DeviceNotFound
                                                  : OperationErrorCode.DeviceNotConnected;
                var r = OperationResult.Fail(err, code);
                SetBad(v, r.ErrorMessage);
                LogWarn(err + " — " + v.Name);
                return r;
            }

            ProtocolDataMessage msg = BuildMessage(v, device, null);
            ProtocolDataMessage result;
            try {
                result = await protocol.ReadAsync(msg, cancellationToken);
            } catch (OperationCanceledException) {
                SetBad(v, "已取消");
                return OperationResult.Cancelled;
            } catch (Exception ex) {
                SetBad(v, ex.Message);
                LogError("读异常: " + v.Name + " — " + ex.Message);
                CheckAndMarkDisconnected(v.DeviceId);
                return OperationResult.Fail(ex.Message, OperationErrorCode.ProtocolError);
            }

            if (result.Success) {
                ApplyLive(v, result.Value, result.Quality, result.ErrorMessage ?? string.Empty);
                _devices.ReportCommSuccess(v.DeviceId);
                return OperationResult.Ok;
            }

            SetBad(v, result.ErrorMessage ?? "协议返回失败");
            LogError("读失败: " + v.Name + " — " + result.ErrorMessage);
            _devices.ReportCommError(v.DeviceId);
            CheckAndMarkDisconnected(v.DeviceId);
            return OperationResult.ProtocolError(
                string.IsNullOrWhiteSpace(result.ErrorMessage) ? "协议返回失败" : result.ErrorMessage);
        }

        public async Task<OperationResult> WriteAsync (
            string variableId,
            object value,
            CancellationToken cancellationToken) {

            VariableItem v;
            try { v = FindRequired(variableId); } catch (Exception ex) { return OperationResult.VariableNotFound(variableId + " " + ex.Message); }

            if (v.Access == VariableAccess.ReadOnly) {
                var r = OperationResult.AccessDenied("只读变量不可写");
                SetBad(v, r.ErrorMessage); return r;
            }

            IProtocol protocol; DeviceInfo device; string err;
            if (!TryGetProtocol(v.DeviceId, out protocol, out device, out err)) {
                var code = err.Contains("不存在") ? OperationErrorCode.DeviceNotFound
                                                  : OperationErrorCode.DeviceNotConnected;
                var r = OperationResult.Fail(err, code);
                SetBad(v, r.ErrorMessage);
                LogWarn(err + " — " + v.Name);
                return r;
            }

            ProtocolDataMessage msg = BuildMessage(v, device, value);
            ProtocolDataMessage result;
            try {
                result = await protocol.WriteAsync(msg, cancellationToken);
            } catch (OperationCanceledException) {
                SetBad(v, "已取消");
                return OperationResult.Cancelled;
            } catch (Exception ex) {
                SetBad(v, ex.Message);
                LogError("写异常: " + v.Name + " — " + ex.Message);
                CheckAndMarkDisconnected(v.DeviceId);
                return OperationResult.Fail(ex.Message, OperationErrorCode.ProtocolError);
            }

            if (result.Success) {
                ApplyLive(v, value, DataQuality.Good, string.Empty);
                _devices.ReportCommSuccess(v.DeviceId);
                return OperationResult.Ok;
            }

            SetBad(v, result.ErrorMessage ?? "协议返回失败");
            LogError("写失败: " + v.Name + " — " + result.ErrorMessage);
            _devices.ReportCommError(v.DeviceId);
            CheckAndMarkDisconnected(v.DeviceId);
            return OperationResult.ProtocolError(
                string.IsNullOrWhiteSpace(result.ErrorMessage) ? "协议返回失败" : result.ErrorMessage);
        }

        public async Task ReadByDeviceAsync (string deviceId, CancellationToken cancellationToken) {
            if (string.IsNullOrEmpty(deviceId)) return;
            foreach (VariableItem v in Variables
                .Where(x => x != null && x.DeviceId == deviceId).ToList()) {
                if (v.Access == VariableAccess.WriteOnly) continue;
                await ReadAsync(v.Id, cancellationToken);
            }
        }

        // ── 私有 ────────────────────────────────────

        private bool TryGetProtocol (
            string deviceId,
            out IProtocol protocol,
            out DeviceInfo device,
            out string error) {
            protocol = null; device = null; error = string.Empty;
            device = _devices.Devices.FirstOrDefault(d => d != null && d.Id == deviceId);
            if (device == null) { error = "设备不存在"; return false; }
            if (!device.IsConnected) { error = "设备未连接"; return false; }
            protocol = _devices.GetProtocol(deviceId);
            if (protocol == null) { error = "无协议会话"; return false; }
            if (!protocol.IsConnected) { error = "协议会话已断开"; return false; }
            return true;
        }

        private static ProtocolDataMessage BuildMessage (VariableItem v, DeviceInfo device, object writeValue) =>
            new ProtocolDataMessage {
                Address = v.Address ?? string.Empty,
                DataType = v.DataType,
                Length = v.Length,
                ByteOrder = device.ByteOrder,
                WordOrder = device.WordOrder,
                StringEncoding = device.StringEncoding,
                Value = writeValue
            };

        /// <summary>
        /// 在 UI 线程回写实时字段，保证 WPF 绑定与变量表快照订阅能收到通知。
        /// </summary>
        private void ApplyLive (VariableItem v, object lastValue, DataQuality quality, string lastError) {
            if (v == null) return;
            Action apply = () => {
                v.LastValue = lastValue;
                v.Quality = quality;
                v.LastError = lastError ?? string.Empty;
            };
            if (_uiCtx != null)
                _uiCtx.Post(_ => apply(), null);
            else
                apply();
        }

        private void SetBad (VariableItem v, string msg) {
            ApplyLive(v, v != null ? v.LastValue : null, DataQuality.Bad, msg ?? string.Empty);
        }

        private void CheckAndMarkDisconnected (string deviceId) {
            try {
                IProtocol p = _devices.GetProtocol(deviceId);
                if (p != null && !p.IsConnected)
                    _devices.Disconnect(deviceId);
            } catch (Exception ex) {
                // 清理路径：记录即可，避免二次异常冲掉主流程错误
                LogWarn("检查断线失败: " + deviceId + " — " + ex.Message);
            }
        }

        private void ValidateForUpsert (VariableItem item, string currentId) {
            if (string.IsNullOrWhiteSpace(item.DeviceId))
                throw new ArgumentException("设备 Id 不能为空", nameof(item));
            if (string.IsNullOrWhiteSpace(item.Name))
                throw new ArgumentException("变量名称不能为空", nameof(item));
            if (string.IsNullOrWhiteSpace(item.Address))
                throw new ArgumentException("变量地址不能为空", nameof(item));
            bool hasDevice = _devices.Devices.Any(d => d != null && d.Id == item.DeviceId);
            if (!hasDevice)
                throw new InvalidOperationException("设备不存在: " + item.DeviceId);
            bool dup = Variables.Any(v =>
                v != null &&
                !string.Equals(v.Id, currentId, StringComparison.Ordinal) &&
                string.Equals(v.DeviceId, item.DeviceId, StringComparison.Ordinal) &&
                string.Equals(v.Address, item.Address, StringComparison.OrdinalIgnoreCase));
            if (dup)
                throw new InvalidOperationException("同设备下变量地址重复: " + item.Address);
        }

        private static void Normalize (VariableItem item) {
            item.Id = (item.Id ?? string.Empty).Trim();
            item.DeviceId = (item.DeviceId ?? string.Empty).Trim();
            item.Name = (item.Name ?? string.Empty).Trim();
            item.Address = (item.Address ?? string.Empty).Trim();
            item.Unit = (item.Unit ?? string.Empty).Trim();
            item.Category = string.IsNullOrWhiteSpace(item.Category) ? "状态点" : item.Category.Trim();
            item.Description = (item.Description ?? string.Empty).Trim();
        }

        private VariableItem FindRequired (string id) {
            if (string.IsNullOrWhiteSpace(id)) throw new ArgumentException("Id 不能为空", nameof(id));
            VariableItem v = Variables.FirstOrDefault(x => x != null && x.Id == id);
            if (v == null) throw new InvalidOperationException("变量不存在: " + id);
            return v;
        }

        private void LogWarn (string msg) => _log?.Warn("Variable", msg);
        private void LogError (string msg) => _log?.Error("Variable", msg);
    }
}