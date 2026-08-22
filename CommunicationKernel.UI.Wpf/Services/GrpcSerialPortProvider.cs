#nullable disable

// -----------------------------------------------------------------------------
// 文件: Services/GrpcSerialPortProvider.cs
// 层级: UI层 — 服务实现
// 作用: 通过 gRPC 向 EngineHost 查询其所在机器上的串口。
// -----------------------------------------------------------------------------

using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using CommunicationKernel.Client.Grpc;
using CommunicationKernel.UI.Wpf.Core.Interfaces;

namespace CommunicationKernel.UI.Wpf.Services
{
    /// <summary>
    /// <see cref="ISerialPortProvider"/> 的 gRPC 实现。
    /// </summary>
    /// <remarks>
    /// 刻意不做缓存：USB 转串口设备可以随时插拔，缓存会让操作员
    /// 插上线后仍然看不到新串口，只能重启上位机。
    /// 这个调用很轻（一次 5 秒截止时间的查询 RPC），每次打开面板拉一遍即可。
    /// </remarks>
    public sealed class GrpcSerialPortProvider : ISerialPortProvider
    {
        private readonly EngineHostGrpcClient _client;

        public GrpcSerialPortProvider(EngineHostGrpcClient client)
        {
            _client = client ?? throw new ArgumentNullException(nameof(client));
        }

        /// <inheritdoc />
        public Task<IReadOnlyList<SerialPortDto>> GetPortsAsync(CancellationToken ct)
            // 客户端内部已把 RPC 异常与 Unimplemented 降级为空列表，
            // 此处无需再包 try/catch
            => _client.QuerySerialPortsAsync(ct);
    }
}
