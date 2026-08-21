using System.Threading;
using System.Threading.Tasks;
using CommunicationDebuggingTools.Core.Enums;
using CommunicationDebuggingTools.Core.Interfaces;
using CommunicationDebuggingTools.Core.Models;

namespace CommunicationDebuggingTools.Tests.Fakes {
    /// <summary>协议桩：会话 + 可配置读写结果。</summary>
    [ProtocolName("Modbus TCP")]
    public class FakeProtocol : IProtocol {
        public bool ConnectResult { get; set; } = true;
        public int ConnectCallCount { get; private set; }
        public int DisconnectCallCount { get; private set; }
        public ProtocolConnectionContext LastContext { get; private set; }

        /// <summary>读是否成功。</summary>
        public bool ReadResult { get; set; } = true;

        /// <summary>读成功时回填的值。</summary>
        public object ReadValue { get; set; } = (short)123;

        /// <summary>写是否成功。</summary>
        public bool WriteResult { get; set; } = true;

        public int ReadCallCount { get; private set; }
        public int WriteCallCount { get; private set; }
        public ProtocolDataMessage LastReadRequest { get; private set; }
        public ProtocolDataMessage LastWriteRequest { get; private set; }
        /// <summary>模拟 Ping；默认与 IsConnected 一致，可单测时改写。</summary>
        public bool PingResult { get; set; } = true;

        public int PingCallCount { get; private set; }

        public Task<bool> PingAsync (CancellationToken cancellationToken) {
            PingCallCount++;
            cancellationToken.ThrowIfCancellationRequested();
            // 未连接则探针失败，贴近真实协议
            if (!IsConnected)
                return Task.FromResult(false);
            return Task.FromResult(PingResult);
        }

        /// <summary>测试时可直接赋值，无需经 ConnectAsync。</summary>
        public bool IsConnected { get; set; }

        public Task<bool> ConnectAsync (
            ProtocolConnectionContext context,
            CancellationToken cancellationToken) {
            ConnectCallCount++;
            LastContext = context;
            IsConnected = ConnectResult && context != null;
            return Task.FromResult(IsConnected);
        }

        public void Disconnect () {
            DisconnectCallCount++;
            IsConnected = false;
        }

        private bool _disposed;
        public void Dispose () {
            if (_disposed) return;
            _disposed = true;
            Disconnect();
        }

        public Task<ProtocolDataMessage> ReadAsync (
            ProtocolDataMessage request,
            CancellationToken cancellationToken) {
            ReadCallCount++;
            LastReadRequest = request;

            if (!ReadResult) {
                request.Success = false;
                request.Quality = DataQuality.Bad;
                request.ErrorMessage = "模拟读失败";
                return Task.FromResult(request);
            }

            request.Value = ReadValue;
            request.Success = true;
            request.Quality = DataQuality.Good;
            request.ErrorMessage = "";
            return Task.FromResult(request);
        }

        public Task<ProtocolDataMessage> WriteAsync (
            ProtocolDataMessage request,
            CancellationToken cancellationToken) {
            WriteCallCount++;
            LastWriteRequest = request;

            if (!WriteResult) {
                request.Success = false;
                request.ErrorMessage = "模拟写失败";
                return Task.FromResult(request);
            }

            request.Success = true;
            request.ErrorMessage = "";
            return Task.FromResult(request);
        }
    }
}