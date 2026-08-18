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
/// - 针对几十到上百台 PLC 场景，读请求会高度并发。
/// - 对“同路由+同地址+同长度”请求进行合并，可显著降低瞬时压力。
/// -----------------------------------------------------------------------------
/// </summary>
public sealed class ReadCoordinator : IReadCoordinator {
    private readonly ConcurrentDictionary<ReadRequestKey, Lazy<Task<OperationResult<byte[]>>>> _inflight = new();

    public async Task<OperationResult<byte[]>> ExecuteAsync(
        ReadRequestKey requestKey,
        Func<CancellationToken, Task<OperationResult<byte[]>>> readAction,
        CancellationToken cancellationToken) {

        // 分支1：调用方未提供读取动作，直接返回参数错误。
        if (readAction is null)
            return OperationResult<byte[]>.Fail("readAction is null", KernelErrorCode.InvalidArgument);

        // 分支2：尝试加入 in-flight 字典。
        // - 若键不存在：创建新的 Lazy<Task>，当前请求成为“发起者”。
        // - 若键已存在：复用已有 Lazy<Task>，当前请求成为“跟随者”。
        Lazy<Task<OperationResult<byte[]>>> created = _inflight.GetOrAdd(
            requestKey,
            _ => new Lazy<Task<OperationResult<byte[]>>>(() => InvokeAndCleanupAsync(requestKey, readAction, cancellationToken),
                LazyThreadSafetyMode.ExecutionAndPublication));

        try {
            // 所有并发请求共同 await 同一任务结果，实现读合并。
            return await created.Value.ConfigureAwait(false);
        } catch (OperationCanceledException) {
            // 分支3：取消请求，返回统一取消错误。
            return OperationResult<byte[]>.Fail("Cancelled", KernelErrorCode.Cancelled);
        } catch (Exception ex) {
            // 分支4：未知异常统一映射，避免异常冒泡破坏调用方流程。
            return OperationResult<byte[]>.Fail(ex.Message, KernelErrorCode.Unknown);
        }
    }

    private async Task<OperationResult<byte[]>> InvokeAndCleanupAsync(
        ReadRequestKey requestKey,
        Func<CancellationToken, Task<OperationResult<byte[]>>> readAction,
        CancellationToken cancellationToken) {
        try {
            // 执行实际读动作。
            OperationResult<byte[]> result = await readAction(cancellationToken).ConfigureAwait(false);

            // 分支5：防御 readAction 返回 null，统一转换为失败结果。
            return result ?? OperationResult<byte[]>.Fail("readAction returned null", KernelErrorCode.Unknown);
        } finally {
            // 无论成功/失败/取消，都清理 in-flight 键，避免后续请求被过期任务卡住。
            _inflight.TryRemove(requestKey, out _);
        }
    }
}
