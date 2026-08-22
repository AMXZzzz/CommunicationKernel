// -----------------------------------------------------------------------------
// 文件: RouterOrchestrator.cs
// 层级: Engine.Router / Runtime
// 作用: 聚合路由表与读合并的编排门面，是路由层唯一对外入口。
// -----------------------------------------------------------------------------

using System;
using System.Threading;
using System.Threading.Tasks;
using CommunicationKernel.Core.Abstractions.Results;
using CommunicationKernel.Engine.Router.Abstractions;
using CommunicationKernel.Engine.Router.Models;

namespace CommunicationKernel.Engine.Router;

/// <summary>
/// -----------------------------------------------------------------------------
/// 文件: RouterOrchestrator.cs
/// 层级: Engine.Router
/// 作用: 聚合路由表与读合并的编排门面。
/// 说明:
/// - 子组件通过构造注入，可在测试中替换为假实现，也可替换合并策略。
///   历史实现在构造函数里 new 出全部具体类型，属于"面向接口声明、面向实现构造"，
///   依赖倒置只做到表面一层。
/// - 读写串行化由 RouteEntry 的独占门控承担，不在本类。
/// -----------------------------------------------------------------------------
/// </summary>
public sealed class RouterOrchestrator : IRouterOrchestrator {

    /// <param name="connectionRouter">路由表实现。</param>
    /// <param name="readCoordinator">读合并实现。</param>
    public RouterOrchestrator(
        IConnectionRouter connectionRouter,
        IReadCoordinator readCoordinator) {

        // 两个子组件均为必填：缺路由表无法登记，缺读合并则同地址并发会重复打点 PLC
        _connectionRouter = connectionRouter ?? throw new ArgumentNullException(nameof(connectionRouter));
        _readCoordinator  = readCoordinator  ?? throw new ArgumentNullException(nameof(readCoordinator));
    }

    /// <summary>路由表。作为实现细节持有，不对外暴露以免绕过编排语义。</summary>
    private readonly IConnectionRouter _connectionRouter;

    /// <summary>读取合并协调器。同为实现细节。</summary>
    private readonly IReadCoordinator _readCoordinator;

    /// <inheritdoc />
    public int RouteCount => _connectionRouter.Count;

    /// <inheritdoc />
    public bool TryRegister(RouteEntry entry) => _connectionRouter.TryRegister(entry);

    /// <inheritdoc />
    public bool TryGet(RouteKey key, out RouteEntry? entry) => _connectionRouter.TryGet(key, out entry);

    /// <inheritdoc />
    public async Task<bool> TryRemoveAndDisposeAsync(RouteKey key, CancellationToken cancellationToken) {
        // 顺序是编排语义的核心：必须先从路由表摘除，再释放传输资源。
        // 反过来会让并发进入的读写拿到一个已释放的 TransportClient。
        if (!_connectionRouter.TryRemove(key, out RouteEntry? entry) || entry is null)
            return false;

        // 路由的 I/O 门控随 RouteEntry 一起被 GC 回收，不单独 Dispose——
        // 释放正在被在途 I/O 持有的信号量会让其 finally 中的 Release 抛
        // ObjectDisposedException，这正是历史实现的一处未捕获异常来源。
        await entry.DisposeAsync().ConfigureAwait(false);
        return true;
    }

    /// <inheritdoc />
    public Task<OperationResult<byte[]>> ExecuteReadAsync(
        ReadRequestKey requestKey,
        Func<CancellationToken, Task<OperationResult<byte[]>>> readAction,
        CancellationToken cancellationToken)
        // 同 (路由, 地址, 长度) 的并发读合成一次设备 I/O，各自用自己的令牌等待
        => _readCoordinator.ExecuteAsync(requestKey, readAction, cancellationToken);
}
