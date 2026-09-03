// -----------------------------------------------------------------------------
// 文件: ConcurrencyStressTests.cs
// 层级: 测试
// 作用: 用并发压力撞引擎的时序假设，覆盖静态审查看不见的竞态。
//
// 为什么需要这一组：
//   静态扫描能查出"锁内触发事件""事件没退订"这类结构问题，但查不出
//   "两条线程恰好在这一微秒交错"导致的故障。而本系统最现实的一个场景恰恰是
//   时序性的——<b>操作员在轮询进行中删除设备</b>：
//     · 轮询线程刚 TryGet 拿到 RouteEntry，
//     · 编排线程随即摘表并 Dispose 掉它的 socket，
//     · 轮询线程带着一个已被释放的连接继续做 I/O。
//   代码注释里写明了"先摘表再释放"的顺序，但那只保证<b>新进入</b>的读写拿不到，
//   已经拿到的那一批仍在窗口里。这组测试就是去撞那个窗口。
//
// 测试原则：
//   · 断言"不该发生什么"（未捕获异常、互斥被破坏），而不是断言具体时序——
//     后者会变成偶发失败的脆弱测试；
//   · 迭代次数足够多以提高撞中概率，但整体控制在秒级，不拖慢常规回归。
// -----------------------------------------------------------------------------

using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using CommunicationKernel.Core.Abstractions.Errors;
using CommunicationKernel.Core.Abstractions.Results;
using CommunicationKernel.Core.EngineRouter.Abstractions;
using CommunicationKernel.Core.EngineRouter.Models;
using CommunicationKernel.Core.EngineRouter;
using CommunicationKernel.Core.EngineRuntime;
using CommunicationKernel.Core.EngineRuntime.Models;
using CommunicationKernel.Core.Protocol.Abstractions;
using CommunicationKernel.Core.Transport.Abstractions;

namespace CommunicationKernel.Tests;

[TestClass]
public class ConcurrencyStressTests {

    /// <summary>并发度。取远高于常见核心数，逼出交错。</summary>
    private const int Concurrency = 32;

    /// <summary>每个并发任务的迭代次数。</summary>
    private const int Iterations = 200;

    // =========================================================================
    // 一、路由门控的互斥性
    // =========================================================================

    /// <summary>
    /// 同一路由上的并发 I/O 必须严格串行。
    /// </summary>
    /// <remarks>
    /// 这是整个传输层的地基：同一个 NetworkStream / 串口句柄上两个请求交错，
    /// 收到的响应会张冠李戴——A 的请求配上 B 的响应，双方都"成功"但值全错。
    /// 用一个"进入时必须为 false"的共享标志来抓重入，比断言耗时更可靠。
    /// </remarks>
    [TestMethod]
    public async Task RouteGate_SerializesConcurrentIo_NeverOverlaps() {
        await using RouteEntry entry = NewEntry("gate-1");

        int inside = 0;
        int overlaps = 0;
        int completed = 0;

        async Task<bool> Io(CancellationToken ct) {
            // 进入临界区：此刻 inside 必须是 0，否则说明门控没挡住
            if (Interlocked.Increment(ref inside) != 1)
                Interlocked.Increment(ref overlaps);

            await Task.Yield();          // 强制让出，放大交错窗口

            Interlocked.Decrement(ref inside);
            Interlocked.Increment(ref completed);
            return true;
        }

        Task[] workers = Enumerable.Range(0, Concurrency).Select(_ => Task.Run(async () => {
            for (int i = 0; i < Iterations; i++)
                await entry.ExecuteExclusiveAsync(Io, CancellationToken.None);
        })).ToArray();

        await Task.WhenAll(workers);

        Assert.AreEqual(0, overlaps,
            "同一路由上出现了并发 I/O：门控失效意味着请求与响应会错配");
        Assert.AreEqual(Concurrency * Iterations, completed, "有 I/O 没有被执行");
    }

