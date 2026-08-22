// -----------------------------------------------------------------------------
// 文件: HostRuntimeLifecycleTests.cs
// 层级: Tests
// 作用: 覆盖 HostRuntime 的注册/注销生命周期与状态广播语义。
// 覆盖缺陷:
//   #12 并发注册同一 RouteId 的 TOCTOU 竞态（旧实现会覆盖登记项并泄漏连接）
//   #16 注销后不广播终态，导致其他客户端永远停留在"已连接"
//   #8  每次成功 I/O 都广播"状态变化"，轮询场景下形成事件风暴
//   注销后 RouteId 可被重新注册（更新设备参数依赖此路径）
// -----------------------------------------------------------------------------

using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
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
using CommunicationKernel.Engine;
using CommunicationKernel.Engine.Models;

namespace CommunicationKernel.Tests;

[TestClass]
public sealed class HostRuntimeLifecycleTests
{
    // =========================================================================
    // #12 — 并发注册竞态
    // =========================================================================

    [TestMethod]
    public async Task RegisterRoute_ConcurrentSameRouteId_OnlyOneSucceeds()
    {
        // 装配服务人为放慢，放大「检查-装配-登记」之间的竞态窗口
        var assembly = new SlowFakeAssemblyService(assembleDelayMs: 50);
        var runtime  = new HostRuntime(assembly, NewOrchestrator());

        // 8 个请求并发注册同一个 RouteId
        Task<OperationResult<string>>[] tasks = Enumerable.Range(0, 8)
            .Select(i => runtime.RegisterRouteAsync(
                NewCommand("route-A", address: $"10.0.0.{i}"), CancellationToken.None))
            .ToArray();

        OperationResult<string>[] results = await Task.WhenAll(tasks);

        int succeeded = results.Count(r => r.Success);
        Assert.AreEqual(1, succeeded,
            "同一 RouteId 并发注册应当只有一个成功，其余以 RouteBusy 拒绝");
        Assert.HasCount(1, runtime.SnapshotRoutes());
    }

    [TestMethod]
    public async Task RegisterRoute_LosersDoNotLeakTransportClients()
    {
        var assembly = new SlowFakeAssemblyService(assembleDelayMs: 50);
        var runtime  = new HostRuntime(assembly, NewOrchestrator());

        Task<OperationResult<string>>[] tasks = Enumerable.Range(0, 8)
            .Select(i => runtime.RegisterRouteAsync(
                NewCommand("route-A", address: $"10.0.0.{i}"), CancellationToken.None))
            .ToArray();

        await Task.WhenAll(tasks);

        // 每次装配都会创建一个 TransportClient；未能登记的那些必须被回滚释放，
        // 否则 socket 成为无人引用的孤儿（旧实现正是如此泄漏的）。
        int created  = assembly.CreatedClients.Count;
        int disposed = assembly.CreatedClients.Count(c => c.Disposed);

        Assert.AreEqual(created - 1, disposed,
            $"应有 {created - 1} 个落败装配被回滚释放，实际 {disposed}");
    }

    [TestMethod]
    public async Task RegisterRoute_FailedAssembly_ReleasesReservation()
    {
        var assembly = new SlowFakeAssemblyService(assembleDelayMs: 0) { FailAssembly = true };
        var runtime  = new HostRuntime(assembly, NewOrchestrator());

        OperationResult<string> first = await runtime.RegisterRouteAsync(
            NewCommand("route-A"), CancellationToken.None);
        Assert.IsFalse(first.Success);

        // 装配失败必须释放占位，否则该 RouteId 被永久占死、再也注册不上
        assembly.FailAssembly = false;
        OperationResult<string> second = await runtime.RegisterRouteAsync(
            NewCommand("route-A"), CancellationToken.None);

        Assert.IsTrue(second.Success, "装配失败后占位应已释放，允许重新注册");
    }

    [TestMethod]
    public async Task RegisterRoute_DuplicateRouteKey_IsRejectedAndRolledBack()
    {
        var assembly = new SlowFakeAssemblyService(assembleDelayMs: 0);
        var runtime  = new HostRuntime(assembly, NewOrchestrator());

        // 相同 protocol+address+port+station 构成相同 RouteKey
        Assert.IsTrue((await runtime.RegisterRouteAsync(
            NewCommand("route-A", address: "10.0.0.1"), CancellationToken.None)).Success);

        OperationResult<string> dup = await runtime.RegisterRouteAsync(
            NewCommand("route-B", address: "10.0.0.1"), CancellationToken.None);

        Assert.IsFalse(dup.Success, "指向同一物理设备的重复路由应被拒绝");
        Assert.AreEqual(KernelErrorCode.RouteBusy, dup.ErrorCode);
        Assert.IsTrue(assembly.CreatedClients[1].Disposed, "被拒绝的装配必须回滚释放连接");
    }

