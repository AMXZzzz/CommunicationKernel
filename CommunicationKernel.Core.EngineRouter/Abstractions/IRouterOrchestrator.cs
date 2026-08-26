// -----------------------------------------------------------------------------
// 文件: IRouterOrchestrator.cs
// 层级: Core.EngineRouter / Abstractions
// 作用: 路由编排器接口——管理路由表并协调读取合并，是路由层唯一对外入口。
// -----------------------------------------------------------------------------

using System;
using System.Threading;
using System.Threading.Tasks;
using CommunicationKernel.Core.Abstractions.Results;
using CommunicationKernel.Core.EngineRouter.Models;

namespace CommunicationKernel.Core.EngineRouter.Abstractions;

/// <summary>
/// -----------------------------------------------------------------------------
/// 文件: IRouterOrchestrator.cs
/// 层级: Core.EngineRouter / Abstractions
/// 作用: 路由编排器接口——管理路由表并协调读取合并。
/// 说明:
/// 1) <b>并发串行化不在本接口</b>：一条路由对应一个物理连接，读写互斥由
///    <see cref="RouteEntry.ExecuteExclusiveAsync{TResult}"/> 统一承担。
///    历史上曾有 WriteScheduler（只管写）与 SerialIoGate（只管串口）两套门控并存，
///    TCP 读路径因此完全没有互斥，多变量轮询时会在同一个流上并发读写而串数据。
/// 2) 本接口只保留路由表与读合并两项真实职责。
/// -----------------------------------------------------------------------------
/// </summary>
/// <remarks>
/// <b>不暴露子组件。</b>
/// 曾经同时提供 <c>ConnectionRouter</c> / <c>ReadCoordinator</c> 属性与一组转发方法，
/// 等于给同一个目的地开了两条路：调用方既可 <c>orchestrator.TryRegister(e)</c>，
/// 也可 <c>orchestrator.ConnectionRouter.TryRegister(e)</c> 绕过编排器。
/// 后者会跳过注销时的资源释放等编排语义，只能靠注释「应当替代直接调用」打补丁。
/// 现在子组件是实现细节，编排器是唯一入口。
/// </remarks>
public interface IRouterOrchestrator {
    /// <summary>当前注册路由数量。</summary>
    int RouteCount { get; }

    /// <summary>注册路由；键冲突时返回 false。</summary>
    bool TryRegister(RouteEntry entry);

    /// <summary>按键取路由条目。</summary>
    bool TryGet(RouteKey key, out RouteEntry? entry);

    /// <summary>
    /// 移除路由并释放其传输资源。
    /// 这是注销路由的唯一正确入口——它保证「先摘表、再释放」的顺序。
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
