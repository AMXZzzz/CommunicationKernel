using System;
using System.Threading;
using System.Threading.Tasks;
using CommunicationKernel.Core.Abstractions.Results;
using CommunicationKernel.Engine.Router.Models;

namespace CommunicationKernel.Engine.Router.Abstractions;

public interface IRouterOrchestrator {
    IConnectionRouter ConnectionRouter { get; }
    IWriteScheduler WriteScheduler { get; }
    IReadCoordinator ReadCoordinator { get; }
    ISubscriptionHub SubscriptionHub { get; }

    int RouteCount { get; }
    int SubscriptionCount { get; }

    bool TryRegister(RouteEntry entry);
    bool TryGet(RouteKey key, out RouteEntry? entry);

    /// <summary>
    /// 移除路由并释放所有关联资源（WriteScheduler 信号量 + TransportClient）。
    /// 应当替代直接调用 ConnectionRouter.TryRemove。
    /// </summary>
    Task<bool> TryRemoveAndDisposeAsync(RouteKey key, CancellationToken cancellationToken);

    Task<OperationResult> ExecuteWriteAsync(
        RouteKey routeKey,
        Func<CancellationToken, Task<OperationResult>> writeAction,
        CancellationToken cancellationToken);

    Task<OperationResult<byte[]>> ExecuteReadAsync(
        ReadRequestKey requestKey,
        Func<CancellationToken, Task<OperationResult<byte[]>>> readAction,
        CancellationToken cancellationToken);

    Guid Subscribe(SubscriptionTopic topic, Func<object, CancellationToken, Task> handler);
    bool Unsubscribe(Guid subscriptionId);
    Task PublishAsync(SubscriptionTopic topic, object payload, CancellationToken cancellationToken);
}