    // =========================================================================
    // 注销与重注册
    // =========================================================================

    [TestMethod]
    public async Task UnregisterRoute_AllowsReRegisteringSameRouteId()
    {
        var runtime = new HostRuntime(new SlowFakeAssemblyService(0), NewOrchestrator());

        Assert.IsTrue((await runtime.RegisterRouteAsync(
            NewCommand("route-A"), CancellationToken.None)).Success);

        Assert.IsTrue((await runtime.UnregisterRouteAsync(
            "route-A", CancellationToken.None)).Success);

        // 「更新设备参数」= 先注销再注册，若占位未释放此处会失败
        OperationResult<string> again = await runtime.RegisterRouteAsync(
            NewCommand("route-A", address: "10.9.9.9"), CancellationToken.None);

        Assert.IsTrue(again.Success, "注销后同名 RouteId 应可重新注册");
    }

    [TestMethod]
    public async Task UnregisterRoute_NotFound_ReturnsRouteNotFound()
    {
        var runtime = new HostRuntime(new SlowFakeAssemblyService(0), NewOrchestrator());

        OperationResult result = await runtime.UnregisterRouteAsync(
            "missing", CancellationToken.None);

        Assert.IsFalse(result.Success);
        Assert.AreEqual(KernelErrorCode.RouteNotFound, result.ErrorCode);
    }

    // =========================================================================
    // #16 — 注销广播终态
    // =========================================================================

    [TestMethod]
    public async Task UnregisterRoute_BroadcastsFinalOfflineEvent()
    {
        var runtime = new HostRuntime(new SlowFakeAssemblyService(0), NewOrchestrator());
        var events  = new List<RouteStatusSnapshot>();

        await runtime.RegisterRouteAsync(NewCommand("route-A"), CancellationToken.None);
        runtime.RouteStatusChanged += s => { lock (events) events.Add(s); };

        await runtime.UnregisterRouteAsync("route-A", CancellationToken.None);

        Assert.HasCount(1, events, "注销应恰好广播一次终态事件");
        Assert.AreEqual("route-A", events[0].RouteId);
        Assert.IsFalse(events[0].Online,
            "终态必须是离线，否则其他客户端会永远显示该设备在线");
    }

    [TestMethod]
    public async Task UnregisterRoute_RemovesStatusSnapshot()
    {
        var runtime = new HostRuntime(new SlowFakeAssemblyService(0), NewOrchestrator());

        await runtime.RegisterRouteAsync(NewCommand("route-A"), CancellationToken.None);
        Assert.HasCount(1, runtime.SnapshotStatuses("route-A"));

        await runtime.UnregisterRouteAsync("route-A", CancellationToken.None);

        Assert.IsEmpty(runtime.SnapshotStatuses("route-A"),
            "已注销路由不应在状态快照中留下幽灵条目");
    }

    // =========================================================================
    // #8 — 状态仅在变化时广播
    // =========================================================================

    [TestMethod]
    public async Task RepeatedSuccessfulReads_DoNotFloodStatusEvents()
    {
        var runtime = new HostRuntime(new SlowFakeAssemblyService(0), NewOrchestrator());
        await runtime.RegisterRouteAsync(NewCommand("route-A"), CancellationToken.None);

        int eventCount = 0;
        runtime.RouteStatusChanged += _ => Interlocked.Increment(ref eventCount);

        // 连续 20 次成功读取，状态始终为「在线」，不应产生任何状态变化事件
        for (int i = 0; i < 20; i++)
        {
            OperationResult<byte[]> read = await runtime.ReadByRouteIdAsync(
                "route-A", "40001", 2, CancellationToken.None);
            Assert.IsTrue(read.Success);
        }

        Assert.AreEqual(0, eventCount,
            "状态未变化时不得广播事件——否则轮询场景下会淹没所有订阅客户端");
    }

    // =========================================================================
    // 测试替身
    // =========================================================================

    /// <summary>构造一套真实子组件的编排器（子组件本身是纯内存实现，无需替身）。</summary>
    private static IRouterOrchestrator NewOrchestrator()
        => new RouterOrchestrator(new ConnectionRouter(), new ReadCoordinator());

    private static RegisterRouteCommand NewCommand(
        string routeId, string address = "127.0.0.1") =>
        new() {
            RouteId       = routeId,
            ProtocolId    = "fake-protocol",
            TransportKind = "Tcp",
            Address       = address,
            Port          = 502,
            Station       = "1"
        };

