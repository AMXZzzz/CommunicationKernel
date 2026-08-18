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
    bool TryRegister(RouteEntry entry);
    bool TryGet(RouteKey key, out RouteEntry? entry);
    bool TryRemove(RouteKey key, out RouteEntry? removed);

    /// <summary>
    /// 获取当前路由快照集合。
    /// 说明：返回快照用于查询展示，避免暴露内部可变字典。
    /// </summary>
    IReadOnlyList<RouteEntry> Snapshot();

    int Count { get; }
}
