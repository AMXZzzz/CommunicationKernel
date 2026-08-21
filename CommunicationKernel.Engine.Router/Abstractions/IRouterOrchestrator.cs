using System;
using System.Threading;
using System.Threading.Tasks;
using CommunicationKernel.Core.Abstractions.Results;
using CommunicationKernel.Engine.Router.Models;

namespace CommunicationKernel.Engine.Router.Abstractions;

/// <summary>
/// -----------------------------------------------------------------------------
/// 文件: IRouterOrchestrator.cs
/// 层级: Engine.Router / Abstractions
/// 作用: 路由编排器接口——管理路由表并协调读取合并。
/// 说明:
/// 1) <b>并发串行化不在本接口</b>：一条路由对应一个物理连接，读写互斥由
///    <see cref="RouteEntry.ExecuteExclusiveAsync{TResult}"/> 统一承担。
///    历史上曾有 WriteScheduler（只管写）与 SerialIoGate（只管串口）两套门控并存，
///    TCP 读路径因此完全没有互斥，多变量轮询时会在同一个流上并发读写而串数据。
/// 2) 本接口只保留路由表与读合并两项真实职责。
/// -----------------------------------------------------------------------------
/// </summary>
public interface IRouterOrchestrator {
    /// <summary>路由表。</summary>
    IConnectionRouter ConnectionRouter { get; }

    /// <summary>读取合并协调器。</summary>
    IReadCoordinator ReadCoordinator { get; }

    /// <summary>当前注册路由数量。</summary>
    int RouteCount { get; }

    /// <summary>注册路由；键冲突时返回 false。</summary>
    bool TryRegister(RouteEntry entry);

    /// <summary>按键取路由条目。</summary>
    bool TryGet(RouteKey key, out RouteEntry? entry);

    /// <summary>
    /// 移除路由并释放其传输资源。
    /// 应当替代直接调用 <see cref="IConnectionRouter.TryRemove"/>。
    /// </summary>
    Task<bool> TryRemoveAndDisposeAsync(RouteKey key, CancellationToken cancellationToken);

    /// <summary>
    /// 执行读取。相同 (路由, 地址, 长度) 的并发请求合并为单次 I/O，共享结果。
    /// </summary>
    Task<OperationResult<byte[]>> ExecuteReadAsync(
        ReadRequestKey requestKey,
        Func<CancellationToken, Task<OperationResult<byte[]>>> readAction,
        CancellationToken cancellationToken);
}
