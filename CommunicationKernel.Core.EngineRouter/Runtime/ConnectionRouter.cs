// -----------------------------------------------------------------------------
// 文件: ConnectionRouter.cs
// 层级: Core.EngineRouter / Runtime
// 作用: 维护 RouteKey → RouteEntry 的线程安全路由表，不执行读写。
// -----------------------------------------------------------------------------

using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using CommunicationKernel.Core.EngineRouter.Abstractions;
using CommunicationKernel.Core.EngineRouter.Models;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace CommunicationKernel.Core.EngineRouter;

/// <summary>
/// -----------------------------------------------------------------------------
/// 文件: ConnectionRouter.cs
/// 层级: Core.EngineRouter
/// 作用: 维护 RouteKey 到 RouteEntry 的线程安全路由表。
/// 说明:
/// - 面向多 UI + 多 PLC 并发场景，路由表读写采用无锁并发字典。
/// - 该组件只负责路由注册/查询/移除，不负责执行读写。
/// - Snapshot() 返回活跃 RouteEntry 快照，仅供内部路由层调用；
///   外部（gRPC/UI）应通过 EngineRuntime.SnapshotRoutes() 获取元数据快照。
/// -----------------------------------------------------------------------------
/// </summary>
public sealed class ConnectionRouter : IConnectionRouter {
    // RouteKey → 活跃 RouteEntry；无锁字典以支撑多 UI 并发登记/查询
    private readonly ConcurrentDictionary<RouteKey, RouteEntry> _routes = new();
    private readonly ILogger<ConnectionRouter> _logger;

    /// <param name="logger">日志记录器；为 null 时退化为 NullLogger，保持嵌入与测试场景可用。</param>
    public ConnectionRouter(ILogger<ConnectionRouter>? logger = null) {
        // 未注入日志时退化为空实现，避免嵌入/测试场景强制依赖日志组件
        _logger = logger ?? NullLogger<ConnectionRouter>.Instance;
    }

    // 当前活跃路由条数，供 Health / Diagnostics 使用
    public int Count => _routes.Count;

    /// <summary>
    /// 获取当前路由快照（内部使用）。
    /// 注意：返回的 RouteEntry 持有活跃 TransportClient，禁止在路由层外部直接操作。
    /// </summary>
    public IReadOnlyList<RouteEntry> Snapshot()
        // ToArray 复制一份，避免调用方遍历时与并发登记/摘除冲突
        => _routes.Values.ToArray();

    /// <summary>
    /// 登记一条路由。RouteKey 已存在时拒绝，不覆盖。
    /// </summary>
    /// <returns>是否登记成功；false 表示同一物理设备已有路由。</returns>
    /// <remarks>
    /// 拒绝覆盖是关键：覆盖会让前一条路由的 TransportClient 成为没人释放的孤儿，
    /// 同时两套 I/O 争用同一个 socket 或串口句柄。
    /// </remarks>
    public bool TryRegister(RouteEntry entry) {
        // 拒绝空条目：空 RouteEntry 无法提供 TransportClient / 协议驱动
        ArgumentNullException.ThrowIfNull(entry);
        // 原子插入：同一物理设备（RouteKey）已存在时拒绝覆盖，避免两套 I/O 争用同一连接
        bool added = _routes.TryAdd(entry.Key, entry);
        if (added)
            _logger.LogInformation("ConnectionRouter: registered route {RouteKey}", entry.Key);
        else
            _logger.LogWarning("ConnectionRouter: duplicate registration rejected for {RouteKey}", entry.Key);
        return added;
    }

    /// <summary>按路由键查找活跃路由。</summary>
    /// <returns>是否命中；未命中时读写路径应返回 RouteNotFound。</returns>
    public bool TryGet(RouteKey key, out RouteEntry? entry)
        // 按物理连接键查找；未命中时读写路径应返回 RouteNotFound
        => _routes.TryGetValue(key, out entry);

    /// <summary>
    /// 从路由表摘除一条路由。
    /// </summary>
    /// <returns>是否确有摘除。</returns>
    /// <remarks>
    /// <b>只摘表，不释放连接。</b>释放由编排器在摘表<b>之后</b>调用 DisposeAsync 完成——
    /// 顺序颠倒会让并发的读写拿到一个正在被释放的 TransportClient。
    /// </remarks>
    public bool TryRemove(RouteKey key, out RouteEntry? removed) {
        // 仅从字典摘除，不释放 socket/串口——释放由编排器在摘表之后调用 DisposeAsync
        bool result = _routes.TryRemove(key, out removed);
        if (result)
            _logger.LogInformation("ConnectionRouter: removed route {RouteKey}", key);
        return result;
    }
}
