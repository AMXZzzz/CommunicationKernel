// -----------------------------------------------------------------------------
// 文件: RouteIoGateTests.cs
// 层级: 测试
// 作用: 路由独占 I/O 门控与读合并取消语义的并发测试。
// 防回归重点（对应审查报告 P0）：
//   P0-02 同一路由的并发读写必须串行，不得在同一物理流上交织
//   P0-11 注销路由不得让在途 I/O 的门控归还抛异常
//   P0-12 读合并的底层 I/O 不得绑定第一个调用方的取消令牌
// -----------------------------------------------------------------------------

using System;
using System.Collections.Concurrent;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using CommunicationKernel.Communication.Protocol.Abstractions;
using CommunicationKernel.Communication.Transport.Abstractions;
using CommunicationKernel.Core.Abstractions.Errors;
using CommunicationKernel.Core.Abstractions.Results;
using CommunicationKernel.Engine.Router;
using CommunicationKernel.Engine.Router.Abstractions;
using CommunicationKernel.Engine.Router.Models;

namespace CommunicationKernel.Tests;

// =============================================================================
// P0-02：路由独占门控
// =============================================================================

// 同一物理连接上的 I/O 必须串行；NetworkStream / SerialPort 都不支持并发读
[TestClass]
public class RouteIoGateTests {

    // 20 个并发操作任意时刻只能有一个在执行
    [TestMethod]
    public async Task ConcurrentOperations_OnSameRoute_NeverOverlap() {
        // ============================================================================
        // Arrange
        // ============================================================================
        RouteEntry entry = NewEntry(minIoIntervalMs: 0);

        int concurrent = 0;
        int maxObserved = 0;

        async Task<OperationResult<byte[]>> Io(CancellationToken ct) {
            int now = Interlocked.Increment(ref concurrent);
            InterlockedMax(ref maxObserved, now);
            await Task.Delay(15, ct).ConfigureAwait(false);
            Interlocked.Decrement(ref concurrent);
            return OperationResult<byte[]>.Ok(Array.Empty<byte>());
        }

        // ============================================================================
        // Act
        // ============================================================================
        // 20 个并发操作模拟多变量轮询同一条路由
        await Task.WhenAll(Enumerable.Range(0, 20)
            .Select(_ => entry.ExecuteExclusiveAsync(Io, CancellationToken.None)));

        // ============================================================================
        // Assert
        // ============================================================================
        Assert.AreEqual(1, maxObserved,
            "同一路由对应一个物理连接，NetworkStream / SerialPort 都不支持并发读，" +
            "任意时刻只能有一个 I/O 在执行");
    }

    // 读与写必须共用同一把锁；历史实现只串行化写路径
    [TestMethod]
    public async Task ReadsAndWrites_ShareTheSameGate() {
        // ============================================================================
        // Arrange
        // ============================================================================
        RouteEntry entry = NewEntry(minIoIntervalMs: 0);

        int concurrent = 0;
        int maxObserved = 0;

        async Task<T> Io<T>(T result, CancellationToken ct) {
            int now = Interlocked.Increment(ref concurrent);
            InterlockedMax(ref maxObserved, now);
            await Task.Delay(10, ct).ConfigureAwait(false);
            Interlocked.Decrement(ref concurrent);
            return result;
        }

        // ============================================================================
        // Act
        // ============================================================================
        // 读写混合：历史实现只串行化写路径，读路径毫无互斥
        Task[] mixed = Enumerable.Range(0, 10)
            .Select(i => i % 2 == 0
                ? entry.ExecuteExclusiveAsync(ct => Io(OperationResult<byte[]>.Ok(Array.Empty<byte>()), ct), CancellationToken.None)
                : (Task)entry.ExecuteExclusiveAsync(ct => Io(OperationResult.Ok, ct), CancellationToken.None))
            .ToArray();

        await Task.WhenAll(mixed);

        // ============================================================================
        // Assert
        // ============================================================================
        Assert.AreEqual(1, maxObserved, "读与写必须共用同一把锁");
    }

    // 串口帧间静默窗口必须被补足，否则对端会把两帧粘成一帧
    [TestMethod]
    public async Task MinIoInterval_IsEnforcedBetweenOperations() {
        // ============================================================================
        // Arrange
        // ============================================================================
        RouteEntry entry = NewEntry(minIoIntervalMs: 40);

        var stamps = new ConcurrentBag<long>();

        async Task<OperationResult<byte[]>> Io(CancellationToken ct) {
            stamps.Add(Environment.TickCount64);
            await Task.Yield();
            return OperationResult<byte[]>.Ok(Array.Empty<byte>());
        }

        // ============================================================================
        // Act
        // ============================================================================
        for (int i = 0; i < 3; i++)
            await entry.ExecuteExclusiveAsync(Io, CancellationToken.None);

        // ============================================================================
        // Assert
        // ============================================================================
        long[] ordered = stamps.OrderBy(x => x).ToArray();
        for (int i = 1; i < ordered.Length; i++) {
            Assert.IsGreaterThanOrEqualTo(30, ordered[i] - ordered[i - 1],
                "串口帧间静默窗口必须被补足（留 10ms 计时容差）");
        }
    }

