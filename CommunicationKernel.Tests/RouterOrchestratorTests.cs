// -----------------------------------------------------------------------------
// 文件: RouterOrchestratorTests.cs
// 层级: Tests
// 作用: 用替身隔离验证 RouterOrchestrator 的编排语义本身。
//
// 为什么要用替身：
//   IConnectionRouter / IReadCoordinator 此前虽已抽象出来，却从无测试替身——
//   属于"缝已经开好，但没人从这个缝进去"。编排逻辑（注册、注销、释放顺序）
//   因此只能连同真实 ConnectionRouter 一起测，无法断言"以什么顺序调用了谁"。
//
//   本文件让这两个接口从"预留的缝"变成"承重的缝"：
//   替身记录调用序列，使顺序契约成为可断言的行为，
//   而不是只存在于注释里的约定。
// -----------------------------------------------------------------------------

using System;
using System.Collections.Generic;
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

[TestClass]
public class RouterOrchestratorTests {

    // =========================================================================
    // 构造契约
    // =========================================================================

    [TestMethod]
    public void Constructor_RejectsNullDependencies() {
        Assert.ThrowsExactly<ArgumentNullException>(
            () => new RouterOrchestrator(null!, new SpyReadCoordinator()));
        Assert.ThrowsExactly<ArgumentNullException>(
            () => new RouterOrchestrator(new SpyConnectionRouter(), null!));
    }

    // =========================================================================
    // 转发语义：编排器不自行实现路由表，只委托
    // =========================================================================

    [TestMethod]
    public void TryRegister_DelegatesToConnectionRouter() {
        var router = new SpyConnectionRouter();
        var sut = new RouterOrchestrator(router, new SpyReadCoordinator());
        RouteEntry entry = NewEntry();

        bool ok = sut.TryRegister(entry);

        Assert.IsTrue(ok);
        CollectionAssert.AreEqual(new[] { "TryRegister" }, router.Calls);
        Assert.AreSame(entry, router.LastRegistered);
    }

    [TestMethod]
    public void TryRegister_PropagatesRejection() {
        var router = new SpyConnectionRouter { RegisterResult = false };
        var sut = new RouterOrchestrator(router, new SpyReadCoordinator());

        Assert.IsFalse(sut.TryRegister(NewEntry()));
    }

    [TestMethod]
    public void RouteCount_ReadsFromConnectionRouter() {
        var router = new SpyConnectionRouter { CountValue = 7 };
        var sut = new RouterOrchestrator(router, new SpyReadCoordinator());

        Assert.AreEqual(7, sut.RouteCount);
    }

    [TestMethod]
    public void ExecuteReadAsync_DelegatesToReadCoordinator() {
        var coordinator = new SpyReadCoordinator();
        var sut = new RouterOrchestrator(new SpyConnectionRouter(), coordinator);
        var key = NewReadKey();

        _ = sut.ExecuteReadAsync(key, _ => Task.FromResult(OperationResult<byte[]>.Ok(new byte[] { 1 })), CancellationToken.None);

        Assert.AreEqual(1, coordinator.ExecuteCount);
        Assert.AreEqual(key, coordinator.LastKey);
    }

    // =========================================================================
    // 编排语义：注销的顺序契约
    // =========================================================================

    [TestMethod]
    public async Task TryRemoveAndDisposeAsync_RemovesFromTableBeforeDisposingEntry() {
        // 顺序是编排器存在的理由。反过来（先释放再摘表）会让并发进入的
        // 读写从路由表里拿到一个已释放的 TransportClient。
        var trace = new List<string>();
        RouteEntry entry = NewEntry(onDispose: () => trace.Add("DisposeEntry"));

        var router = new SpyConnectionRouter(trace) { EntryToRemove = entry };
        var sut = new RouterOrchestrator(router, new SpyReadCoordinator());

        bool removed = await sut.TryRemoveAndDisposeAsync(entry.Key, CancellationToken.None);

        Assert.IsTrue(removed);
        CollectionAssert.AreEqual(
            new[] { "TryRemove", "DisposeEntry" }, trace,
            "必须先从路由表摘除，再释放传输资源");
    }

    [TestMethod]
    public async Task TryRemoveAndDisposeAsync_WhenNotFound_DoesNotDispose() {
        var trace = new List<string>();
        var router = new SpyConnectionRouter(trace) { EntryToRemove = null };
        var sut = new RouterOrchestrator(router, new SpyReadCoordinator());

        bool removed = await sut.TryRemoveAndDisposeAsync(NewKey(), CancellationToken.None);

        Assert.IsFalse(removed);
        CollectionAssert.AreEqual(new[] { "TryRemove" }, trace,
            "路由不存在时不应触碰任何资源");
    }

    // =========================================================================
    // 封装契约：子组件不得外泄
    // =========================================================================

