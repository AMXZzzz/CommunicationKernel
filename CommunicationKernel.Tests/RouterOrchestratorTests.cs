// -----------------------------------------------------------------------------
// 文件: RouterOrchestratorTests.cs
// 层级: 测试
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

// 编排器只委托、不自实现；注销必须先摘表再释放
[TestClass]
public class RouterOrchestratorTests {

    // =========================================================================
    // 构造契约
    // =========================================================================

    // 依赖为 null 必须立刻失败，而不是在第一次调用时 NRE
    [TestMethod]
    public void Constructor_RejectsNullDependencies() {
        // ============================================================================
        // Assert
        // ============================================================================
        Assert.ThrowsExactly<ArgumentNullException>(
            () => new RouterOrchestrator(null!, new SpyReadCoordinator()));
        Assert.ThrowsExactly<ArgumentNullException>(
            () => new RouterOrchestrator(new SpyConnectionRouter(), null!));
    }

    // =========================================================================
    // 转发语义：编排器不自行实现路由表，只委托
    // =========================================================================

    // TryRegister 必须原样转发给 ConnectionRouter
    [TestMethod]
    public void TryRegister_DelegatesToConnectionRouter() {
        // ============================================================================
        // Arrange
        // ============================================================================
        var router = new SpyConnectionRouter();
        var sut = new RouterOrchestrator(router, new SpyReadCoordinator());
        RouteEntry entry = NewEntry();

        // ============================================================================
        // Act
        // ============================================================================
        bool ok = sut.TryRegister(entry);

        // ============================================================================
        // Assert
        // ============================================================================
        Assert.IsTrue(ok);
        CollectionAssert.AreEqual(new[] { "TryRegister" }, router.Calls);
        Assert.AreSame(entry, router.LastRegistered);
    }

    // 路由表拒绝时编排器必须原样传播，不得自行重试
    [TestMethod]
    public void TryRegister_PropagatesRejection() {
        // ============================================================================
        // Arrange
        // ============================================================================
        var router = new SpyConnectionRouter { RegisterResult = false };
        var sut = new RouterOrchestrator(router, new SpyReadCoordinator());

        // ============================================================================
        // Act / Assert
        // ============================================================================
        Assert.IsFalse(sut.TryRegister(NewEntry()));
    }

    // RouteCount 必须读自路由表，不得自己再维护一份计数
    [TestMethod]
    public void RouteCount_ReadsFromConnectionRouter() {
        // ============================================================================
        // Arrange
        // ============================================================================
        var router = new SpyConnectionRouter { CountValue = 7 };
        var sut = new RouterOrchestrator(router, new SpyReadCoordinator());

        // ============================================================================
        // Assert
        // ============================================================================
        Assert.AreEqual(7, sut.RouteCount);
    }

    // 读路径必须走 ReadCoordinator，否则同键合并不会生效
    [TestMethod]
    public void ExecuteReadAsync_DelegatesToReadCoordinator() {
        // ============================================================================
        // Arrange
        // ============================================================================
        var coordinator = new SpyReadCoordinator();
        var sut = new RouterOrchestrator(new SpyConnectionRouter(), coordinator);
        var key = NewReadKey();

        // ============================================================================
        // Act
        // ============================================================================
        _ = sut.ExecuteReadAsync(key, _ => Task.FromResult(OperationResult<byte[]>.Ok(new byte[] { 1 })), CancellationToken.None);

        // ============================================================================
        // Assert
        // ============================================================================
        Assert.AreEqual(1, coordinator.ExecuteCount);
        Assert.AreEqual(key, coordinator.LastKey);
    }

    // =========================================================================
    // 编排语义：注销的顺序契约
    // =========================================================================

    // 必须先从路由表摘除，再释放传输资源
    [TestMethod]
    public async Task TryRemoveAndDisposeAsync_RemovesFromTableBeforeDisposingEntry() {
        // ============================================================================
        // Arrange
        // ============================================================================
        // 顺序是编排器存在的理由。反过来（先释放再摘表）会让并发进入的
        // 读写从路由表里拿到一个已释放的 TransportClient。
        var trace = new List<string>();
        RouteEntry entry = NewEntry(onDispose: () => trace.Add("DisposeEntry"));

        var router = new SpyConnectionRouter(trace) { EntryToRemove = entry };
        var sut = new RouterOrchestrator(router, new SpyReadCoordinator());

        // ============================================================================
        // Act
        // ============================================================================
        bool removed = await sut.TryRemoveAndDisposeAsync(entry.Key, CancellationToken.None);

        // ============================================================================
        // Assert
        // ============================================================================
        Assert.IsTrue(removed);
        CollectionAssert.AreEqual(
            new[] { "TryRemove", "DisposeEntry" }, trace,
            "必须先从路由表摘除，再释放传输资源");
    }

    // 路由不存在时不应触碰任何资源
    [TestMethod]
    public async Task TryRemoveAndDisposeAsync_WhenNotFound_DoesNotDispose() {
        // ============================================================================
        // Arrange
        // ============================================================================
        var trace = new List<string>();
        var router = new SpyConnectionRouter(trace) { EntryToRemove = null };
        var sut = new RouterOrchestrator(router, new SpyReadCoordinator());

        // ============================================================================
        // Act
        // ============================================================================
        bool removed = await sut.TryRemoveAndDisposeAsync(NewKey(), CancellationToken.None);

        // ============================================================================
        // Assert
        // ============================================================================
        Assert.IsFalse(removed);
        CollectionAssert.AreEqual(new[] { "TryRemove" }, trace,
            "路由不存在时不应触碰任何资源");
    }

    // =========================================================================
    // 封装契约：子组件不得外泄
    // =========================================================================

    // 子组件必须是实现细节，暴露出去就能绕过 TryRemoveAndDisposeAsync
    [TestMethod]
    public void Interface_DoesNotExposeSubComponents() {
        // ============================================================================
        // Arrange
        // ============================================================================
        // 防回归：曾经同时提供子组件属性与转发方法，
        // 调用方可绕过 TryRemoveAndDisposeAsync 直接 ConnectionRouter.TryRemove，
        // 从而跳过资源释放。子组件必须是实现细节。
        Type t = typeof(IRouterOrchestrator);

        // ============================================================================
        // Assert
        // ============================================================================
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

        /// <summary>替身不涉及真实连接，恒为可用。</summary>
        public bool IsConnectionAlive => true;

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
