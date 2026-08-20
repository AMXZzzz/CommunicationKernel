using System;
using System.Collections.Concurrent;
using System.Threading;
using System.Threading.Tasks;
using CommunicationKernel.Core.Abstractions.Errors;
using CommunicationKernel.Core.Abstractions.Results;
using CommunicationKernel.Engine.Router.Abstractions;
using CommunicationKernel.Engine.Router.Models;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace CommunicationKernel.Engine.Router;

/// <summary>
/// 以 RouteKey 为粒度对写操作串行化调度。
/// 同一路由写入串行，不同路由并行，满足多设备（几十/上百 PLC）吞吐需求。
/// </summary>
public sealed class WriteScheduler : IWriteScheduler {
    private readonly ConcurrentDictionary<RouteKey, SemaphoreSlim> _routeLocks = new();
    private readonly ILogger<WriteScheduler> _logger;

    public WriteScheduler(ILogger<WriteScheduler>? logger = null) {
        _logger = logger ?? NullLogger<WriteScheduler>.Instance;
    }

    public async Task<OperationResult> ScheduleAsync(
        RouteKey routeKey,
        Func<CancellationToken, Task<OperationResult>> writeAction,
        CancellationToken cancellationToken) {

        if (writeAction is null)
            return OperationResult.InvalidArgument("writeAction is null");

        SemaphoreSlim routeLock = _routeLocks.GetOrAdd(routeKey, _ => new SemaphoreSlim(1, 1));

        bool acquired = false;
        try {
            await routeLock.WaitAsync(cancellationToken).ConfigureAwait(false);
            acquired = true;

            OperationResult result = await writeAction(cancellationToken).ConfigureAwait(false);
            return result ?? OperationResult.Fail("writeAction returned null", KernelErrorCode.Unknown);
        } catch (OperationCanceledException) {
            return OperationResult.Cancelled;
        } catch (Exception ex) {
            _logger.LogError(ex, "WriteScheduler: unhandled exception on route {RouteKey}", routeKey);
            return OperationResult.Fail(ex.Message, KernelErrorCode.Unknown);
        } finally {
            if (acquired)
                routeLock.Release();
        }
    }

    /// <summary>
    /// 路由注销时移除并释放对应信号量，防止资源泄漏。
    /// </summary>
    public void Release(RouteKey routeKey) {
        if (_routeLocks.TryRemove(routeKey, out SemaphoreSlim? sem)) {
            sem.Dispose();
            _logger.LogDebug("WriteScheduler: released semaphore for route {RouteKey}", routeKey);
        }
    }
}
