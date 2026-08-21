using System;
using System.Collections.Specialized;
using System.ComponentModel;
using System.Linq;
using System.Threading;
using System.Threading.Channels;
using System.Threading.Tasks;
using CommunicationDebuggingTools.Contracts.V1;
using CommunicationDebuggingTools.Core.Enums;
using CommunicationDebuggingTools.Core.Interfaces;
using CommunicationDebuggingTools.Core.Models;
using Grpc.Core;

namespace CommunicationDebuggingTools.EngineHost.Services {

    public sealed class EngineGrpcService : Engine.EngineBase {

        private readonly IDeviceService    _devices;
        private readonly IVariableService  _variables;
        private readonly IProtocolResolver _protocols;
        private readonly IPollingEngine    _polling;

        public EngineGrpcService(IDeviceService devices, IVariableService variables,
            IProtocolResolver protocols, IPollingEngine polling) {
            _devices   = devices;
            _variables = variables;
            _protocols = protocols;
            _polling   = polling;
        }

        // ── Health / Protocols ──────────────────────────────

        public override Task<HealthResponse> Health(HealthRequest req, ServerCallContext ctx) {
            return Task.FromResult(new HealthResponse {
                Ok = true, Version = "0.2.0-host",
                DeviceCount          = _devices?.Devices?.Count ?? 0,
                VariableCount        = _variables?.Variables?.Count ?? 0,
                ConnectedDeviceCount = _devices?.Devices?.Count(d => d?.IsConnected == true) ?? 0
            });
        }

        public override Task<ListProtocolsResponse> ListProtocols(ListProtocolsRequest req, ServerCallContext ctx) {
            var resp = new ListProtocolsResponse();
            foreach (var n in _protocols?.GetProtocolNames() ?? Array.Empty<string>())
                if (!string.IsNullOrWhiteSpace(n)) resp.ProtocolNames.Add(n);
            return Task.FromResult(resp);
        }

        // ── Device CRUD ──────────────────────────────────────

        public override Task<ListDevicesResponse> ListDevices(ListDevicesRequest req, ServerCallContext ctx) {
            var resp = new ListDevicesResponse();
            if (_devices?.Devices == null) return Task.FromResult(resp);
            foreach (var d in _devices.Devices) if (d != null) resp.Devices.Add(ToDto(d));
            return Task.FromResult(resp);
        }

        public override Task<UpsertDeviceResponse> UpsertDevice(UpsertDeviceRequest req, ServerCallContext ctx) {
            var resp = new UpsertDeviceResponse();
            if (req?.Device == null) { resp.Result = Fail("device 为空","INVALID_ARGUMENT"); return Task.FromResult(resp); }
            try {
                var info = FromDto(req.Device);
                bool isNew = string.IsNullOrWhiteSpace(req.Device.Id);
                if (isNew) { if (string.IsNullOrWhiteSpace(info.Id)) info.Id = Guid.NewGuid().ToString("N"); _devices.Add(info); }
                else _devices.Update(info);
                var saved = _devices.Devices.FirstOrDefault(x => x?.Id == info.Id);
                resp.Device = ToDto(saved ?? info); resp.Result = Ok();
            } catch (Exception ex) { resp.Result = Fail(ex.Message,"UPSERT_FAILED"); }
            return Task.FromResult(resp);
        }

        public override Task<DeleteDeviceResponse> DeleteDevice(DeleteDeviceRequest req, ServerCallContext ctx) {
            var resp = new DeleteDeviceResponse();
            if (string.IsNullOrWhiteSpace(req?.Id)) { resp.Result = Fail("id 为空","INVALID_ARGUMENT"); return Task.FromResult(resp); }
            try { _devices.Remove(req.Id); resp.Result = Ok(); } catch (Exception ex) { resp.Result = Fail(ex.Message,"DELETE_FAILED"); }
            return Task.FromResult(resp);
        }

        public override async Task<ConnectResponse> Connect(ConnectRequest req, ServerCallContext ctx) {
            var resp = new ConnectResponse();
            if (string.IsNullOrWhiteSpace(req?.Id)) { resp.Result = Fail("id 为空","INVALID_ARGUMENT"); return resp; }
            try {
                bool ok = await _devices.ConnectAsync(req.Id, ctx.CancellationToken).ConfigureAwait(false);
                var d = FindDevice(req.Id);
                if (d != null) resp.Device = ToDto(d);
                resp.Result = ok ? Ok() : Fail(d?.StatusText ?? "连接失败","CONNECT_FAILED");
            } catch (Exception ex) { resp.Result = Fail(ex.Message,"CONNECT_FAILED"); var d = FindDevice(req.Id); if (d != null) resp.Device = ToDto(d); }
            return resp;
        }

