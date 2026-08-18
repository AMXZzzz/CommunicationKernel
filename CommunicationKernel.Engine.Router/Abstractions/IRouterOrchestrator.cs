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

    bool TryRegister(RouteEntry entry);
    bool TryGet(RouteKey key, out RouteEntry? entry);
    bool TryRemove(RouteKey key, out RouteEntry? removed);

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