    [TestMethod]
    public void Interface_DoesNotExposeSubComponents() {
        // 防回归：曾经同时提供子组件属性与转发方法，
        // 调用方可绕过 TryRemoveAndDisposeAsync 直接 ConnectionRouter.TryRemove，
        // 从而跳过资源释放。子组件必须是实现细节。
        Type t = typeof(IRouterOrchestrator);

        Assert.IsNull(t.GetProperty("ConnectionRouter"),
            "IRouterOrchestrator 不应暴露 ConnectionRouter——那是绕过编排语义的旁路");
        Assert.IsNull(t.GetProperty("ReadCoordinator"),
            "IRouterOrchestrator 不应暴露 ReadCoordinator");
    }

    // =========================================================================
    // 替身
    // =========================================================================

    /// <summary>记录调用序列的路由表替身。</summary>
    private sealed class SpyConnectionRouter : IConnectionRouter {
        private readonly List<string> _trace;

        public SpyConnectionRouter(List<string>? trace = null) => _trace = trace ?? new List<string>();

        public List<string> Calls => _trace;
        public bool RegisterResult { get; init; } = true;
        public int CountValue { get; init; }
        public RouteEntry? EntryToRemove { get; init; }
        public RouteEntry? LastRegistered { get; private set; }

        public int Count => CountValue;

        public bool TryRegister(RouteEntry entry) {
            _trace.Add("TryRegister");
            LastRegistered = entry;
            return RegisterResult;
        }

        public bool TryGet(RouteKey key, out RouteEntry? entry) {
            _trace.Add("TryGet");
            entry = EntryToRemove;
            return entry is not null;
        }

        public bool TryRemove(RouteKey key, out RouteEntry? entry) {
            _trace.Add("TryRemove");
            entry = EntryToRemove;
            return entry is not null;
        }

        public IReadOnlyList<RouteEntry> Snapshot() {
            _trace.Add("Snapshot");
            return EntryToRemove is null
                ? Array.Empty<RouteEntry>()
                : new[] { EntryToRemove };
        }
    }

    /// <summary>记录调用次数的读合并替身。</summary>
    private sealed class SpyReadCoordinator : IReadCoordinator {
        public int ExecuteCount { get; private set; }
        public ReadRequestKey LastKey { get; private set; }

        public Task<OperationResult<byte[]>> ExecuteAsync(
            ReadRequestKey requestKey,
            Func<CancellationToken, Task<OperationResult<byte[]>>> readAction,
            CancellationToken cancellationToken) {

            ExecuteCount++;
            LastKey = requestKey;
            return readAction(cancellationToken);
        }
    }

    // =========================================================================
    // 测试数据
    // =========================================================================

    private static RouteKey NewKey() =>
        new("fake", TransportKind.Tcp, "127.0.0.1", 502, "1");

    private static ReadRequestKey NewReadKey() => new(NewKey(), "40001", 2);

    private static RouteEntry NewEntry(Action? onDispose = null) => new() {
        Key             = NewKey(),
        TransportClient = new TraceTransportClient(onDispose),
        ProtocolDriver  = new NoopDriver()
    };

    private sealed class TraceTransportClient : ITransportClient {
        private readonly Action? _onDispose;
        public TraceTransportClient(Action? onDispose) => _onDispose = onDispose;

        public string TransportId => "fake";
        public TransportKind Kind => TransportKind.Tcp;

        public ValueTask DisposeAsync() {
            _onDispose?.Invoke();
            return ValueTask.CompletedTask;
        }

        public Task<OperationResult> ConnectAsync(TransportEndpoint e, CancellationToken ct)
            => Task.FromResult(OperationResult.Ok);
        public Task<OperationResult> DisconnectAsync(CancellationToken ct)
            => Task.FromResult(OperationResult.Ok);
        public Task<OperationResult<byte[]>> SendAndReceiveAsync(byte[] r, TryGetFrameLength p, CancellationToken ct)
            => Task.FromResult(OperationResult<byte[]>.Ok(Array.Empty<byte>()));
    }

    private sealed class NoopDriver : IProtocolDriver {
        public ProtocolMetadata Metadata { get; } =
            new() { ProtocolId = "fake", DisplayName = "Fake", PluginApiVersion = 1 };
        public OperationResult<byte[]> BuildReadFrame(string a, int l) => OperationResult<byte[]>.Ok(Array.Empty<byte>());
        public OperationResult<byte[]> BuildWriteFrame(string a, byte[] p) => OperationResult<byte[]>.Ok(Array.Empty<byte>());
        public Task<OperationResult<byte[]>> ReadAsync(ITransportClient c, string a, int l, CancellationToken ct)
            => Task.FromResult(OperationResult<byte[]>.Ok(Array.Empty<byte>()));
        public Task<OperationResult> WriteAsync(ITransportClient c, string a, byte[] p, CancellationToken ct)
            => Task.FromResult(OperationResult.Ok);
    }
}
