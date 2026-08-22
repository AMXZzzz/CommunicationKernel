using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using CommunicationKernel.Engine.Router.Abstractions;
using CommunicationKernel.Engine.Router.Models;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace CommunicationKernel.Engine.Router;

/// <summary>
/// -----------------------------------------------------------------------------
/// 文件: ConnectionRouter.cs
/// 层级: Engine.Router
/// 作用: 维护 RouteKey 到 RouteEntry 的线程安全路由表。
/// 说明:
/// - 面向多 UI + 多 PLC 并发场景，路由表读写采用无锁并发字典。
/// - 该组件只负责路由注册/查询/移除，不负责执行读写。
/// - Snapshot() 返回活跃 RouteEntry 快照，仅供内部路由层调用；
///   外部（gRPC/UI）应通过 EngineRuntime.SnapshotRoutes() 获取元数据快照。
/// -----------------------------------------------------------------------------
/// </summary>
public sealed class ConnectionRouter : IConnectionRouter {
    private readonly ConcurrentDictionary<RouteKey, RouteEntry> _routes = new();
    private readonly ILogger<ConnectionRouter> _logger;

    public ConnectionRouter(ILogger<ConnectionRouter>? logger = null) {
        _logger = logger ?? NullLogger<ConnectionRouter>.Instance;
    }

    public int Count => _routes.Count;

    /// <summary>
    /// 获取当前路由快照（内部使用）。
    /// 注意：返回的 RouteEntry 持有活跃 TransportClient，禁止在路由层外部直接操作。
    /// </summary>
    public IReadOnlyList<RouteEntry> Snapshot()
        => _routes.Values.ToArray();

    public bool TryRegister(RouteEntry entry) {
        ArgumentNullException.ThrowIfNull(entry);
        bool added = _routes.TryAdd(entry.Key, entry);
        if (added)
            _logger.LogInformation("ConnectionRouter: registered route {RouteKey}", entry.Key);
        else
            _logger.LogWarning("ConnectionRouter: duplicate registration rejected for {RouteKey}", entry.Key);
        return added;
    }

    public bool TryGet(RouteKey key, out RouteEntry? entry)
        => _routes.TryGetValue(key, out entry);

    public bool TryRemove(RouteKey key, out RouteEntry? removed) {
        bool result = _routes.TryRemove(key, out removed);
        if (result)
            _logger.LogInformation("ConnectionRouter: removed route {RouteKey}", key);
        return result;
    }
}
