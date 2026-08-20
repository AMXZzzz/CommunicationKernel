using System;
using System.Threading;
using System.Threading.Tasks;
using CommunicationKernel.Core.Abstractions.Results;
using CommunicationKernel.Engine.Router.Models;

namespace CommunicationKernel.Engine.Router.Abstractions;

public interface IWriteScheduler {
    /// <summary>对指定路由的写操作串行化调度。</summary>
    Task<OperationResult> ScheduleAsync(
        RouteKey routeKey,
        Func<CancellationToken, Task<OperationResult>> writeAction,
        CancellationToken cancellationToken);

    /// <summary>
    /// 路由注销时调用：移除并释放该路由对应的信号量。
    /// 必须在确认无任务持有该路由写锁后调用。
    /// </summary>
    void Release(RouteKey routeKey);
}
