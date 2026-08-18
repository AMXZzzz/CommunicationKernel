using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using CommunicationKernel.Engine.Router.Abstractions;
using CommunicationKernel.Engine.Router.Models;

namespace CommunicationKernel.Engine.Router;

/// <summary>
/// -----------------------------------------------------------------------------
/// 文件: ConnectionRouter.cs
/// 层级: Engine.Router
/// 作用: 维护 RouteKey 到 RouteEntry 的线程安全路由表。
/// 说明:
/// - 面向多 UI + 多 PLC 并发场景，路由表读写采用无锁并发字典。
/// - 该组件只负责路由注册/查询/移除，不负责执行读写。
/// -----------------------------------------------------------------------------
/// </summary>
public sealed class ConnectionRouter : IConnectionRouter {
    private readonly ConcurrentDictionary<RouteKey, RouteEntry> _routes = new();

    /// <summary>
    /// 当前已注册路由数量。
    /// </summary>
    public int Count => _routes.Count;

    /// <summary>
    /// 获取当前路由快照集合。
    /// </summary>
    public IReadOnlyList<RouteEntry> Snapshot() {
        // 快照语义：复制当前值集合，避免调用方枚举期间受并发写入影响。
        return _routes.Values.ToArray();
    }

    /// <summary>
    /// 尝试注册路由。
    /// 分支语义：键已存在时返回 false，保持已有路由稳定。
    /// </summary>
    public bool TryRegister(RouteEntry entry) {
        // 企业级参数防御：即使签名是非空引用，也防御运行期非法 null 传入。
        ArgumentNullException.ThrowIfNull(entry);
        return _routes.TryAdd(entry.Key, entry);
    }

    /// <summary>
    /// 尝试获取路由。
    /// 分支语义：命中返回 true，否则返回 false 并输出 null。
    /// </summary>
    public bool TryGet(RouteKey key, out RouteEntry? entry)
        => _routes.TryGetValue(key, out entry);

    /// <summary>
    /// 尝试移除路由。
    /// 分支语义：存在则删除并返回 true；不存在则返回 false。
    /// </summary>
    public bool TryRemove(RouteKey key, out RouteEntry? removed)
        => _routes.TryRemove(key, out removed);
}
