using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using CommunicationDebuggingTools.Contracts.V1;
using CommunicationDebuggingTools.Core.Enums;
using CommunicationDebuggingTools.Core.Interfaces;
using CommunicationDebuggingTools.Core.Models;

namespace CommunicationDebuggingTools.Client {

    /// <summary>
    /// IDeviceService 的 gRPC 代理实现（Remote 模式）。
    /// Devices 集合由后台 WatchDevices 流维护，UI 可直接绑定。
    /// </summary>
    public sealed class RemoteDeviceService : IDeviceService, IDisposable {

        private readonly Engine.EngineClient _client;
        private readonly SynchronizationContext _ui;
        private CancellationTokenSource _watchCts;

        public ObservableCollection<DeviceInfo> Devices { get; } =
            new ObservableCollection<DeviceInfo>();

        public RemoteDeviceService (EngineHostChannel channel, SynchronizationContext ui = null) {
            _client = channel.Client;
            _ui     = ui ?? SynchronizationContext.Current;
        }

        // ── 生命周期 ──────────────────────────────────

        /// <summary>启动 WatchDevices 后台流，保持 Devices 集合实时。</summary>
        public void StopWatch () {
            _watchCts?.Cancel();
        }

        public void StartWatch () {
            _watchCts?.Cancel();
            _watchCts = new CancellationTokenSource();
            Task.Run(() => WatchLoopAsync(_watchCts.Token));
        }

        private async Task WatchLoopAsync (CancellationToken ct) {
            try {
                using var call = _client.WatchDevices(new Empty(), cancellationToken: ct);
                while (await call.ResponseStream.MoveNext(ct).ConfigureAwait(false)) {
                    DeviceEvent evt = call.ResponseStream.Current;
                    Post(() => ApplyDeviceEvent(evt));
                }
            } catch (OperationCanceledException) { }
            catch { /* 重连逻辑可在此加 */ }
        }

        private void ApplyDeviceEvent (DeviceEvent evt) {
            switch (evt.EventKind) {
                case "snapshot":
                case "status_changed": {
                    DeviceInfo existing = Devices.FirstOrDefault(d => d.Id == evt.DeviceId);
                    if (existing != null) MergeInto(existing, evt.Device);
                    else                 Devices.Add(FromDto(evt.Device));
                    break;
                }
                case "added":
                    if (!Devices.Any(d => d.Id == evt.DeviceId))
                        Devices.Add(FromDto(evt.Device));
                    break;
                case "removed": {
                    DeviceInfo d = Devices.FirstOrDefault(x => x.Id == evt.DeviceId);
                    if (d != null) Devices.Remove(d);
                    break;
                }
            }
        }

        // ── IDeviceService 实现 ───────────────────────

        public void Load () => RefreshDevices();

        public void Save () { /* 远端无需客户端 Save */ }

        public void Add (DeviceInfo device) {
            var dto = ToDto(device);
            dto.Id = "";  // 让服务端生成 Id
            var resp = _client.UpsertDevice(new UpsertDeviceRequest { Device = dto });
            if (resp?.Device != null && !string.IsNullOrEmpty(resp.Device.Id))
                device.Id = resp.Device.Id;
        }

        public void Update (DeviceInfo device) =>
            _client.UpsertDevice(new UpsertDeviceRequest { Device = ToDto(device) });

        public void Remove (string id) =>
            _client.DeleteDevice(new DeleteDeviceRequest { Id = id });

        public async Task<bool> ConnectAsync (string id, CancellationToken ct) {
            var resp = await _client.ConnectAsync(new ConnectRequest { Id = id },
                cancellationToken: ct).ConfigureAwait(false);
            return resp?.Result?.Ok == true;
        }

        public void Disconnect (string id) =>
            _client.Disconnect(new DisconnectRequest { Id = id });

        public void DisconnectAll () =>
            _client.DisconnectAll(new DisconnectAllRequest());

        public IProtocol GetProtocol (string deviceId) => null; // 远端无本地会话

        public void CheckConnections () { /* 远端心跳由 Host 自己管理 */ }

        public void ReportCommSuccess (string deviceId) { }
        public void ReportCommError   (string deviceId) { }

        // ── 辅助 ─────────────────────────────────────

        private void RefreshDevices () {
            try {
                var resp = _client.ListDevices(new ListDevicesRequest());
                if (resp == null) return;
                Devices.Clear();
                foreach (DeviceDto dto in resp.Devices)
                    Devices.Add(FromDto(dto));
            } catch (Exception) {
                // Host 未启动或网络不可达：保持空列表，不抛到 UI 线程导致闪退
                Devices.Clear();
            }
        }

        private void Post (Action action) {
            if (_ui != null) _ui.Post(_ => action(), null);
            else             action();
        }

        private static DeviceInfo FromDto (DeviceDto dto) {
            if (dto == null) return new DeviceInfo();
            var d = new DeviceInfo {
                Id       = dto.Id   ?? "",
                Name     = dto.Name ?? "",
                Model    = dto.Model ?? "",
                Protocol = dto.Protocol ?? "",
                Ip       = dto.Ip  ?? "",
                Port     = dto.Port,
                StationNo = dto.StationNo,
                ExtraSettingsJson = dto.ExtraSettingsJson ?? "{}",
                IsConnected = dto.IsConnected
            };
            if (Enum.TryParse(dto.StatusType, true, out DeviceStatusType st)) d.StatusType = st;
            if (Enum.TryParse(dto.ByteOrder, true, out ByteOrder bo)) d.ByteOrder = bo;
            if (Enum.TryParse(dto.WordOrder, true, out WordOrder wo)) d.WordOrder = wo;
            if (Enum.TryParse(dto.Lane, true, out LaneType lane)) d.Lane = lane;
            if (Enum.TryParse(dto.StringEncoding, true, out StringEncodingKind se)) d.StringEncoding = se;
            return d;
        }

        private static void MergeInto (DeviceInfo d, DeviceDto dto) {
            if (dto == null) return;
            d.IsConnected = dto.IsConnected;
            if (Enum.TryParse(dto.StatusType, true, out DeviceStatusType st)) d.StatusType = st;
        }

        private static DeviceDto ToDto (DeviceInfo d) {
            return new DeviceDto {
                Id = d.Id ?? "", Name = d.Name ?? "", Model = d.Model ?? "",
                Protocol = d.Protocol ?? "", Ip = d.Ip ?? "",
                Port = d.Port, StationNo = d.StationNo,
                ExtraSettingsJson = d.ExtraSettingsJson ?? "{}",
                ByteOrder = d.ByteOrder.ToString(), WordOrder = d.WordOrder.ToString(),
                Lane = d.Lane.ToString(), StringEncoding = d.StringEncoding.ToString()
            };
        }

        public void Dispose () {
            _watchCts?.Cancel();
            _watchCts?.Dispose();
            _watchCts = null;
        }
    }
}
