using System;
using System.Threading;
using System.Threading.Tasks;
using CommunicationKernel.Core.Abstractions.Results;
using CommunicationKernel.Engine.Router.Abstractions;
using CommunicationKernel.Engine.Router.Models;
using Microsoft.Extensions.Logging;

namespace CommunicationKernel.Engine.Router;

/// <summary>
/// -----------------------------------------------------------------------------
/// 文件: RouterOrchestrator.cs
/// 层级: Engine.Router
/// 作用: 聚合路由注册、读写调度与订阅分发的统一编排门面。
/// 说明:
/// - Host 层仅依赖此门面，降低对底层组件组合细节的耦合。
/// - 面向多 UI 并发访问同一设备场景，统一在此层进行调度分流。
/// - 提供 TryRemoveAndDisposeAsync 统一路由注销路径，确保 WriteScheduler
///   信号量与 TransportClient 均被正确清理，不留泄漏。
/// -----------------------------------------------------------------------------
/// </summary>
public sealed class RouterOrchestrator : IRouterOrchestrator {
    public RouterOrchestrator(ILoggerFactory? loggerFactory = null) {
        ConnectionRouter = new ConnectionRouter(loggerFactory?.CreateLogger<ConnectionRouter>());
        WriteScheduler   = new WriteScheduler(loggerFactory?.CreateLogger<WriteScheduler>());
        ReadCoordinator  = new ReadCoordinator();
        SubscriptionHub  = new SubscriptionHub(loggerFactory?.CreateLogger<SubscriptionHub>());
    }

    public IConnectionRouter ConnectionRouter { get; }
    public IWriteScheduler   WriteScheduler   { get; }
    public IReadCoordinator  ReadCoordinator  { get; }
    public ISubscriptionHub  SubscriptionHub  { get; }

    public int RouteCount        => ConnectionRouter.Count;
    public int SubscriptionCount => SubscriptionHub.Count;

    public bool TryRegister(RouteEntry entry) => ConnectionRouter.TryRegister(entry);
    public bool TryGet(RouteKey key, out RouteEntry? entry) => ConnectionRouter.TryGet(key, out entry);

    /// <summary>
    /// 注销路由并释放所有关联资源：
    /// 1) 从路由表移除。
    /// 2) 释放 WriteScheduler 信号量（防止内存泄漏）。
    /// 3) Dispose TransportClient（关闭 socket / 串口句柄）。
    /// </summary>
    public async Task<bool> TryRemoveAndDisposeAsync(RouteKey key, CancellationToken cancellationToken) {
        if (!ConnectionRouter.TryRemove(key, out RouteEntry? entry) || entry is null)
            return false;

        WriteScheduler.Release(key);

        await entry.DisposeAsync().ConfigureAwait(false);
        return true;
    }

    public Task<OperationResult> ExecuteWriteAsync(
        RouteKey routeKey,
        Func<CancellationToken, Task<OperationResult>> writeAction,
        CancellationToken cancellationToken)
        => WriteScheduler.ScheduleAsync(routeKey, writeAction, cancellationToken);

    public Task<OperationResult<byte[]>> ExecuteReadAsync(
        ReadRequestKey requestKey,
        Func<CancellationToken, Task<OperationResult<byte[]>>> readAction,
        CancellationToken cancellationToken)
        => ReadCoordinator.ExecuteAsync(requestKey, readAction, cancellationToken);

    public Guid Subscribe(SubscriptionTopic topic, Func<object, CancellationToken, Task> handler)
        => SubscriptionHub.Subscribe(topic, handler);

    public bool Unsubscribe(Guid subscriptionId)
        => SubscriptionHub.Unsubscribe(subscriptionId);

    public Task PublishAsync(SubscriptionTopic topic, object payload, CancellationToken cancellationToken)
        => SubscriptionHub.PublishAsync(topic, payload, cancellationToken);
}