    // 节流等待被取消时必须归还门控，否则该路此后所有读写永久挂起
    [TestMethod]
    public async Task CancellationDuringThrottle_DoesNotLeakTheGate() {
        // ============================================================================
        // Arrange
        // ============================================================================
        // 历史缺陷：节流等待在 try/finally 之外获取信号量，
        // Task.Delay 被取消时不归还 → 该路由此后所有读写永久挂起
        RouteEntry entry = NewEntry(minIoIntervalMs: 500);

        // 先做一次 I/O，使后续操作必须等待静默窗口
        await entry.ExecuteExclusiveAsync(
            _ => Task.FromResult(OperationResult<byte[]>.Ok(Array.Empty<byte>())),
            CancellationToken.None);

        // ============================================================================
        // Act
        // ============================================================================
        using var cts = new CancellationTokenSource(20);
        try {
            await entry.ExecuteExclusiveAsync(
                _ => Task.FromResult(OperationResult<byte[]>.Ok(Array.Empty<byte>())),
                cts.Token);
        } catch (OperationCanceledException) {
            // 预期：节流等待期间被取消
        }

        // 门控必须已归还——否则这一步会永久挂起
        Task<OperationResult<byte[]>> next = entry.ExecuteExclusiveAsync(
            _ => Task.FromResult(OperationResult<byte[]>.Ok(Array.Empty<byte>())),
            CancellationToken.None);

        // ============================================================================
        // Assert
        // ============================================================================
        Task completed = await Task.WhenAny(next, Task.Delay(3000));
        Assert.AreSame(next, completed, "取消后门控必须归还，否则路由死锁");
    }

    // 注销时不得 Dispose 正在被持有的信号量，在途 I/O 必须正常收尾
    [TestMethod]
    public async Task UnregisterDuringInflightIo_DoesNotThrow() {
        // ============================================================================
        // Arrange
        // ============================================================================
        // 历史缺陷：注销时 Dispose 正在被持有的信号量，
        // 在途 I/O 的 finally 中 Release 会抛 ObjectDisposedException 且不在 try 内
        var router = new ConnectionRouter();
        var orchestrator = new RouterOrchestrator(router, new ReadCoordinator());

        RouteEntry entry = NewEntry(minIoIntervalMs: 0);
        Assert.IsTrue(orchestrator.TryRegister(entry));

        var started = new TaskCompletionSource();
        var release = new TaskCompletionSource();

        Task<OperationResult<byte[]>> inflight = entry.ExecuteExclusiveAsync(async ct => {
            started.SetResult();
            await release.Task.ConfigureAwait(false);
            return OperationResult<byte[]>.Ok(Array.Empty<byte>());
        }, CancellationToken.None);

        await started.Task;

        // ============================================================================
        // Act
        // ============================================================================
        // I/O 在途时注销路由
        bool removed = await orchestrator.TryRemoveAndDisposeAsync(entry.Key, CancellationToken.None);
        Assert.IsTrue(removed);

        release.SetResult();

        // ============================================================================
        // Assert
        // ============================================================================
        // 在途 I/O 必须正常收尾，不得抛出
        OperationResult<byte[]> result = await inflight;
        Assert.IsTrue(result.Success);
    }

    // -------------------------------------------------------------------------

    private static RouteEntry NewEntry(int minIoIntervalMs) => new() {
        Key             = new RouteKey("fake", TransportKind.Tcp, "127.0.0.1", 502, "1"),
        TransportClient = new NoopTransportClient(),
        ProtocolDriver  = new NoopProtocolDriver(),
        MinIoIntervalMs = minIoIntervalMs
    };

    private static void InterlockedMax(ref int target, int value) {
        int current;
        while (value > (current = Volatile.Read(ref target))) {
            if (Interlocked.CompareExchange(ref target, value, current) == current)
                return;
        }
    }

    private sealed class NoopTransportClient : ITransportClient {
        public string TransportId => "fake";
        public TransportKind Kind => TransportKind.Tcp;
        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
        public Task<OperationResult> ConnectAsync(TransportEndpoint e, CancellationToken ct) => Task.FromResult(OperationResult.Ok);
        public Task<OperationResult> DisconnectAsync(CancellationToken ct) => Task.FromResult(OperationResult.Ok);
        public Task<OperationResult<byte[]>> SendAndReceiveAsync(byte[] r, TryGetFrameLength p, CancellationToken ct)
            => Task.FromResult(OperationResult<byte[]>.Ok(Array.Empty<byte>()));
    }

    private sealed class NoopProtocolDriver : IProtocolDriver {
        public ProtocolMetadata Metadata { get; } = new() { ProtocolId = "fake", DisplayName = "Fake", PluginApiVersion = 1 };
        public OperationResult<byte[]> BuildReadFrame(string a, int l) => OperationResult<byte[]>.Ok(Array.Empty<byte>());
        public OperationResult<byte[]> BuildWriteFrame(string a, byte[] p) => OperationResult<byte[]>.Ok(Array.Empty<byte>());
        public Task<OperationResult<byte[]>> ReadAsync(ITransportClient c, string a, int l, CancellationToken ct)
            => Task.FromResult(OperationResult<byte[]>.Ok(Array.Empty<byte>()));
        public Task<OperationResult> WriteAsync(ITransportClient c, string a, byte[] p, CancellationToken ct)
            => Task.FromResult(OperationResult.Ok);
    }
}

