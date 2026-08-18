using System;
using System.Threading;
using System.Threading.Tasks;
using CommunicationKernel.Core.Abstractions.Results;
using CommunicationKernel.Engine.Router.Models;

namespace CommunicationKernel.Engine.Router.Abstractions;

public interface IWriteScheduler {
    Task<OperationResult> ScheduleAsync(
        RouteKey routeKey,
        Func<CancellationToken, Task<OperationResult>> writeAction,
        CancellationToken cancellationToken);
}
