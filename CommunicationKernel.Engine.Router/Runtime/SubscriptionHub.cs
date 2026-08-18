using System;
using System.Collections.Concurrent;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using CommunicationKernel.Engine.Router.Abstractions;
using CommunicationKernel.Engine.Router.Models;

namespace CommunicationKernel.Engine.Router;

/// <summary>
/// -----------------------------------------------------------------------------
/// 文件: SubscriptionHub.cs
/// 层级: Engine.Router
/// 作用: 提供线程安全的发布订阅中心。
/// 说明:
/// 1) Host/Router 层将“状态变化事件”统一发布到此中心。
/// 2) 多个 UI 端可按 Topic 订阅同一设备/路由的状态。
/// 3) 单订阅者异常会被隔离，不影响其他订阅者接收。
/// -----------------------------------------------------------------------------
/// </summary>
public sealed class SubscriptionHub : ISubscriptionHub {
    private sealed class SubscriptionEntry {
        public required Guid Id { get; init; }
        public required SubscriptionTopic Topic { get; init; }
        public required Func<object, CancellationToken, Task> Handler { get; init; }
    }

    private readonly ConcurrentDictionary<Guid, SubscriptionEntry> _subscriptions = new();

    /// <summary>
    /// 当前有效订阅数量。
    /// </summary>
    public int Count => _subscriptions.Count;

    /// <summary>
    /// 注册一个订阅回调并返回订阅标识。
    /// </summary>
    public Guid Subscribe(SubscriptionTopic topic, Func<object, CancellationToken, Task> handler) {
        ArgumentNullException.ThrowIfNull(handler);

        var entry = new SubscriptionEntry {
            Id = Guid.NewGuid(),
            Topic = topic,
            Handler = handler
        };

        _subscriptions[entry.Id] = entry;
        return entry.Id;
    }

    /// <summary>
    /// 按订阅标识注销回调。
    /// </summary>
    public bool Unsubscribe(Guid subscriptionId)
        => _subscriptions.TryRemove(subscriptionId, out _);

    /// <summary>
    /// 发布指定主题事件。
    /// </summary>
    public async Task PublishAsync(SubscriptionTopic topic, object payload, CancellationToken cancellationToken) {
        var handlers = _subscriptions.Values
            .Where(subscription => subscription.Topic.Equals(topic))
            .Select(subscription => subscription.Handler)
            .ToArray();

        foreach (Func<object, CancellationToken, Task> handler in handlers) {
            cancellationToken.ThrowIfCancellationRequested();
            try {
                await handler(payload, cancellationToken).ConfigureAwait(false);
            } catch {
                // 失败隔离：单个订阅处理器异常不影响整体广播流程。
            }
        }
    }
}
