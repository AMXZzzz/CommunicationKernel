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
/// 以 RouteKey 为粒度对写操作串行化调度。
/// 同一路由写入串行，不同路由并行，满足多设备（几十/上百 PLC）吞吐需求。
/// </summary>
public sealed class WriteScheduler : IWriteScheduler
{
    private readonly ConcurrentDictionary<RouteKey, SemaphoreSlim> _routeLocks = new();

    public async Task<OperationResult> ScheduleAsync(
        RouteKey routeKey,
        Func<CancellationToken, Task<OperationResult>> writeAction,
        CancellationToken cancellationToken)
    {
        if (writeAction is null)
            return OperationResult.InvalidArgument("writeAction is null");

        // 每个路由一个信号量：同路由串行、跨路由并行
        SemaphoreSlim routeLock = _routeLocks.GetOrAdd(routeKey, _ => new SemaphoreSlim(1, 1));

        bool acquired = false;
        try
        {
            await routeLock.WaitAsync(cancellationToken).ConfigureAwait(false);
            acquired = true;

            OperationResult result = await writeAction(cancellationToken).ConfigureAwait(false);
            return result ?? OperationResult.Fail("writeAction returned null", KernelErrorCode.Unknown);
        }
        catch (OperationCanceledException)
        {
            return OperationResult.Cancelled;
        }
        catch (Exception ex)
        {
            return OperationResult.Fail(ex.Message, KernelErrorCode.Unknown);
        }
        finally
        {
            // 只在成功获取锁后才释放，防止未持锁时重复释放
            if (acquired)
                routeLock.Release();
        }
    }
}