        public override Task<DisconnectResponse> Disconnect(DisconnectRequest req, ServerCallContext ctx) {
            var resp = new DisconnectResponse();
            if (string.IsNullOrWhiteSpace(req?.Id)) { resp.Result = Fail("id 为空","INVALID_ARGUMENT"); return Task.FromResult(resp); }
            try { _devices.Disconnect(req.Id); var d = FindDevice(req.Id); if (d != null) resp.Device = ToDto(d); resp.Result = Ok(); }
            catch (Exception ex) { resp.Result = Fail(ex.Message,"DISCONNECT_FAILED"); }
            return Task.FromResult(resp);
        }

        public override Task<DisconnectAllResponse> DisconnectAll(DisconnectAllRequest req, ServerCallContext ctx) {
            var resp = new DisconnectAllResponse();
            try { _devices.DisconnectAll(); resp.Result = Ok(); } catch (Exception ex) { resp.Result = Fail(ex.Message,"DISCONNECT_ALL_FAILED"); }
            return Task.FromResult(resp);
        }

        // ── Variable CRUD ─────────────────────────────────────

        public override Task<ListVariablesResponse> ListVariables(ListVariablesRequest req, ServerCallContext ctx) {
            var resp = new ListVariablesResponse();
            if (_variables?.Variables == null) return Task.FromResult(resp);
            string did = req?.DeviceId;
            foreach (var v in _variables.Variables) {
                if (v == null) continue;
                if (!string.IsNullOrEmpty(did) && !string.Equals(v.DeviceId, did, StringComparison.Ordinal)) continue;
                resp.Variables.Add(ToDto(v));
            }
            return Task.FromResult(resp);
        }

        public override Task<UpsertVariableResponse> UpsertVariable(UpsertVariableRequest req, ServerCallContext ctx) {
            var resp = new UpsertVariableResponse();
            if (req?.Variable == null) { resp.Result = Fail("variable 为空","INVALID_ARGUMENT"); return Task.FromResult(resp); }
            try {
                var item = FromDto(req.Variable);
                bool isNew = string.IsNullOrWhiteSpace(req.Variable.Id);
                if (isNew) { if (string.IsNullOrWhiteSpace(item.Id)) item.Id = Guid.NewGuid().ToString("N"); _variables.Add(item); }
                else _variables.Update(item);
                var saved = _variables.Variables.FirstOrDefault(x => x?.Id == item.Id);
                resp.Variable = ToDto(saved ?? item); resp.Result = Ok();
            } catch (Exception ex) { resp.Result = Fail(ex.Message,"UPSERT_FAILED"); }
            return Task.FromResult(resp);
        }

        public override Task<DeleteVariableResponse> DeleteVariable(DeleteVariableRequest req, ServerCallContext ctx) {
            var resp = new DeleteVariableResponse();
            if (string.IsNullOrWhiteSpace(req?.Id)) { resp.Result = Fail("id 为空","INVALID_ARGUMENT"); return Task.FromResult(resp); }
            try { _variables.Remove(req.Id); resp.Result = Ok(); } catch (Exception ex) { resp.Result = Fail(ex.Message,"DELETE_FAILED"); }
            return Task.FromResult(resp);
        }

        public override async Task<ReadVariableResponse> ReadVariable(ReadVariableRequest req, ServerCallContext ctx) {
            var resp = new ReadVariableResponse();
            if (string.IsNullOrWhiteSpace(req?.Id)) { resp.Result = Fail("id 为空","INVALID_ARGUMENT"); return resp; }
            try {
                var op = await _variables.ReadAsync(req.Id, ctx.CancellationToken).ConfigureAwait(false);
                var v = FindVariable(req.Id); if (v != null) resp.Variable = ToDto(v);
                resp.Result = op?.Success == true ? Ok() : Fail(op?.ErrorMessage ?? "读失败", op?.ErrorCode.ToString() ?? "READ_FAILED");
            } catch (Exception ex) { resp.Result = Fail(ex.Message,"READ_FAILED"); }
            return resp;
        }

