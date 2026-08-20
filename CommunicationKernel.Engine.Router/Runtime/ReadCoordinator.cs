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
/// 文件: ReadCoordinator.cs
/// 层级: Engine.Router
/// 作用: 协调同一读请求键的并发读取，避免重复打点设备。
/// 说明:
/// - 读合并键为 (RouteKey, DataAddress, Length)。
///   设计决策：不同 Length 的同地址请求视为独立请求，不做跨长度合并——
///   合并需要剪切响应字节，引入额外复杂度，与当前场景不符。
/// - 对"同路由+同地址+同长度"请求合并为单次 IO，所有并发调用共享结果。
/// -----------------------------------------------------------------------------
/// </summary>
public sealed class ReadCoordinator : IReadCoordinator {
    private readonly ConcurrentDictionary<ReadRequestKey, Lazy<Task<OperationResult<byte[]>>>> _inflight = new();

    public async Task<OperationResult<byte[]>> ExecuteAsync(
        ReadRequestKey requestKey,
        Func<CancellationToken, Task<OperationResult<byte[]>>> readAction,
        CancellationToken cancellationToken) {

        if (readAction is null)
            return OperationResult<byte[]>.Fail("readAction is null", KernelErrorCode.InvalidArgument);

        Lazy<Task<OperationResult<byte[]>>> created = _inflight.GetOrAdd(
            requestKey,
            _ => new Lazy<Task<OperationResult<byte[]>>>(
                () => InvokeAndCleanupAsync(requestKey, readAction, cancellationToken),
                LazyThreadSafetyMode.ExecutionAndPublication));

        try {
            return await created.Value.ConfigureAwait(false);
        } catch (OperationCanceledException) {
            return OperationResult<byte[]>.Fail("Cancelled", KernelErrorCode.Cancelled);
        } catch (Exception ex) {
            return OperationResult<byte[]>.Fail(ex.Message, KernelErrorCode.Unknown);
        }
    }

    private async Task<OperationResult<byte[]>> InvokeAndCleanupAsync(
        ReadRequestKey requestKey,
        Func<CancellationToken, Task<OperationResult<byte[]>>> readAction,
        CancellationToken cancellationToken) {
        try {
            OperationResult<byte[]> result = await readAction(cancellationToken).ConfigureAwait(false);
            return result ?? OperationResult<byte[]>.Fail("readAction returned null", KernelErrorCode.Unknown);
        } finally {
            _inflight.TryRemove(requestKey, out _);
        }
    }
}
