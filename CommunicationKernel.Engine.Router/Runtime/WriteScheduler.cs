using System;
using System.Collections.Concurrent;
using System.Threading;
using System.Threading.Tasks;
using CommunicationKernel.Core.Abstractions.Errors;
using CommunicationKernel.Core.Abstractions.Results;
using CommunicationKernel.Engine.Router.Abstractions;
using CommunicationKernel.Engine.Router.Models;

namespace CommunicationKernel.Engine.Router;

/// <summary>
/// -----------------------------------------------------------------------------
/// 文件: WriteScheduler.cs
/// 层级: Engine.Router
/// 作用: 以 RouteKey 为粒度对写操作串行化调度。
/// 说明:
/// - 同一路由（同设备会话）写入串行，避免协议会话并发冲突。
/// - 不同路由并行，满足多设备（几十/上百 PLC）吞吐需求。
/// -----------------------------------------------------------------------------
/// </summary>
public sealed class WriteScheduler : IWriteScheduler {
    private readonly ConcurrentDictionary<RouteKey, SemaphoreSlim> _routeLocks = new();

    public async Task<OperationResult> ScheduleAsync(
        RouteKey routeKey,
        Func<CancellationToken, Task<OperationResult>> writeAction,
        CancellationToken cancellationToken) {

        // 分支1：写动作为空，直接返回参数错误。
        if (writeAction is null)
            return OperationResult.InvalidArgument("writeAction is null");

        // 每个路由一个信号量：同路由串行、跨路由并行。
        SemaphoreSlim routeLock = _routeLocks.GetOrAdd(routeKey, _ => new SemaphoreSlim(1, 1));

        try {
            // 进入临界区：确保当前路由同一时刻仅一个写操作执行。
            await routeLock.WaitAsync(cancellationToken).ConfigureAwait(false);

            // 执行真实写入动作。
            OperationResult result = await writeAction(cancellationToken).ConfigureAwait(false);

            // 分支2：防御 writeAction 返回 null，统一映射为失败结果。
            return result ?? OperationResult.Fail("writeAction returned null", KernelErrorCode.Unknown);
        } catch (OperationCanceledException) {
            // 分支3：请求取消，返回统一取消结果。
            return OperationResult.Cancelled;
        } catch (Exception ex) {
            // 分支4：未知异常统一映射，避免异常外抛影响批量调度。
            return OperationResult.Fail(ex.Message, KernelErrorCode.Unknown);
        } finally {
            // 收尾：只有在当前持锁状态下才释放，避免重复释放导致计数异常。
            if (routeLock.CurrentCount == 0)
                routeLock.Release();
        }
    }
}
