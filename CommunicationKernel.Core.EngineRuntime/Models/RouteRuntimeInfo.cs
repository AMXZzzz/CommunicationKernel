// -----------------------------------------------------------------------------
// 文件: RouteRuntimeInfo.cs
// 层级: Core.EngineRuntime / Models
// 作用: 已注册路由的只读元数据快照，供 gRPC/UI 查询，不暴露 TransportClient。
// -----------------------------------------------------------------------------

using CommunicationKernel.Core.Transport.Abstractions;
using CommunicationKernel.Core.EngineRouter.Models;

namespace CommunicationKernel.Core.EngineRuntime.Models;

/// <summary>一条已注册路由的只读运行时信息，供查询接口使用。</summary>
public sealed class RouteRuntimeInfo {
    /// <summary>调用方分配的路由标识。</summary>
    public required string RouteId { get; init; }

    /// <summary>实际选中的传输插件标识。</summary>
    public required string TransportId { get; init; }

    /// <summary>路由键（协议 + 介质 + 地址 + 端口 + 站号）。</summary>
    public required RouteKey RouteKey { get; init; }

    /// <summary>连接参数快照。</summary>
    public required TransportEndpoint Endpoint { get; init; }
}