    /// <summary>
    /// 大量等待者被取消后，门控本身不能被破坏。
    /// </summary>
    /// <remarks>
    /// 危险形态是"没拿到锁却归还了一次"：信号量计数会被抬高，
    /// 此后互斥<b>静默失效</b>，而且不会有任何异常提示。
    /// 因此取消一批等待者之后，必须再验一遍互斥仍然成立。
    /// </remarks>
    [TestMethod]
    public async Task RouteGate_SurvivesCancelledWaiters_MutualExclusionIntact() {
        await using RouteEntry entry = NewEntry("gate-2");

        // 先占住门控，让后续所有请求都堵在 WaitAsync 上
        var hold = new TaskCompletionSource();
        Task holder = entry.ExecuteExclusiveAsync(async _ => { await hold.Task; return true; },
            CancellationToken.None);

        // 等持有者确实进去了
        await Task.Delay(50);

        // 一批注定被取消的等待者
        var cancelled = new List<Task>();
        for (int i = 0; i < Concurrency; i++) {
            var cts = new CancellationTokenSource();
            Task t = entry.ExecuteExclusiveAsync<bool>(_ => Task.FromResult(true), cts.Token);
            cts.CancelAfter(10);
            cancelled.Add(t);
        }

        // 取消会以 OperationCanceledException 结束，这是预期的
        foreach (Task t in cancelled) {
            try { await t; } catch (OperationCanceledException) { }
        }

        hold.SetResult();
        await holder;

        // 关键断言：经历过一批取消之后，互斥必须仍然成立
        int inside = 0, overlaps = 0;
        Task[] workers = Enumerable.Range(0, Concurrency).Select(_ => Task.Run(async () => {
            for (int i = 0; i < 50; i++) {
                await entry.ExecuteExclusiveAsync(async _ => {
                    if (Interlocked.Increment(ref inside) != 1)
                        Interlocked.Increment(ref overlaps);
                    await Task.Yield();
                    Interlocked.Decrement(ref inside);
                    return true;
                }, CancellationToken.None);
            }
        })).ToArray();
        await Task.WhenAll(workers);

        Assert.AreEqual(0, overlaps,
            "取消过等待者之后互斥被破坏：多半是未获取锁却执行了 Release，信号量计数被抬高");
    }

    // =========================================================================
    // 二、在途 I/O 期间摘除路由（最现实的竞态）
    // =========================================================================

    /// <summary>
    /// 一边持续读、一边反复注销并重建同一条路由，异常不得冲出内核。
    /// </summary>
    /// <remarks>
    /// 对应现场操作：轮询开着的时候删掉设备、改完再加回来。
    /// <para>
    /// 传输替身在释放后会抛 ObjectDisposedException——真实的 socket 与串口就是这个行为。
    /// 注销<b>不等待在途 I/O</b>（<c>UnregisterRouteAsync</c> 摘表后立即 Dispose），
    /// 所以「拿到登记项 → 连接被释放 → 才真正发起 I/O」这个窗口客观存在。
    /// </para>
    /// <para>
    /// <b>本测试只证明这条链路整体不漏异常，不证明内核的兜底 catch 被触发。</b>
    /// 实测中窗口极窄（替身 I/O 是同步返回的），撞中与否不稳定；
    /// 兜底 catch 由 <see cref="ThrowingDriver_KernelReturnsFailure_NotException"/>
    /// 确定性地覆盖。
    /// </para>
    /// </remarks>
    [TestMethod]
    public async Task RemoveRouteWhileIoInFlight_ExceptionNeverEscapesKernel() {
        var assembly = new ThrowingAssemblyService();
        await using var runtime = new EngineRuntime(
            assembly, new RouterOrchestrator(new ConnectionRouter(), new ReadCoordinator()));

        const string routeId = "churn-route";
        var escaped = new ConcurrentBag<Exception>();
        int reads = 0;
        using var stop = new CancellationTokenSource(TimeSpan.FromSeconds(3));

        await runtime.RegisterRouteAsync(NewCommand(routeId), CancellationToken.None);

        // 读者：持续经内核读取
        Task[] readers = Enumerable.Range(0, Concurrency / 2).Select(worker => Task.Run(async () => {
            while (!stop.IsCancellationRequested) {
                try {
                    // 无论路由在不在、传输是否已被释放，这里都必须拿到结果对象而非异常
                    _ = await runtime.ReadByRouteIdAsync(routeId, "DT0", 2, stop.Token);
                    Interlocked.Increment(ref reads);
                } catch (OperationCanceledException) {
                    // 整体停止信号，属正常收尾
                } catch (Exception ex) {
                    escaped.Add(ex);
                }
            }
        })).ToArray();

        // 搅动者：反复注销与重建，制造"在途 I/O 撞上注销"的窗口
        Task churner = Task.Run(async () => {
            while (!stop.IsCancellationRequested) {
                try {
                    await runtime.UnregisterRouteAsync(routeId, CancellationToken.None);
                    await Task.Delay(1);
                    await runtime.RegisterRouteAsync(NewCommand(routeId), CancellationToken.None);
                } catch (OperationCanceledException) {
                } catch (Exception ex) {
                    escaped.Add(ex);
                }
            }
        });

        await Task.WhenAll(readers.Append(churner));

        Assert.IsEmpty(escaped,
            "异常冲出了内核，第一个是：" + (escaped.FirstOrDefault()?.ToString() ?? "(无)"));

        // 确认压力真的跑起来了——一次 I/O 都没发生的话，上面的"无异常"毫无意义
        Assert.IsGreaterThan(0, reads, "压力测试没有真正跑到 I/O，断言无效");
    }