        public override async Task<WriteVariableResponse> WriteVariable(WriteVariableRequest req, ServerCallContext ctx) {
            var resp = new WriteVariableResponse();
            if (string.IsNullOrWhiteSpace(req?.Id)) { resp.Result = Fail("id 为空","INVALID_ARGUMENT"); return resp; }
            try {
                var op = await _variables.WriteAsync(req.Id, req.Value ?? "", ctx.CancellationToken).ConfigureAwait(false);
                var v = FindVariable(req.Id); if (v != null) resp.Variable = ToDto(v);
                resp.Result = op?.Success == true ? Ok() : Fail(op?.ErrorMessage ?? "写失败", op?.ErrorCode.ToString() ?? "WRITE_FAILED");
            } catch (Exception ex) { resp.Result = Fail(ex.Message,"WRITE_FAILED"); }
            return resp;
        }

        // ── Watch 实时流 ──────────────────────────────────────

        /// <summary>
        /// 设备状态推流。
        /// 先推当前快照（event_kind="snapshot"），之后推增量（status_changed / added / removed）。
        /// Channel 做线程安全桥：PropertyChanged / CollectionChanged（任意线程）→ Channel.Writer；
        /// gRPC 流循环从 Channel.Reader 消费，WriteAsync 给客户端。
        /// </summary>
        public override async Task WatchDevices(Empty req,
            IServerStreamWriter<DeviceEvent> stream, ServerCallContext ctx) {

            var ct = ctx.CancellationToken;
            var ch = Channel.CreateUnbounded<DeviceEvent>(new UnboundedChannelOptions { SingleReader = true });

            PropertyChangedEventHandler propH = (sender, e) => {
                if (sender is DeviceInfo d &&
                    (e.PropertyName == nameof(DeviceInfo.IsConnected) ||
                     e.PropertyName == nameof(DeviceInfo.StatusType)))
                    ch.Writer.TryWrite(new DeviceEvent { DeviceId = d.Id ?? "", Device = ToDto(d), EventKind = "status_changed" });
            };

            NotifyCollectionChangedEventHandler collH = (sender, e) => {
                if (e.NewItems != null)
                    foreach (DeviceInfo d in e.NewItems) { if (d == null) continue; d.PropertyChanged += propH; ch.Writer.TryWrite(new DeviceEvent { DeviceId = d.Id ?? "", Device = ToDto(d), EventKind = "added" }); }
                if (e.OldItems != null)
                    foreach (DeviceInfo d in e.OldItems) { if (d == null) continue; d.PropertyChanged -= propH; ch.Writer.TryWrite(new DeviceEvent { DeviceId = d.Id ?? "", Device = ToDto(d), EventKind = "removed" }); }
            };

            foreach (var d in _devices.Devices.ToList()) d.PropertyChanged += propH;
            _devices.Devices.CollectionChanged += collH;

            try {
                // 全量快照
                foreach (var d in _devices.Devices.ToList()) {
                    if (ct.IsCancellationRequested) break;
                    await stream.WriteAsync(new DeviceEvent { DeviceId = d.Id ?? "", Device = ToDto(d), EventKind = "snapshot" }, ct).ConfigureAwait(false);
                }
                // 增量流
                await foreach (var evt in ch.Reader.ReadAllAsync(ct).ConfigureAwait(false))
                    await stream.WriteAsync(evt, ct).ConfigureAwait(false);
            } catch (OperationCanceledException) {
                // 客户端正常断开
            } finally {
                ch.Writer.TryComplete();
                _devices.Devices.CollectionChanged -= collH;
                foreach (var d in _devices.Devices.ToList()) d.PropertyChanged -= propH;
            }
        }

        /// <summary>
        /// 变量值推流。
        /// 先推当前快照，之后每次 PollingEngine.CycleCompleted 触发时推送对应变量最新值。
        /// </summary>
        public override async Task WatchVariables(Empty req,
            IServerStreamWriter<VariableValueEvent> stream, ServerCallContext ctx) {

            var ct = ctx.CancellationToken;
            var ch = Channel.CreateUnbounded<VariableValueEvent>(new UnboundedChannelOptions { SingleReader = true });

            Action<string, bool> cycleH = (variableId, _) => {
                var v = FindVariable(variableId);
                if (v != null) ch.Writer.TryWrite(MakeVarEvent(v));
            };

            _polling.CycleCompleted += cycleH;
            try {
                // 全量快照
                foreach (var v in _variables.Variables.ToList()) {
                    if (v == null || ct.IsCancellationRequested) break;
                    await stream.WriteAsync(MakeVarEvent(v), ct).ConfigureAwait(false);
                }
                // 增量流
                await foreach (var evt in ch.Reader.ReadAllAsync(ct).ConfigureAwait(false))
                    await stream.WriteAsync(evt, ct).ConfigureAwait(false);
            } catch (OperationCanceledException) {
            } finally {
                ch.Writer.TryComplete();
                _polling.CycleCompleted -= cycleH;
            }
        }

