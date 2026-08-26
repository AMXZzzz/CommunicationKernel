// -----------------------------------------------------------------------------
// 文件: IReadCoordinator.cs
// 层级: Core.EngineRouter / Abstractions
// 作用: 读合并契约——相同 (路由, 地址, 长度) 的并发读合成一次设备 I/O。
// -----------------------------------------------------------------------------

using System;
using System.Threading;
using System.Threading.Tasks;
using CommunicationKernel.Core.Abstractions.Results;
using CommunicationKernel.Core.EngineRouter.Models;

namespace CommunicationKernel.Core.EngineRouter.Abstractions;

// ============================================================================
// 读合并契约
// ============================================================================

public interface IReadCoordinator {
    // 执行或加入一次读取：同键并发调用共享单次 PLC 读，各自用自己的取消令牌等待
    Task<OperationResult<byte[]>> ExecuteAsync(
        ReadRequestKey requestKey,
        Func<CancellationToken, Task<OperationResult<byte[]>>> readAction,
        CancellationToken cancellationToken);
}
