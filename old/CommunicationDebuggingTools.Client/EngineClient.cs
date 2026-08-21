using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using CommunicationDebuggingTools.Contracts.V1;
using CommunicationDebuggingTools.Core.Interfaces;

namespace CommunicationDebuggingTools.Client {

    /// <summary>
    /// EngineHost gRPC 客户端 SDK 的唯一入口。
    ///
    /// 使用方式（各平台统一）：
    ///   var client = EngineClient.Connect("http://192.168.1.10:5100");
    ///   client.StartWatch();                 // 订阅实时流
    ///   client.Devices.ConnectAsync(id, ct); // 通过 IDeviceService 操作
    ///   client.Variables.ReadAsync(id, ct);  // 通过 IVariableService 操作
    ///   client.Dispose();                    // 断开并释放资源
    ///
    /// 业务升级只需替换 CommunicationDebuggingTools.Client.dll，UI 代码不变。
    /// </summary>
    public sealed class EngineClient : IDisposable {

        private readonly EngineHostChannel    _channel;
        private readonly RemoteDeviceService  _deviceSvc;
        private readonly RemoteVariableService _variableSvc;
        private bool _disposed;

        // ── 公开接口（UI 只依赖 Core 接口，不依赖 gRPC）──
        public IDeviceService   Devices   => _deviceSvc;
        public IVariableService Variables => _variableSvc;

        /// <summary>当前连接的 EngineHost 地址。</summary>
        public string Address => _channel.Address;

        // ── 工厂 ────────────────────────────────────────

        /// <summary>
        /// 建立到 EngineHost 的连接并返回 SDK 实例。
        /// 不抛出：连接失败在第一次实际调用时才报错（懒连接）。
        /// </summary>
        public static EngineClient Connect (
            string address,
            SynchronizationContext uiContext = null) {

            if (string.IsNullOrWhiteSpace(address))
                address = "http://127.0.0.1:5100";

            var channel = new EngineHostChannel();
            channel.Open(address);

            return new EngineClient(channel, uiContext ?? SynchronizationContext.Current);
        }

        private EngineClient (EngineHostChannel channel, SynchronizationContext ui) {
            _channel     = channel;
            _deviceSvc   = new RemoteDeviceService (channel, ui);
            _variableSvc = new RemoteVariableService(channel, ui);
        }

        // ── 生命周期 ─────────────────────────────────────

        /// <summary>
        /// 启动 WatchDevices / WatchVariables 后台流，集合自动实时更新。
        /// 应在 UI 线程调用（传入的 uiContext 来自此时的 SynchronizationContext）。
        /// </summary>
        public void StartWatch () {
            ThrowIfDisposed();
            _deviceSvc.StartWatch();
            _variableSvc.StartWatch();
        }

        /// <summary>停止后台流（通常在应用退出前调用）。</summary>
        public void StopWatch () {
            _deviceSvc.StopWatch();
            _variableSvc.StopWatch();
        }

        /// <summary>向 EngineHost 发送健康探针，返回是否可达。</summary>
        public Task<bool> PingAsync (CancellationToken ct = default) {
            ThrowIfDisposed();
            return _channel.PingAsync(ct);
        }

        /// <summary>获取 Host 当前可用协议名称列表。</summary>
        public async Task<IReadOnlyList<string>> ListProtocolsAsync (CancellationToken ct = default) {
            ThrowIfDisposed();
            var resp = await _channel.Client.ListProtocolsAsync(new ListProtocolsRequest(), cancellationToken: ct).ConfigureAwait(false);
            if (resp?.ProtocolNames == null) {
                return Array.Empty<string>();
            }

            return resp.ProtocolNames
                .Where(x => !string.IsNullOrWhiteSpace(x))
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToList();
        }

        public void Dispose () {
            if (_disposed) return;
            _disposed = true;
            _deviceSvc.Dispose();
            _variableSvc.Dispose();
            _channel.Dispose();
        }

        private void ThrowIfDisposed () {
            if (_disposed) throw new ObjectDisposedException(nameof(EngineClient));
        }
    }
}
