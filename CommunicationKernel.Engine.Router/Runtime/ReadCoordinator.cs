// -----------------------------------------------------------------------------
// 文件: ReadCoordinator.cs
// 层级: Engine.Router / Runtime
// 作用: 协调同一读请求键的并发读取，避免多 UI 轮询重复打点同一 PLC 地址。
// -----------------------------------------------------------------------------

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

        // 读委托缺失无法打点设备，直接以参数错误失败，避免空引用进入合并表
        if (readAction is null)
            return OperationResult<byte[]>.Fail("readAction is null", KernelErrorCode.InvalidArgument);

        // 加入已有的同键在途读取，或创建新的——多 UI 轮询同一地址只打点 PLC 一次
        InflightRead inflight = Join(requestKey, readAction);

        try {
            // 各调用方用自己的令牌等待共享结果；本方等待被取消不影响其他参与者
            return await inflight.Task.WaitAsync(cancellationToken).ConfigureAwait(false);
        } catch (OperationCanceledException) {
            // 本调用方退出：递减引用计数，若已无人等待则取消底层 I/O
            inflight.Leave();
            return OperationResult<byte[]>.Fail("Cancelled", KernelErrorCode.Cancelled);
        } catch (Exception ex) {
            // 非取消异常（如共享任务本身故障）：映射为 Unknown，不误触发重连
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

        // GetOrAdd 与 TryEnter 之间存在「条目刚完成」窗口，必须循环直到成功加入
        while (true) {
            // 同键已有在途读则复用；否则工厂创建新条目（工厂可能被推测性调用，真正 I/O 延迟启动）
            InflightRead existing = _inflight.GetOrAdd(
                requestKey,
                key => new InflightRead(key, readAction, _inflight));

            // TryEnter 失败说明该条目正在收尾（已完成并即将从字典移除），
            // 重试一轮即可拿到新建的条目
            if (existing.TryEnter())
                return existing;

            // 条件移除：只删掉这个已收尾的实例，避免误删后来者刚插入的新条目
            _inflight.TryRemove(new KeyValuePair<ReadRequestKey, InflightRead>(requestKey, existing));
        }
    }

    /// <summary>
    /// 一次在途读取及其参与者引用计数。
    /// </summary>
    private sealed class InflightRead {
        // 独立取消源：与任一调用方的令牌解耦，只有全部参与者离开才取消底层 PLC 读
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

        /// <param name="key">读请求键（路由 + 地址 + 长度），相同键的并发请求会被合并。</param>
        /// <param name="readAction">真正发起协议读的委托，整个在途期内<b>只执行一次</b>。</param>
        /// <param name="owner">所属的在途表，完成后由本条目自行摘除。</param>
        public InflightRead(
            ReadRequestKey key,
            Func<CancellationToken, Task<OperationResult<byte[]>>> readAction,
            ConcurrentDictionary<ReadRequestKey, InflightRead> owner) {

            _key   = key;
            _owner = owner;
            // ExecutionAndPublication：多线程首次访问只启动一次真实 PLC 读
            _task  = new Lazy<Task<OperationResult<byte[]>>>(
                () => RunAsync(readAction), LazyThreadSafetyMode.ExecutionAndPublication);
        }

        /// <summary>共享的读取任务；首次访问时启动。</summary>
        public Task<OperationResult<byte[]>> Task => _task.Value;

        /// <summary>尝试加入；条目已完成时返回 false。</summary>
        public bool TryEnter() {
            // 已完成的条目不再接受新参与者，调用方应走 Join 重试拿到新条目
            if (Volatile.Read(ref _completed) != 0)
                return false;

            // 先占名额，再二次检查完成标志——关闭「检查与自增之间刚好完成」的窗口
            Interlocked.Increment(ref _participants);

            // 再检查一次：可能在自增之前刚好完成
            if (Volatile.Read(ref _completed) != 0) {
                // 条目已收尾：退还刚占的名额，避免引用计数泄漏导致底层 I/O 永不取消
                Interlocked.Decrement(ref _participants);
                return false;
            }
            return true;
        }

        /// <summary>某参与者放弃等待；最后一个离开时取消底层 I/O。</summary>
        public void Leave() {
            // 仍有其他 UI/调用方在等同一份结果：不取消底层 PLC 读
            if (Interlocked.Decrement(ref _participants) > 0)
                return;

            try {
                // 最后一个等待者已走：取消底层读，避免无人认领的 I/O 继续占用独占门控
                _cts.Cancel();
            } catch (ObjectDisposedException) {
                // 读取已收尾，无需取消
            }
        }

        /// <summary>
        /// 执行真正的协议读，并把结果分发给所有参与者。
        /// </summary>
        /// <remarks>
        /// 用<b>独立</b>的 CTS 驱动，不串联任何调用方的令牌：
        /// 多个调用方共享这一次读取，其中一个取消不应中断其余人的等待。
        /// 只有参与者全部退出（计数归零）时才会真正取消。
        /// </remarks>
        private async Task<OperationResult<byte[]>> RunAsync(
            Func<CancellationToken, Task<OperationResult<byte[]>>> readAction) {

            try {
                // 用独立 CTS 驱动真正的协议读；任一调用方取消自己的令牌不会传到这里
                OperationResult<byte[]> result = await readAction(_cts.Token).ConfigureAwait(false);
                // 协议驱动返回 null 视为未知故障，避免上游 NRE
                return result ?? OperationResult<byte[]>.Fail("readAction returned null", KernelErrorCode.Unknown);
            } catch (OperationCanceledException) {
                // 全部参与者离开或外部取消：映射为 Cancelled，上层不得据此重连
                return OperationResult<byte[]>.Fail("Cancelled", KernelErrorCode.Cancelled);
            } catch (Exception ex) {
                // 协议/传输抛出的未分类异常：包装后交给上层策略（重连由 EngineRuntime 判定）
                return OperationResult<byte[]>.Fail(ex.Message, KernelErrorCode.Unknown);
            } finally {
                // 标记完成，阻止新参与者加入这个即将移除的条目
                Volatile.Write(ref _completed, 1);
                // 条件移除自身：只删这个实例，避免误删 Join 重试后插入的新条目
                _owner.TryRemove(new KeyValuePair<ReadRequestKey, InflightRead>(_key, this));
                // 释放独立取消源；此后 Leave 中的 Cancel 会碰到 ObjectDisposedException
                _cts.Dispose();
            }
        }
    }
}