// =============================================================================
// P0-12：读合并的取消语义
// =============================================================================

// 共享 I/O 不得绑定第一个调用方的令牌，否则关一个页面会取消所有等待者
[TestClass]
public class ReadCoordinatorCancellationTests {

    // 同键 8 路并发必须合并为单次 I/O
    [TestMethod]
    public async Task SameKeyConcurrentReads_ShareASingleIo() {
        // ============================================================================
        // Arrange
        // ============================================================================
        var coordinator = new ReadCoordinator();
        var key = NewKey();
        int invocations = 0;

        async Task<OperationResult<byte[]>> Read(CancellationToken ct) {
            Interlocked.Increment(ref invocations);
            await Task.Delay(50, ct).ConfigureAwait(false);
            return OperationResult<byte[]>.Ok(new byte[] { 1, 2 });
        }

        // ============================================================================
        // Act
        // ============================================================================
        OperationResult<byte[]>[] results = await Task.WhenAll(
            Enumerable.Range(0, 8).Select(_ => coordinator.ExecuteAsync(key, Read, CancellationToken.None)));

        // ============================================================================
        // Assert
        // ============================================================================
        Assert.AreEqual(1, invocations, "同键并发读应合并为单次 I/O");
        Assert.IsTrue(results.All(r => r.Success));
    }

    // 第一个调用方取消不得波及其余参与者
    [TestMethod]
    public async Task FirstCallerCancelling_DoesNotCancelOtherParticipants() {
        // ============================================================================
        // Arrange
        // ============================================================================
        // 历史缺陷：共享 I/O 直接绑定第一个调用方的令牌，
        // WPF 关页面取消自己的读，会把正在等同一份结果的 Web 客户端一起取消
        var coordinator = new ReadCoordinator();
        var key = NewKey();
        var proceed = new TaskCompletionSource();

        async Task<OperationResult<byte[]>> Read(CancellationToken ct) {
            await proceed.Task.ConfigureAwait(false);
            ct.ThrowIfCancellationRequested();
            return OperationResult<byte[]>.Ok(new byte[] { 0xAB });
        }

        using var firstCts = new CancellationTokenSource();

        // ============================================================================
        // Act
        // ============================================================================
        Task<OperationResult<byte[]>> first  = coordinator.ExecuteAsync(key, Read, firstCts.Token);
        await Task.Delay(30);
        Task<OperationResult<byte[]>> second = coordinator.ExecuteAsync(key, Read, CancellationToken.None);

        // 第一个调用方放弃
        firstCts.Cancel();
        await Task.Delay(30);
        proceed.SetResult();

        OperationResult<byte[]> firstResult  = await first;
        OperationResult<byte[]> secondResult = await second;

        // ============================================================================
        // Assert
        // ============================================================================
        Assert.AreEqual(KernelErrorCode.Cancelled, firstResult.ErrorCode);
        Assert.IsTrue(secondResult.Success, "其余参与者不应被第一个调用方的取消波及");
        Assert.AreEqual((byte)0xAB, secondResult.Value[0]);
    }

    // 全部参与者都取消后应取消底层 I/O，避免无人等待的读继续占用链路
    [TestMethod]
    public async Task AllParticipantsCancelling_CancelsUnderlyingIo() {
        // ============================================================================
        // Arrange
        // ============================================================================
        var coordinator = new ReadCoordinator();
        var key = NewKey();
        var observed = new TaskCompletionSource<bool>();

        async Task<OperationResult<byte[]>> Read(CancellationToken ct) {
            try {
                await Task.Delay(Timeout.Infinite, ct).ConfigureAwait(false);
                return OperationResult<byte[]>.Ok(Array.Empty<byte>());
            } catch (OperationCanceledException) {
                observed.TrySetResult(true);
                throw;
            }
        }

        using var a = new CancellationTokenSource();
        using var b = new CancellationTokenSource();

        // ============================================================================
        // Act
        // ============================================================================
        Task<OperationResult<byte[]>> ta = coordinator.ExecuteAsync(key, Read, a.Token);
        await Task.Delay(20);
        Task<OperationResult<byte[]>> tb = coordinator.ExecuteAsync(key, Read, b.Token);

        a.Cancel();
        b.Cancel();

        await Task.WhenAll(ta, tb);

        // ============================================================================
        // Assert
        // ============================================================================
        Task completed = await Task.WhenAny(observed.Task, Task.Delay(2000));
        Assert.AreSame(observed.Task, completed, "全部参与者取消后应取消底层 I/O，避免无人等待的读继续占用链路");
    }

    private static ReadRequestKey NewKey() =>
        new(new RouteKey("fake", TransportKind.Tcp, "127.0.0.1", 502, "1"), "40001", 2);
}
