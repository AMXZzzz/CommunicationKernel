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
/// 作用: 作为 Host 对上层 UI/Client 的统一门面。
/// 说明:
/// 1) 对下组合 <see cref="IRouterOrchestrator"/>，承接路由、读写与订阅调度。
/// 2) 对上输出 Contracts DTO，屏蔽内核对象细节。
/// 3) 保持“单一访问中枢”原则，满足多 UI 并行访问同设备场景。
/// -----------------------------------------------------------------------------
/// </summary>
public sealed class EngineHostFacade {
    private readonly IRouterOrchestrator _orchestrator;

    /// <summary>
    /// 创建门面实例。
    /// </summary>
    /// <param name="orchestrator">由组合根注入的路由编排器实例。</param>
    public EngineHostFacade(IRouterOrchestrator orchestrator) {
        // 企业级约束：门面层不直接 new 低层具体实现，强制通过依赖注入传入。
        ArgumentNullException.ThrowIfNull(orchestrator);
        _orchestrator = orchestrator;
    }

    /// <summary>
    /// 当前使用的路由编排器。
    /// </summary>
    public IRouterOrchestrator Orchestrator => _orchestrator;

    public bool TryRegister(RouteEntry entry) => _orchestrator.TryRegister(entry);
    public bool TryGet(RouteKey key, out RouteEntry? entry) => _orchestrator.TryGet(key, out entry);
    public bool TryRemove(RouteKey key, out RouteEntry? removed) => _orchestrator.TryRemove(key, out removed);

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

    /// <summary>
    /// 将内核路由条目转换为对外路由信息 DTO。
    /// </summary>
    public RouteInfoDto ToRouteInfo(RouteEntry entry) {
        return new RouteInfoDto {
            RouteId = entry.Key.ToString(),
            ProtocolId = entry.Key.ProtocolId,
            TransportKind = entry.Key.TransportKind.ToString(),
            Address = entry.Key.Address,
            Port = entry.Key.Port,
            Station = entry.Key.Station
        };
    }

    /// <summary>
    /// 构建路由查询响应 DTO。
    /// </summary>
    public QueryRoutesResponseDto ToQueryRoutesResponse(string requestId, IEnumerable<RouteEntry> routes) {
        return new QueryRoutesResponseDto {
            // 分支1：requestId 为空时回退为空字符串，保证响应序列化稳定。
            RequestId = requestId ?? string.Empty,

            // 路由查询成功构造场景固定输出 None，错误由更上游调用流程填充。
            ErrorCode = Core.Abstractions.Errors.KernelErrorCode.None,
            ErrorMessage = string.Empty,

            // 分支2：routes 为空时回退空集合，避免空引用并保证前端可直接遍历。
            Routes = (routes ?? Array.Empty<RouteEntry>()).Select(ToRouteInfo).ToList()
        };
    }

    /// <summary>
    /// 将内核读结果转换为对外读响应 DTO。
    /// </summary>
    public ReadResponseDto ToReadResponse(string requestId, OperationResult<byte[]> result) {
        return new ReadResponseDto {
            // 分支1：请求标识为空时回退，避免上层协议因 null 触发额外判断。
            RequestId = requestId ?? string.Empty,

            // 分支2：读取成功时统一置 None；失败时透传内核错误码。
            ErrorCode = result.Success
                ? Core.Abstractions.Errors.KernelErrorCode.None
                : result.ErrorCode,

            // 分支3：成功不附带错误消息；失败透传错误文本。
            ErrorMessage = result.Success
                ? string.Empty
                : result.ErrorMessage,

            // 分支4：仅成功时携带数据，失败返回 null 明确表示无有效负载。
            Data = result.Success ? result.Value : null
        };
    }

    /// <summary>
    /// 将内核写结果转换为对外写响应 DTO。
    /// </summary>
    public WriteResponseDto ToWriteResponse(string requestId, OperationResult result) {
        return new WriteResponseDto {
            RequestId = requestId ?? string.Empty,
            ErrorCode = result.Success
                ? Core.Abstractions.Errors.KernelErrorCode.None
                : result.ErrorCode,
            ErrorMessage = result.Success
                ? string.Empty
                : result.ErrorMessage
        };
    }

    /// <summary>
    /// 构建订阅响应 DTO。
    /// </summary>
    public SubscribeResponseDto ToSubscribeResponse(string requestId, Guid subscriptionId, OperationResult result) {
        return new SubscribeResponseDto {
            RequestId = requestId ?? string.Empty,
            SubscriptionId = subscriptionId.ToString("D"),
            ErrorCode = result.Success
                ? Core.Abstractions.Errors.KernelErrorCode.None
                : result.ErrorCode,
            ErrorMessage = result.Success
                ? string.Empty
                : result.ErrorMessage
        };
    }

    /// <summary>
    /// 构建退订响应 DTO。
    /// </summary>
    public UnsubscribeResponseDto ToUnsubscribeResponse(string requestId, bool removed, OperationResult result) {
        return new UnsubscribeResponseDto {
            RequestId = requestId ?? string.Empty,
            Removed = removed,
            ErrorCode = result.Success
                ? Core.Abstractions.Errors.KernelErrorCode.None
                : result.ErrorCode,
            ErrorMessage = result.Success
                ? string.Empty
                : result.ErrorMessage
        };
    }

    /// <summary>
    /// 构建诊断响应 DTO。
    /// </summary>
    public DiagnosticsResponseDto ToDiagnosticsResponse(string requestId, DiagnosticsDto diagnostics, OperationResult? result = null) {
        return new DiagnosticsResponseDto {
            // 分支1：保持请求标识稳定。
            RequestId = requestId ?? string.Empty,

            // 分支2：result 为 null 视为“仅回传诊断快照”成功场景。
            // 分支3：result.Success=true 同样输出 None。
            // 分支4：仅当 result 明确失败时透传错误码。
            ErrorCode = result is null || result.Success
                ? Core.Abstractions.Errors.KernelErrorCode.None
                : result.ErrorCode,

            // 与 ErrorCode 对应：成功场景统一空消息，失败场景透传错误信息。
            ErrorMessage = result is null || result.Success
                ? string.Empty
                : result.ErrorMessage,

            // 诊断载荷原样回传，供多 UI 端统一展示运行状态。
            Diagnostics = diagnostics
        };
    }
}
