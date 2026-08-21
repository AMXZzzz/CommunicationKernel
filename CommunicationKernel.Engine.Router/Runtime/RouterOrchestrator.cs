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

        ConnectionRouter = connectionRouter ?? throw new ArgumentNullException(nameof(connectionRouter));
        ReadCoordinator  = readCoordinator  ?? throw new ArgumentNullException(nameof(readCoordinator));
    }

    /// <inheritdoc />
    public IConnectionRouter ConnectionRouter { get; }

    /// <inheritdoc />
    public IReadCoordinator ReadCoordinator { get; }

    /// <inheritdoc />
    public int RouteCount => ConnectionRouter.Count;

    /// <inheritdoc />
    public bool TryRegister(RouteEntry entry) => ConnectionRouter.TryRegister(entry);

    /// <inheritdoc />
    public bool TryGet(RouteKey key, out RouteEntry? entry) => ConnectionRouter.TryGet(key, out entry);

    /// <inheritdoc />
    public async Task<bool> TryRemoveAndDisposeAsync(RouteKey key, CancellationToken cancellationToken) {
        if (!ConnectionRouter.TryRemove(key, out RouteEntry? entry) || entry is null)
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
        => ReadCoordinator.ExecuteAsync(requestKey, readAction, cancellationToken);
}