    /// <summary>
    /// 协议插件抛出未包装异常时，内核必须返回失败结果而不是把异常放出去。
    /// </summary>
    /// <remarks>
    /// 这是插件架构的地基性保证。内置的两个传输插件都自己 catch 了异常并转成 Fail，
    /// 但那是<b>它们自己</b>的纪律——第三方写的协议/传输插件随时可能漏掉这一层。
    /// <para>
    /// 异常一旦冲出内核，调用方（轮询循环）就整个停摆，而界面上只表现为
    /// "数据不再更新"，没有任何报错。这类静默停摆是最难查的，
    /// 所以内核必须自己兜住，不能指望插件作者的自觉。
    /// </para>
    /// <para>
    /// 与上面的搅动测试不同，这里是<b>确定性</b>触发：驱动每次都抛。
    /// </para>
    /// </remarks>
    [TestMethod]
    public async Task ThrowingDriver_KernelReturnsFailure_NotException() {
        await using var runtime = new EngineRuntime(
            new ThrowingAssemblyService(alwaysThrowInDriver: true),
            new RouterOrchestrator(new ConnectionRouter(), new ReadCoordinator()));

        const string routeId = "bad-plugin";
        await runtime.RegisterRouteAsync(NewCommand(routeId), CancellationToken.None);

        // 不加 try/catch：异常若冲出内核，这里会直接让测试失败，正是我们要钉住的
        OperationResult<byte[]> result =
            await runtime.ReadByRouteIdAsync(routeId, "DT0", 2, CancellationToken.None);

        Assert.IsFalse(result.Success, "插件抛异常时不应报成功");
        Assert.AreEqual(KernelErrorCode.TransportIoError, result.ErrorCode,
            "插件抛出的未包装异常应被归类为传输故障");
    }

    // =========================================================================
    // 三、路由表本身的并发增删查
    // =========================================================================

