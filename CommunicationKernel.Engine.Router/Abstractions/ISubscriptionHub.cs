using System;
using System.Threading;
using System.Threading.Tasks;
using CommunicationKernel.Engine.Router.Models;

namespace CommunicationKernel.Engine.Router.Abstractions;

public interface ISubscriptionHub {
    Guid Subscribe(SubscriptionTopic topic, Func<object, CancellationToken, Task> handler);
    bool Unsubscribe(Guid subscriptionId);
    Task PublishAsync(SubscriptionTopic topic, object payload, CancellationToken cancellationToken);
    int Count { get; }
}
