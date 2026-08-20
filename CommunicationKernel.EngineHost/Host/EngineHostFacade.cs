using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using CommunicationKernel.Contracts.Models;
using CommunicationKernel.Core.Abstractions.Results;
using CommunicationKernel.Engine.Router.Abstractions;
using CommunicationKernel.Engine.Router.Models;

namespace CommunicationKernel.EngineHost.Host;

/// <summary>
/// -----------------------------------------------------------------------------
/// 文件: EngineHostFacade.cs
/// 层级: EngineHost / Host
/// 作用: 作为 Host 对上层（gRPC 服务 / 测试）的统一门面。
/// 说明:
/// 1) 对下组合 IRouterOrchestrator，承接路由、读写与订阅调度。
/// 2) DTO 映射辅助方法供非 gRPC 传输层使用；gRPC 层直接使用 HostRuntime 快照。
/// 3) ToRouteInfo 接受 routeId 参数以保证外部标识与内部 RouteKey 不混淆。
/// -----------------------------------------------------------------------------
/// </summary>
public sealed class EngineHostFacade {
    private readonly IRouterOrchestrator _orchestrator;

    public EngineHostFacade(IRouterOrchestrator orchestrator) {
        ArgumentNullException.ThrowIfNull(orchestrator);
        _orchestrator = orchestrator;
    }

    public IRouterOrchestrator Orchestrator => _orchestrator;

    public bool TryRegister(RouteEntry entry)                           => _orchestrator.TryRegister(entry);
    public bool TryGet(RouteKey key, out RouteEntry? entry)             => _orchestrator.TryGet(key, out entry);

    public Task<OperationResult> ExecuteWriteAsync(
        RouteKey routeKey,
        Func<CancellationToken, Task<OperationResult>> writeAction,
        CancellationToken cancellationToken)
        => _orchestrator.ExecuteWriteAsync(routeKey, writeAction, cancellationToken);

    public Task<OperationResult<byte[]>> ExecuteReadAsync(
        ReadRequestKey requestKey,
        Func<CancellationToken, Task<OperationResult<byte[]>>> readAction,
        CancellationToken cancellationToken)
        => _orchestrator.ExecuteReadAsync(requestKey, readAction, cancellationToken);

    public Guid Subscribe(SubscriptionTopic topic, Func<object, CancellationToken, Task> handler)
        => _orchestrator.Subscribe(topic, handler);

    public bool Unsubscribe(Guid subscriptionId)
        => _orchestrator.Unsubscribe(subscriptionId);

    public Task PublishAsync(SubscriptionTopic topic, object payload, CancellationToken cancellationToken)
        => _orchestrator.PublishAsync(topic, payload, cancellationToken);

    // ── DTO mapping helpers ───────────────────────────────────────────────────

    /// <summary>
    /// 将路由条目转换为对外 DTO。
    /// routeId 参数为调用方分配的路由标识（非 RouteKey.ToString()），
    /// 可选传入；若省略则回退为 Key.ToString()（仅用于测试）。
    /// </summary>
    public RouteInfoDto ToRouteInfo(RouteEntry entry, string? routeId = null) {
        return new RouteInfoDto {
            RouteId      = routeId ?? entry.Key.ToString(),
            ProtocolId   = entry.Key.ProtocolId,
            TransportKind = entry.Key.TransportKind.ToString(),
            Address      = entry.Key.Address,
            Port         = entry.Key.Port,
            Station      = entry.Key.Station
        };
    }

    public QueryRoutesResponseDto ToQueryRoutesResponse(string requestId, IEnumerable<RouteEntry> routes) {
        ArgumentNullException.ThrowIfNull(routes);
        return new QueryRoutesResponseDto {
            RequestId    = requestId,
            ErrorCode    = Core.Abstractions.Errors.KernelErrorCode.None,
            ErrorMessage = string.Empty,
            Routes       = routes.Select(e => ToRouteInfo(e)).ToList()
        };
    }

    public ReadResponseDto ToReadResponse(string requestId, OperationResult<byte[]> result) {
        return new ReadResponseDto {
            RequestId    = requestId,
            ErrorCode    = result.Success ? Core.Abstractions.Errors.KernelErrorCode.None : result.ErrorCode,
            ErrorMessage = result.Success ? string.Empty : result.ErrorMessage,
            Data         = result.Success ? result.Value : null
        };
    }

    public WriteResponseDto ToWriteResponse(string requestId, OperationResult result) {
        return new WriteResponseDto {
            RequestId    = requestId,
            ErrorCode    = result.Success ? Core.Abstractions.Errors.KernelErrorCode.None : result.ErrorCode,
            ErrorMessage = result.Success ? string.Empty : result.ErrorMessage
        };
    }

    public SubscribeResponseDto ToSubscribeResponse(string requestId, Guid subscriptionId, OperationResult result) {
        return new SubscribeResponseDto {
            RequestId      = requestId,
            SubscriptionId = subscriptionId.ToString("D"),
            ErrorCode      = result.Success ? Core.Abstractions.Errors.KernelErrorCode.None : result.ErrorCode,
            ErrorMessage   = result.Success ? string.Empty : result.ErrorMessage
        };
    }

    public UnsubscribeResponseDto ToUnsubscribeResponse(string requestId, bool removed, OperationResult result) {
        return new UnsubscribeResponseDto {
            RequestId    = requestId,
            Removed      = removed,
            ErrorCode    = result.Success ? Core.Abstractions.Errors.KernelErrorCode.None : result.ErrorCode,
            ErrorMessage = result.Success ? string.Empty : result.ErrorMessage
        };
    }

    public DiagnosticsResponseDto ToDiagnosticsResponse(string requestId, DiagnosticsDto diagnostics, OperationResult? result = null) {
        return new DiagnosticsResponseDto {
            RequestId    = requestId,
            ErrorCode    = result is null || result.Success
                ? Core.Abstractions.Errors.KernelErrorCode.None
                : result.ErrorCode,
            ErrorMessage = result is null || result.Success
                ? string.Empty
                : result.ErrorMessage,
            Diagnostics  = diagnostics
        };
    }
}