    /// <summary>
    /// 可控延迟的装配服务替身，用于放大注册竞态窗口并记录创建的传输客户端。
    /// </summary>
    private sealed class SlowFakeAssemblyService : IRouteAssemblyService
    {
        private readonly int _assembleDelayMs;

        public SlowFakeAssemblyService(int assembleDelayMs) => _assembleDelayMs = assembleDelayMs;

        /// <summary>置 true 时所有装配请求返回失败。</summary>
        public bool FailAssembly { get; set; }

        /// <summary>记录每次装配创建的传输客户端，用于校验回滚是否释放。</summary>
        public ConcurrentBag<TrackedTransportClient> CreatedClientsBag { get; } = new();

        /// <summary>按创建顺序返回客户端列表。</summary>
        public IReadOnlyList<TrackedTransportClient> CreatedClients =>
            CreatedClientsBag.OrderBy(c => c.Sequence).ToList();

        private int _sequence;

        public IReadOnlyList<ProtocolMetadata> GetAvailableProtocols() =>
            new[] { new ProtocolMetadata { ProtocolId = "fake-protocol", DisplayName = "Fake" } };

        public async Task<OperationResult<RouteAssemblyResult>> AssembleAsync(
            RegisterRouteCommand command, CancellationToken cancellationToken)
        {
            if (_assembleDelayMs > 0)
                await Task.Delay(_assembleDelayMs, cancellationToken).ConfigureAwait(false);

            if (FailAssembly)
                return OperationResult<RouteAssemblyResult>.Fail(
                    "assembly failed", KernelErrorCode.TransportUnavailable);

            var client = new TrackedTransportClient(Interlocked.Increment(ref _sequence));
            CreatedClientsBag.Add(client);

            var routeKey = new RouteKey(
                command.ProtocolId, TransportKind.Tcp,
                command.Address ?? string.Empty, command.Port, command.Station);

            var endpoint = new TransportEndpoint {
                Kind = TransportKind.Tcp,
                Address = command.Address ?? string.Empty,
                Port = command.Port
            };

            return OperationResult<RouteAssemblyResult>.Ok(new RouteAssemblyResult {
                RouteKey        = routeKey,
                Endpoint        = endpoint,
                TransportId     = "fake-transport",
                IsSerialRoute   = false,
                MinIoIntervalMs = 0,
                RouteEntry      = new RouteEntry {
                    Key             = routeKey,
                    TransportClient = client,
                    ProtocolDriver  = new NoopProtocolDriver()
                },
                RollbackAsync   = async ct => await client.DisposeAsync().ConfigureAwait(false)
            });
        }
    }

    /// <summary>记录是否被释放的传输客户端替身。</summary>
    private sealed class TrackedTransportClient : ITransportClient
    {
        public TrackedTransportClient(int sequence) => Sequence = sequence;

        public int  Sequence { get; }
        public bool Disposed { get; private set; }

        public string TransportId => "fake-transport";
        public TransportKind Kind => TransportKind.Tcp;

        public ValueTask DisposeAsync()
        {
            Disposed = true;
            return ValueTask.CompletedTask;
        }

        public Task<OperationResult> ConnectAsync(TransportEndpoint endpoint, CancellationToken ct)
            => Task.FromResult(OperationResult.Ok);

        public Task<OperationResult<byte[]>> SendAndReceiveAsync(byte[] request, TryGetFrameLength tryGetFrameLength, CancellationToken ct)
            => Task.FromResult(OperationResult<byte[]>.Ok(Array.Empty<byte>()));

        public Task<OperationResult> DisconnectAsync(CancellationToken ct)
            => Task.FromResult(OperationResult.Ok);
    }

    /// <summary>始终成功的协议驱动替身。</summary>
    private sealed class NoopProtocolDriver : IProtocolDriver
    {
        public ProtocolMetadata Metadata { get; } =
            new() { ProtocolId = "fake-protocol", DisplayName = "Fake", PluginApiVersion = 1 };

        public OperationResult<byte[]> BuildReadFrame(string address, int length)
            => OperationResult<byte[]>.Ok(Array.Empty<byte>());

        public OperationResult<byte[]> BuildWriteFrame(string address, byte[] payload)
            => OperationResult<byte[]>.Ok(Array.Empty<byte>());

        public Task<OperationResult<byte[]>> ReadAsync(
            ITransportClient client, string address, int length, CancellationToken ct)
            => Task.FromResult(OperationResult<byte[]>.Ok(new byte[] { 0x00, 0x01 }));

        public Task<OperationResult> WriteAsync(
            ITransportClient client, string address, byte[] payload, CancellationToken ct)
            => Task.FromResult(OperationResult.Ok);
    }
}
