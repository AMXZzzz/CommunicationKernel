// -----------------------------------------------------------------------------
// 文件: RouteEntry.cs
// 层级: Engine.Router / Abstractions
// 作用: 路由条目，持有传输客户端、协议驱动，以及读写共用的独占 I/O 门控。
// -----------------------------------------------------------------------------

using System;
using System.Threading;
using System.Threading.Tasks;
using CommunicationKernel.Communication.Protocol.Abstractions;
using CommunicationKernel.Communication.Transport.Abstractions;
using CommunicationKernel.Engine.Router.Models;

namespace CommunicationKernel.Engine.Router.Abstractions;

/// <summary>
/// -----------------------------------------------------------------------------
/// 文件: RouteEntry.cs
/// 层级: Engine.Router / Abstractions
/// 作用: 路由条目，持有特定路由的传输客户端、协议驱动与独占 I/O 门控。
/// 说明:
/// - 实现 IAsyncDisposable：路由注销时必须调用 DisposeAsync 释放 TransportClient，
///   否则底层 TCP socket / SerialPort 句柄泄漏。
/// - 调用方（EngineRuntime.UnregisterRouteAsync）负责在 TryRemove 后调用 DisposeAsync。
/// -----------------------------------------------------------------------------
/// </summary>
public sealed class RouteEntry : IAsyncDisposable {
    /// <summary>
    /// 本路由的独占 I/O 门控。<b>读与写共用同一把锁。</b>
    /// </summary>
    /// <remarks>
    /// <para>
    /// 一条路由对应一个物理连接（一个 NetworkStream 或一个串口句柄），
    /// 而 <c>NetworkStream</c> 与 <c>SerialPort</c> 都<b>不支持并发读</b>。
    /// 轮询 N 个不同地址的变量时，若不串行化，N 个 <c>SendAndReceiveAsync</c>
    /// 会同时在同一个流上读写：请求字节交织、响应被别的调用方读走。
    /// </para>
    /// <para>
    /// 历史实现只在写路径串行化（WriteScheduler），读路径仅做「同键合并」，
    /// 不同地址的读之间毫无互斥——多变量轮询下几乎必然串数据。
    /// 现统一由本门控覆盖读写两条路径。
    /// </para>
    /// <para>
    /// 该信号量随 RouteEntry 生命周期存在，不单独释放：
    /// <see cref="SemaphoreSlim"/> 不持有非托管资源，仅在未使用 <c>AvailableWaitHandle</c>
    /// 时可由 GC 直接回收。刻意不调用 <c>Dispose</c>，正是为了避免
    /// 「注销路由时释放正在被使用的信号量、导致在途 I/O 的 Release 抛
    /// ObjectDisposedException」这一竞态。
    /// </para>
    /// </remarks>
    private readonly SemaphoreSlim _ioGate = new(1, 1);

    /// <summary>路由键。</summary>
    public required RouteKey Key { get; init; }

    /// <summary>传输客户端（socket / 串口）。</summary>
    public required ITransportClient TransportClient { get; init; }

    /// <summary>协议驱动。</summary>
    public required IProtocolDriver ProtocolDriver { get; init; }

    /// <summary>
    /// 本路由要求的最小 I/O 间隔（毫秒）。
    /// 串口链路需要帧间静默窗口；TCP 链路通常为 0。
    /// </summary>
    public int MinIoIntervalMs { get; init; }

    /// <summary>上一次 I/O 完成时刻（UTC ticks），用于最小间隔节流。</summary>
    private long _lastIoCompletedUtcTicks = DateTimeOffset.MinValue.UtcTicks;

    /// <summary>
    /// 在本路由的独占门控下执行一次 I/O。
    /// </summary>
    /// <remarks>
    /// 进入门控后先补足 <see cref="MinIoIntervalMs"/> 要求的静默间隔，
    /// 再执行 <paramref name="ioAction"/>；无论成功失败都会归还门控并记录完成时刻。
    /// 节流等待期间若被取消，门控在此方法内部归还，不会泄漏。
    /// </remarks>
    /// <typeparam name="TResult">I/O 操作的返回类型。</typeparam>
    /// <param name="ioAction">受门控保护的实际 I/O 操作。</param>
    /// <param name="cancellationToken">取消令牌。</param>
    public async Task<TResult> ExecuteExclusiveAsync<TResult>(
        Func<CancellationToken, Task<TResult>> ioAction,
        CancellationToken cancellationToken) {

        // 拒绝空委托：否则拿到门控后无法执行，会空占物理连接
        ArgumentNullException.ThrowIfNull(ioAction);

        // 抢占本路由的独占门控：同一 NetworkStream / 串口上读写必须串行
        await _ioGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try {
            // 串口从站需要帧间静默窗口；TCP 的 MinIoIntervalMs 通常为 0，跳过
            if (MinIoIntervalMs > 0) {
                // 距上次 I/O 完成已过多久；从未执行过则视为间隔已足够
                int elapsed = GetElapsedSinceLastIoMs();
                int delay   = MinIoIntervalMs - elapsed;
                // 静默窗口尚未结束：补足剩余毫秒，避免从站把连续帧当粘包
                if (delay > 0)
                    await Task.Delay(delay, cancellationToken).ConfigureAwait(false);
            }

            // 在独占期内执行真正的协议读写（SendAndReceive）
            return await ioAction(cancellationToken).ConfigureAwait(false);
        } finally {
            // 无论成功、失败还是取消，都记下完成时刻并归还门控，避免后续 I/O 永久阻塞
            Interlocked.Exchange(ref _lastIoCompletedUtcTicks, DateTimeOffset.UtcNow.UtcTicks);
            _ioGate.Release();
        }
    }

    /// <summary>距上一次 I/O 完成经过的毫秒数；从未执行过时返回 <see cref="int.MaxValue"/>。</summary>
    private int GetElapsedSinceLastIoMs() {
        // 无锁读取上次完成时刻，避免与 finally 中的写入竞争
        long ticks = Interlocked.Read(ref _lastIoCompletedUtcTicks);
        // 哨兵值：本路由尚未做过任何 I/O，无需等待静默窗口
        if (ticks == DateTimeOffset.MinValue.UtcTicks)
            return int.MaxValue;

        // 把 UTC ticks 转成已过毫秒，并钳位到 int 范围供 Delay 使用
        TimeSpan elapsed = DateTimeOffset.UtcNow - new DateTimeOffset(ticks, TimeSpan.Zero);
        return (int)Math.Clamp(elapsed.TotalMilliseconds, 0, int.MaxValue);
    }

    /// <summary>
    /// 本路由是否刚做过 I/O（含心跳）。用于跳过多余探活，避免轮询中的路由再打一枪。
    /// </summary>
    public bool HasCompletedIoWithin(int milliseconds)
    {
        if (milliseconds <= 0) return false;
        int elapsed = GetElapsedSinceLastIoMs();
        return elapsed < milliseconds;
    }

    /// <summary>
    /// 释放传输客户端底层资源（socket / 串口句柄）。
    /// 必须在路由从路由表移除后调用。
    /// </summary>
    public async ValueTask DisposeAsync() {
        try {
            // 关闭 TCP socket / 串口句柄；必须在摘表之后，避免并发读写拿到已释放的客户端
            await TransportClient.DisposeAsync().ConfigureAwait(false);
        } catch (Exception) {
            // 释放阶段异常不应传播：已移除的路由资源最大努力释放即可。
        }
    }
}
