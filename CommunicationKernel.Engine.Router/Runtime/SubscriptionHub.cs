using System;
using System.Collections.Concurrent;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using CommunicationKernel.Engine.Router.Abstractions;
using CommunicationKernel.Engine.Router.Models;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace CommunicationKernel.Engine.Router;

/// <summary>
/// -----------------------------------------------------------------------------
/// 文件: SubscriptionHub.cs
/// 层级: Engine.Router
/// 作用: 提供线程安全的发布订阅中心。
/// 说明:
/// 1) 多 UI 端可按 Topic 订阅同一设备/路由的状态。
/// 2) 发布采用并行扇出（Task.WhenAll），避免慢订阅者阻塞其他 UI（队头阻塞问题）。
/// 3) 单订阅者异常被隔离并记录日志，不影响其他订阅者；但 OperationCanceledException
///    会被重新抛出，确保取消信号正确传播。
/// -----------------------------------------------------------------------------
/// </summary>
public sealed class SubscriptionHub : ISubscriptionHub {
    private sealed class SubscriptionEntry {
        public required Guid Id { get; init; }
        public required SubscriptionTopic Topic { get; init; }
        public required Func<object, CancellationToken, Task> Handler { get; init; }
    }

    private readonly ConcurrentDictionary<Guid, SubscriptionEntry> _subscriptions = new();
    private readonly ILogger<SubscriptionHub> _logger;

    public SubscriptionHub(ILogger<SubscriptionHub>? logger = null) {
        _logger = logger ?? NullLogger<SubscriptionHub>.Instance;
    }

    public int Count => _subscriptions.Count;

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

    public bool Unsubscribe(Guid subscriptionId)
        => _subscriptions.TryRemove(subscriptionId, out _);

    /// <summary>
    /// 并行扇出发布：所有匹配订阅者同时执行，互不阻塞。
    /// </summary>
    public async Task PublishAsync(SubscriptionTopic topic, object payload, CancellationToken cancellationToken) {
        cancellationToken.ThrowIfCancellationRequested();

        Func<object, CancellationToken, Task>[] handlers = _subscriptions.Values
            .Where(s => s.Topic.Equals(topic))
            .Select(s => s.Handler)
            .ToArray();

        if (handlers.Length == 0) return;

        await Task.WhenAll(handlers.Select(h => SafeInvokeAsync(h, payload, cancellationToken)))
            .ConfigureAwait(false);
    }

    private async Task SafeInvokeAsync(
        Func<object, CancellationToken, Task> handler,
        object payload,
        CancellationToken cancellationToken) {
        try {
            await handler(payload, cancellationToken).ConfigureAwait(false);
        } catch (OperationCanceledException) {
            // 外部取消信号：不吞没，让 WhenAll 传播取消。
            throw;
        } catch (Exception ex) {
            // 业务异常：隔离记录，不中断其他订阅者。
            _logger.LogError(ex, "SubscriptionHub: handler failed for topic {Topic}", payload?.GetType().Name ?? "unknown");
        }
    }
}
