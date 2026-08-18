using System;
using System.Threading;
using System.Threading.Tasks;
using CommunicationKernel.Core.Abstractions.Results;
using CommunicationKernel.Engine.Router.Models;

namespace CommunicationKernel.Engine.Router.Abstractions;

public interface IReadCoordinator {
    Task<OperationResult<byte[]>> ExecuteAsync(
        ReadRequestKey requestKey,
        Func<CancellationToken, Task<OperationResult<byte[]>>> readAction,
        CancellationToken cancellationToken);
}