    /// <summary>
    /// 并发注册 / 摘除 / 查询同一批键，路由表不得损坏或抛异常。
    /// </summary>
    /// <remarks>
    /// 同一 RouteKey 的重复注册必须恰好成功一次——两套 I/O 争用同一个物理连接
    /// 是最难查的一类故障：两边都在正常收发，但彼此吃掉对方的响应。
    /// </remarks>
    [TestMethod]
    public async Task ConnectionRouter_ConcurrentChurn_StaysConsistent() {
        var router = new ConnectionRouter();
        var escaped = new ConcurrentBag<Exception>();
        RouteKey[] keys = Enumerable.Range(0, 8).Select(i => NewKey("k" + i)).ToArray();

        Task[] workers = Enumerable.Range(0, Concurrency).Select(w => Task.Run(() => {
            var rnd = new Random(w);
            for (int i = 0; i < Iterations; i++) {
                RouteKey k = keys[rnd.Next(keys.Length)];
                try {
                    switch (rnd.Next(3)) {
                        case 0:
                            router.TryRegister(new RouteEntry {
                                Key = k,
                                TransportClient = new StressTransportClient(),
                                ProtocolDriver = new StressProtocolDriver(),
                            });
                            break;
                        case 1:
                            router.TryRemove(k, out _);
                            break;
                        default:
                            router.TryGet(k, out _);
                            _ = router.Snapshot();   // 快照期间别人正在增删
                            break;
                    }
                } catch (Exception ex) {
                    escaped.Add(ex);
                }
            }
        })).ToArray();

        await Task.WhenAll(workers);

        Assert.IsEmpty(escaped,
            "并发增删查抛异常，第一个是：" + (escaped.FirstOrDefault()?.ToString() ?? "(无)"));
        Assert.IsLessThanOrEqualTo(keys.Length, router.Count, "路由表条数超过了键的总数");
    }

    /// <summary>
    /// 同一 RouteKey 被并发注册时，必须恰好成功一次。
    /// </summary>
    [TestMethod]
    public async Task ConnectionRouter_ConcurrentDuplicateRegister_ExactlyOneWins() {
        for (int round = 0; round < 50; round++) {
            var router = new ConnectionRouter();
            RouteKey key = NewKey("dup");
            int wins = 0;

            Task[] racers = Enumerable.Range(0, Concurrency).Select(_ => Task.Run(() => {
                if (router.TryRegister(new RouteEntry {
                    Key = key,
                    TransportClient = new StressTransportClient(),
                    ProtocolDriver = new StressProtocolDriver(),
                })) Interlocked.Increment(ref wins);
            })).ToArray();

            await Task.WhenAll(racers);

            Assert.AreEqual(1, wins,
                $"第 {round} 轮：同一 RouteKey 的并发注册成功了 {wins} 次，" +
                "多于一次意味着两套 I/O 会争用同一个物理连接");
            Assert.AreEqual(1, router.Count);
        }
    }

    // =========================================================================
    // 辅助
    // =========================================================================

    /// <summary>造一个用于压力测试的路由键。</summary>
    private static RouteKey NewKey(string station) =>
        new("stress", TransportKind.Tcp, "127.0.0.1", 502, station);

    /// <summary>造一条注册命令。</summary>
    private static RegisterRouteCommand NewCommand(string routeId) => new() {
        RouteId = routeId,
        ProtocolId = "stress",
        TransportKind = "Tcp",
        Address = "127.0.0.1",
        Port = 502,
        Station = "1",
    };

    /// <summary>
    /// 装配出「释放后就抛异常」的传输，用来验内核的兜底网。
    /// </summary>
    private sealed class ThrowingAssemblyService : IRouteAssemblyService {

        private readonly bool _alwaysThrowInDriver;

        /// <param name="alwaysThrowInDriver">true 时协议驱动每次读都抛异常。</param>
        public ThrowingAssemblyService(bool alwaysThrowInDriver = false)
            => _alwaysThrowInDriver = alwaysThrowInDriver;

        public IReadOnlyList<ProtocolMetadata> GetAvailableProtocols() =>
            new[] { new ProtocolMetadata { ProtocolId = "stress", DisplayName = "stress" } };

        public IReadOnlyList<SerialPortInfo> GetAvailableSerialPorts() => Array.Empty<SerialPortInfo>();

        public Task<OperationResult<RouteAssemblyResult>> AssembleAsync(
            RegisterRouteCommand command, CancellationToken cancellationToken) {

            var client = new StressTransportClient();
            var routeKey = new RouteKey(
                command.ProtocolId, TransportKind.Tcp,
                command.Address ?? string.Empty, command.Port, command.Station);

            return Task.FromResult(OperationResult<RouteAssemblyResult>.Ok(new RouteAssemblyResult {
                RouteKey = routeKey,
                Endpoint = new TransportEndpoint {
                    Kind = TransportKind.Tcp,
                    Address = command.Address ?? string.Empty,
                    Port = command.Port,
                },
                TransportId = "stress-transport",
                IsSerialRoute = false,
                MinIoIntervalMs = 0,
                RouteEntry = new RouteEntry {
                    Key = routeKey,
                    TransportClient = client,
                    ProtocolDriver = new StressProtocolDriver(_alwaysThrowInDriver),
                },
                RollbackAsync = async ct => await client.DisposeAsync().ConfigureAwait(false),
            }));
        }
    }