        // ── 映射 / 辅助 ──────────────────────────────────────

        private DeviceInfo   FindDevice(string id) => _devices?.Devices?.FirstOrDefault(x => x?.Id == id);
        private VariableItem FindVariable(string id) => _variables?.Variables?.FirstOrDefault(x => x?.Id == id);

        private static VariableValueEvent MakeVarEvent(VariableItem v) =>
            new VariableValueEvent {
                VariableId = v.Id ?? "", DeviceId = v.DeviceId ?? "",
                LastValue = v.LastValue != null ? Convert.ToString(v.LastValue) : "",
                Quality = v.Quality.ToString(), LastError = v.LastError ?? ""
            };

        private static RpcResult Ok() =>
            new RpcResult { Ok = true, Message = "", ErrorCode = "" };
        private static RpcResult Fail(string msg, string code) =>
            new RpcResult { Ok = false, Message = msg ?? "", ErrorCode = code ?? "UNKNOWN" };

        private static DeviceDto ToDto(DeviceInfo d) {
            if (d == null) return new DeviceDto();
            return new DeviceDto {
                Id = d.Id ?? "", Name = d.Name ?? "", Model = d.Model ?? "", Protocol = d.Protocol ?? "",
                Ip = d.Ip ?? "", Port = d.Port, StationNo = d.StationNo,
                ExtraSettingsJson = string.IsNullOrWhiteSpace(d.ExtraSettingsJson) ? "{}" : d.ExtraSettingsJson,
                ByteOrder = d.ByteOrder.ToString(), WordOrder = d.WordOrder.ToString(),
                StringEncoding = d.StringEncoding.ToString(), Lane = d.Lane.ToString(),
                IsConnected = d.IsConnected, StatusType = d.StatusType.ToString(), StatusText = d.StatusText ?? ""
            };
        }

        private static DeviceInfo FromDto(DeviceDto dto) {
            var d = new DeviceInfo();
            if (!string.IsNullOrWhiteSpace(dto.Id)) d.Id = dto.Id.Trim();
            d.Name = dto.Name ?? ""; d.Model = dto.Model ?? ""; d.Protocol = dto.Protocol ?? "";
            d.Ip = dto.Ip ?? ""; d.Port = dto.Port > 0 ? dto.Port : 502; d.StationNo = dto.StationNo;
            d.ExtraSettingsJson = string.IsNullOrWhiteSpace(dto.ExtraSettingsJson) ? "{}" : dto.ExtraSettingsJson;
            LaneType lane; if (Enum.TryParse(dto.Lane, true, out lane)) d.Lane = lane;
            ByteOrder bo; if (Enum.TryParse(dto.ByteOrder, true, out bo)) d.ByteOrder = bo;
            WordOrder wo; if (Enum.TryParse(dto.WordOrder, true, out wo)) d.WordOrder = wo;
            StringEncodingKind se; if (Enum.TryParse(dto.StringEncoding, true, out se)) d.StringEncoding = se;
            return d;
        }

        private static VariableDto ToDto(VariableItem v) {
            if (v == null) return new VariableDto();
            return new VariableDto {
                Id = v.Id ?? "", DeviceId = v.DeviceId ?? "", Name = v.Name ?? "", Address = v.Address ?? "",
                DataType = v.DataType.ToString(), Access = v.Access.ToString(), Length = v.Length,
                Unit = v.Unit ?? "", Category = v.Category ?? "", Description = v.Description ?? "",
                LastValue = v.LastValue != null ? Convert.ToString(v.LastValue) : "",
                Quality = v.Quality.ToString(), LastError = v.LastError ?? ""
            };
        }

        private static VariableItem FromDto(VariableDto dto) {
            var v = new VariableItem();
            if (!string.IsNullOrWhiteSpace(dto.Id)) v.Id = dto.Id.Trim();
            v.DeviceId = dto.DeviceId ?? ""; v.Name = dto.Name ?? ""; v.Address = dto.Address ?? "";
            v.Length = dto.Length; v.Unit = dto.Unit ?? "";
            v.Category = string.IsNullOrWhiteSpace(dto.Category) ? "状态点" : dto.Category;
            v.Description = dto.Description ?? "";
            VariableDataType dt; if (Enum.TryParse(dto.DataType, true, out dt)) v.DataType = dt;
            VariableAccess ac; if (Enum.TryParse(dto.Access, true, out ac)) v.Access = ac;
            return v;
        }
    }
}
