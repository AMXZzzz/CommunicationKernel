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

    /// <summary>在途读取，按请求键合并。</summary>
    private readonly ConcurrentDictionary<ReadRequestKey, InflightRead> _inflight = new();

    /// <summary>
    /// 执行（或加入）一次读取。
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>取消语义</b>：底层 I/O 由独立的取消源驱动，并对参与者做引用计数——
    /// 只有当<b>全部</b>参与者都取消时才取消底层读取。
    /// </para>
    /// <para>
    /// 历史实现把第一个调用方的 <see cref="CancellationToken"/> 直接绑定给共享的 I/O：
    /// WPF 客户端关页面取消了自己的读，正在等同一份结果的 Web 客户端会被一起取消；
    /// 反过来，第二个调用方取消自己的令牌完全不起作用，它仍会一直等下去。
    /// </para>
    /// </remarks>
    public async Task<OperationResult<byte[]>> ExecuteAsync(
        ReadRequestKey requestKey,
        Func<CancellationToken, Task<OperationResult<byte[]>>> readAction,
        CancellationToken cancellationToken) {

        if (readAction is null)
            return OperationResult<byte[]>.Fail("readAction is null", KernelErrorCode.InvalidArgument);

        InflightRead inflight = Join(requestKey, readAction);

        try {
            // 各调用方用自己的令牌等待共享结果；本方等待被取消不影响其他参与者
            return await inflight.Task.WaitAsync(cancellationToken).ConfigureAwait(false);
        } catch (OperationCanceledException) {
            // 本调用方退出：递减引用计数，若已无人等待则取消底层 I/O
            inflight.Leave();
            return OperationResult<byte[]>.Fail("Cancelled", KernelErrorCode.Cancelled);
        } catch (Exception ex) {
            return OperationResult<byte[]>.Fail(ex.Message, KernelErrorCode.Unknown);
        }
    }

    /// <summary>加入已有的在途读取，或创建一个新的。</summary>
    /// <remarks>
    /// <b>创建者不享有特权</b>：它和后来者一样必须通过 <see cref="InflightRead.TryEnter"/>
    /// 登记，否则会被重复计数——创建时记 1、随后 TryEnter 再记 1，
    /// 引用计数永远降不到 0，底层 I/O 便再也不会被取消。
    /// </remarks>
    private InflightRead Join(
        ReadRequestKey requestKey,
        Func<CancellationToken, Task<OperationResult<byte[]>>> readAction) {

        while (true) {
            InflightRead existing = _inflight.GetOrAdd(
                requestKey,
                key => new InflightRead(key, readAction, _inflight));

            // TryEnter 失败说明该条目正在收尾（已完成并即将从字典移除），
            // 重试一轮即可拿到新建的条目
            if (existing.TryEnter())
                return existing;

            _inflight.TryRemove(new KeyValuePair<ReadRequestKey, InflightRead>(requestKey, existing));
        }
    }

    /// <summary>
    /// 一次在途读取及其参与者引用计数。
    /// </summary>
    private sealed class InflightRead {
        private readonly CancellationTokenSource _cts = new();
        private readonly ConcurrentDictionary<ReadRequestKey, InflightRead> _owner;
        private readonly ReadRequestKey _key;

        /// <summary>
        /// 共享读取任务。用 <see cref="Lazy{T}"/> 延迟到首个参与者登记成功后才启动，
        /// 并保证只启动一次。
        /// </summary>
        /// <remarks>
        /// 不在构造函数里直接启动：<c>ConcurrentDictionary.GetOrAdd</c> 的工厂
        /// 可能被推测性调用（该实例最终未被采纳），那样会凭空多发一次真实 I/O。
        /// </remarks>
        private readonly Lazy<Task<OperationResult<byte[]>>> _task;

        /// <summary>参与者数量；降到 0 表示无人再关心此次读取。</summary>
        private int _participants;

        /// <summary>条目是否已完成（完成后不再接受新参与者）。</summary>
        private int _completed;

        public InflightRead(
            ReadRequestKey key,
            Func<CancellationToken, Task<OperationResult<byte[]>>> readAction,
            ConcurrentDictionary<ReadRequestKey, InflightRead> owner) {

            _key   = key;
            _owner = owner;
            _task  = new Lazy<Task<OperationResult<byte[]>>>(
                () => RunAsync(readAction), LazyThreadSafetyMode.ExecutionAndPublication);
        }

        /// <summary>共享的读取任务；首次访问时启动。</summary>
        public Task<OperationResult<byte[]>> Task => _task.Value;

        /// <summary>尝试加入；条目已完成时返回 false。</summary>
        public bool TryEnter() {
            if (Volatile.Read(ref _completed) != 0)
                return false;

            Interlocked.Increment(ref _participants);

            // 再检查一次：可能在自增之前刚好完成
            if (Volatile.Read(ref _completed) != 0) {
                Interlocked.Decrement(ref _participants);
                return false;
            }
            return true;
        }

        /// <summary>某参与者放弃等待；最后一个离开时取消底层 I/O。</summary>
        public void Leave() {
            if (Interlocked.Decrement(ref _participants) > 0)
                return;

            try {
                _cts.Cancel();
            } catch (ObjectDisposedException) {
                // 读取已收尾，无需取消
            }
        }

        private async Task<OperationResult<byte[]>> RunAsync(
            Func<CancellationToken, Task<OperationResult<byte[]>>> readAction) {

            try {
                OperationResult<byte[]> result = await readAction(_cts.Token).ConfigureAwait(false);
                return result ?? OperationResult<byte[]>.Fail("readAction returned null", KernelErrorCode.Unknown);
            } catch (OperationCanceledException) {
                return OperationResult<byte[]>.Fail("Cancelled", KernelErrorCode.Cancelled);
            } catch (Exception ex) {
                return OperationResult<byte[]>.Fail(ex.Message, KernelErrorCode.Unknown);
            } finally {
                Volatile.Write(ref _completed, 1);
                _owner.TryRemove(new KeyValuePair<ReadRequestKey, InflightRead>(_key, this));
                _cts.Dispose();
            }
        }
    }
}
