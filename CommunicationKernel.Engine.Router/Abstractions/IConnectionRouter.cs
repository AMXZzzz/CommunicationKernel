// -----------------------------------------------------------------------------
// 文件: IConnectionRouter.cs
// 层级: Engine.Router / Abstractions
// 作用: 定义路由注册表最小契约——只负责 RouteEntry 的登记、查询、摘除与快照。
// -----------------------------------------------------------------------------

using System.Collections.Generic;
using CommunicationKernel.Engine.Router.Models;

namespace CommunicationKernel.Engine.Router.Abstractions;

/// <summary>
/// -----------------------------------------------------------------------------
/// 文件: IConnectionRouter.cs
/// 层级: Engine.Router / Abstractions
/// 作用: 定义路由注册表最小契约。
/// 说明:
/// 1) 负责 RouteEntry 的注册、查询、移除与快照读取。
/// 2) 不承担读写执行职责，执行由调度器与协议驱动完成。
/// 3) 为 Host/gRPC 查询接口提供统一只读视图。
/// -----------------------------------------------------------------------------
/// </summary>
public interface IConnectionRouter {
    // 登记一条路由；RouteKey 冲突时返回 false，不覆盖已有物理连接
    bool TryRegister(RouteEntry entry);

    // 按 RouteKey 取活跃条目；未登记时返回 false，读写路径据此判定 RouteNotFound
    bool TryGet(RouteKey key, out RouteEntry? entry);

    // 从路由表摘除条目，但不释放 TransportClient——释放必须由编排器在摘表之后完成
    bool TryRemove(RouteKey key, out RouteEntry? removed);

    /// <summary>
    /// 获取当前路由快照集合。
    /// 说明：返回快照用于查询展示，避免暴露内部可变字典。
    /// </summary>
    IReadOnlyList<RouteEntry> Snapshot();

    // 当前活跃路由条数，供 Health / Diagnostics 端点使用
    int Count { get; }
}
