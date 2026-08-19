using System;
using System.Threading;
using System.Threading.Tasks;
using CommunicationDebuggingTools.Contracts.V1;
using Grpc.Net.Client;

namespace CommunicationDebuggingTools.Client {

    /// <summary>
    /// 管理与 EngineHost 的 gRPC 通道生命周期。
    /// 单例持有，应用退出时 Dispose。
    /// </summary>
    public sealed class EngineHostChannel : IDisposable {

        private GrpcChannel   _channel;
        private Engine.EngineClient _client;
        private bool          _disposed;

        public string Address { get; private set; }
        public bool   IsOpen  => _channel != null && !_disposed;

        /// <summary>打开（或复用）通道。地址变更时先关旧通道。</summary>
        public Engine.EngineClient Open (string address) {
            if (_disposed) throw new ObjectDisposedException(nameof(EngineHostChannel));
            if (_channel != null && Address == address) return _client;

            CloseInternal();
            Address  = address;
            _channel = GrpcChannel.ForAddress(address, new GrpcChannelOptions {
                // 允许 HTTP/1.1（Kestrel 配置了 Http1AndHttp2）
                HttpHandler = new System.Net.Http.HttpClientHandler()
            });
            _client  = new Engine.EngineClient(_channel);
            return _client;
        }

        public Engine.EngineClient Client => _client
            ?? throw new InvalidOperationException("请先调用 Open()");

        /// <summary>向 EngineHost 发送健康探针，返回是否可达。</summary>
        public async Task<bool> PingAsync (CancellationToken ct = default) {
            if (_client == null) return false;
            try {
                var resp = await _client.HealthAsync(new HealthRequest(),
                    cancellationToken: ct).ConfigureAwait(false);
                return resp.Ok;
            } catch { return false; }
        }

        private void CloseInternal () {
            try { _channel?.ShutdownAsync().Wait(500); } catch { }
            _channel?.Dispose();
            _channel = null;
            _client  = null;
        }

        public void Dispose () {
            if (_disposed) return;
            _disposed = true;
            CloseInternal();
        }
    }
}
