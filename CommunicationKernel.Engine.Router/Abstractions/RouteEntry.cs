using System;
using System.Threading.Tasks;
using CommunicationKernel.Communication.Protocol.Abstractions;
using CommunicationKernel.Communication.Transport.Abstractions;
using CommunicationKernel.Engine.Router.Models;

namespace CommunicationKernel.Engine.Router.Abstractions;

/// <summary>
/// -----------------------------------------------------------------------------
/// 文件: RouteEntry.cs
/// 层级: Engine.Router / Abstractions
/// 作用: 路由条目，持有特定路由的传输客户端与协议驱动实例。
/// 说明:
/// - 实现 IAsyncDisposable：路由注销时必须调用 DisposeAsync 释放 TransportClient，
///   否则底层 TCP socket / SerialPort 句柄泄漏。
/// - 调用方（HostRuntime.UnregisterRouteAsync）负责在 TryRemove 后调用 DisposeAsync。
/// -----------------------------------------------------------------------------
/// </summary>
public sealed class RouteEntry : IAsyncDisposable {
    public required RouteKey Key { get; init; }
    public required ITransportClient TransportClient { get; init; }
    public required IProtocolDriver ProtocolDriver { get; init; }

    /// <summary>
    /// 释放传输客户端底层资源（socket / 串口句柄）。
    /// 必须在路由从路由表移除后调用。
    /// </summary>
    public async ValueTask DisposeAsync() {
        try {
            await TransportClient.DisposeAsync().ConfigureAwait(false);
        } catch (Exception) {
            // 释放阶段异常不应传播：已移除的路由资源最大努力释放即可。
        }
    }
}
