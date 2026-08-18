using System;
using System.Threading;
using System.Threading.Tasks;
using CommunicationKernel.Core.Abstractions.Results;
using CommunicationKernel.Engine.Router.Abstractions;
using CommunicationKernel.Engine.Router.Models;

namespace CommunicationKernel.Engine.Router;

/// <summary>
/// -----------------------------------------------------------------------------
/// 文件: RouterOrchestrator.cs
/// 层级: Engine.Router
/// 作用: 聚合路由注册、读写调度与订阅分发的统一编排门面。
/// 说明:
/// - Host 层仅依赖此门面，降低对底层组件组合细节的耦合。
/// - 面向多 UI 并发访问同一设备场景，统一在此层进行调度分流。
/// -----------------------------------------------------------------------------
/// </summary>
public sealed class RouterOrchestrator : IRouterOrchestrator {
    public RouterOrchestrator() {
        // 组合初始化：分别构建路由表、写调度、读协调与订阅中心。
        ConnectionRouter = new ConnectionRouter();
        WriteScheduler = new WriteScheduler();
        ReadCoordinator = new ReadCoordinator();
        SubscriptionHub = new SubscriptionHub();
    }

    public IConnectionRouter ConnectionRouter { get; }
    public IWriteScheduler WriteScheduler { get; }
    public IReadCoordinator ReadCoordinator { get; }
    public ISubscriptionHub SubscriptionHub { get; }

    // 路由表转发：注册/查询/删除。
    public bool TryRegister(RouteEntry entry) => ConnectionRouter.TryRegister(entry);
    public bool TryGet(RouteKey key, out RouteEntry? entry) => ConnectionRouter.TryGet(key, out entry);
    public bool TryRemove(RouteKey key, out RouteEntry? removed) => ConnectionRouter.TryRemove(key, out removed);

    // 写路径：按路由串行调度。
    public Task<OperationResult> ExecuteWriteAsync(
        RouteKey routeKey,
        Func<CancellationToken, Task<OperationResult>> writeAction,
        CancellationToken cancellationToken)
        => WriteScheduler.ScheduleAsync(routeKey, writeAction, cancellationToken);

    // 读路径：同键读请求合并。
    public Task<OperationResult<byte[]>> ExecuteReadAsync(
        ReadRequestKey requestKey,
        Func<CancellationToken, Task<OperationResult<byte[]>>> readAction,
        CancellationToken cancellationToken)
        => ReadCoordinator.ExecuteAsync(requestKey, readAction, cancellationToken);

    // 订阅路径：注册/注销/广播。
    public Guid Subscribe(SubscriptionTopic topic, Func<object, CancellationToken, Task> handler)
        => SubscriptionHub.Subscribe(topic, handler);

    public bool Unsubscribe(Guid subscriptionId)
        => SubscriptionHub.Unsubscribe(subscriptionId);

    public Task PublishAsync(SubscriptionTopic topic, object payload, CancellationToken cancellationToken)
        => SubscriptionHub.PublishAsync(topic, payload, cancellationToken);
}