    /// <summary>造一条带替身传输的路由。</summary>
    private static RouteEntry NewEntry(string station) => new() {
        Key = NewKey(station),
        TransportClient = new StressTransportClient(),
        ProtocolDriver = new StressProtocolDriver(),
    };

    /// <summary>压力测试不关心分帧，给一个恒定长度的探针。</summary>
    private static bool Probe(ReadOnlySpan<byte> received, out int totalLength) {
        totalLength = 1;
        return true;
    }

    /// <summary>
    /// 会真正"被释放"的传输替身。
    /// </summary>
    /// <remarks>
    /// 释放后继续调用必须抛 <see cref="ObjectDisposedException"/>——
    /// 这正是真实 socket / 串口的行为。替身若在释放后仍老实返回成功，
    /// 就把"摘除路由撞上在途 I/O"这个最要紧的场景掩盖掉了，测试也就白做。
    /// </remarks>
    private sealed class StressTransportClient : ITransportClient {
        private volatile bool _disposed;

        public string TransportId => "stress";
        public TransportKind Kind => TransportKind.Custom;
        public bool IsConnectionAlive => !_disposed;

        public ValueTask DisposeAsync() {
            _disposed = true;
            return ValueTask.CompletedTask;
        }

        public Task<OperationResult> ConnectAsync(TransportEndpoint endpoint, CancellationToken cancellationToken)
            => Task.FromResult(OperationResult.Ok);

        public Task<OperationResult> DisconnectAsync(CancellationToken cancellationToken)
            => Task.FromResult(OperationResult.Ok);

        public Task<OperationResult<byte[]>> SendAndReceiveAsync(
            byte[] request, TryGetFrameLength tryGetFrameLength, CancellationToken cancellationToken) {
            ObjectDisposedException.ThrowIf(_disposed, this);
            return Task.FromResult(OperationResult<byte[]>.Ok(new byte[] { 0 }));
        }
    }

    /// <summary>只提供 ProtocolId 的协议替身，不组帧。</summary>
    private sealed class StressProtocolDriver : IProtocolDriver {

        private readonly bool _alwaysThrow;

        /// <param name="alwaysThrow">true 时每次读都抛未包装异常，模拟有缺陷的第三方插件。</param>
        public StressProtocolDriver(bool alwaysThrow = false) => _alwaysThrow = alwaysThrow;
        public ProtocolMetadata Metadata { get; } = new() {
            ProtocolId = "stress", DisplayName = "stress", PluginApiVersion = 1,
        };

        public OperationResult<byte[]> BuildReadFrame(string address, int length)
            => OperationResult<byte[]>.Ok(Array.Empty<byte>());

        public OperationResult<byte[]> BuildWriteFrame(string address, byte[] payload)
            => OperationResult<byte[]>.Ok(Array.Empty<byte>());

        /// <summary>
        /// 真的去碰传输。
        /// </summary>
        /// <remarks>
        /// 必须真调 <c>SendAndReceiveAsync</c>：直接返回成功的话，
        /// 就永远碰不到"连接已被释放"那条路径，整个压力场景会被架空。
        /// </remarks>
        public Task<OperationResult<byte[]>> ReadAsync(
            ITransportClient client, string address, int length, CancellationToken cancellationToken)
            => _alwaysThrow
                ? throw new InvalidOperationException("插件内部错误（测试用）")
                : client.SendAndReceiveAsync(new byte[] { 1 }, Probe, cancellationToken);

        public Task<OperationResult> WriteAsync(
            ITransportClient client, string address, byte[] payload, CancellationToken cancellationToken)
            => Task.FromResult(OperationResult.Ok);
    }
}
