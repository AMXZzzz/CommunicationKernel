// -----------------------------------------------------------------------------
// 文件: ConnectionRouterTests.cs
// 层级: 测试
// 作用: 覆盖 ConnectionRouter 路由表的注册互斥与注销语义。
// 说明:
//   路由表是引擎的身份登记册：同一 RouteKey 只能登记一次，
//   重复登记必须被拒绝，否则两条路由会争抢同一条物理连接。
// -----------------------------------------------------------------------------

using System;
using System.Threading;
using System.Threading.Tasks;
using CommunicationKernel.Communication.Protocol.Abstractions;
using CommunicationKernel.Communication.Transport.Abstractions;
using CommunicationKernel.Core.Abstractions.Results;
using CommunicationKernel.Core.EngineRouter;
using CommunicationKernel.Core.EngineRouter.Abstractions;
using CommunicationKernel.Core.EngineRouter.Models;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace CommunicationKernel.Tests;

// 用内存替身验证路由表本身，不真正连 PLC
[TestClass]
public sealed class ConnectionRouterTests {

    // 同一 RouteKey 二次登记必须失败——否则会覆盖已有连接并泄漏前者
    [TestMethod]
    public void TryRegister_SameKeyTwice_SecondShouldFail() {
        // ============================================================================
        // Arrange
        // ============================================================================
        // 构造空路由表与一条 TCP 路由键（不真正连 PLC）
        var router = new ConnectionRouter();
        var key = new RouteKey("modbus", TransportKind.Tcp, "127.0.0.1", 502, "1");

        // ============================================================================
        // Act
        // ============================================================================
        // 用同一 Key 连续登记两次
        bool first = router.TryRegister(new RouteEntry {
            Key = key,
            TransportClient = new FakeTransportClient(),
            ProtocolDriver = new FakeProtocolDriver("modbus")
        });
        bool second = router.TryRegister(new RouteEntry {
            Key = key,
            TransportClient = new FakeTransportClient(),
            ProtocolDriver = new FakeProtocolDriver("modbus")
        });

        // ============================================================================
        // Assert
        // ============================================================================
        // 第一次成功、第二次被拒绝，表中仍只有一条
        Assert.IsTrue(first);
        Assert.IsFalse(second);
        Assert.AreEqual(1, router.Count);
    }

    // 登记后再按同一 Key 移除，必须成功并清空表
    [TestMethod]
    public void TryRemove_AfterRegister_ShouldReturnTrue() {
        // ============================================================================
        // Arrange
        // ============================================================================
        // 先登记一条 S7 路由，作为待移除对象
        var router = new ConnectionRouter();
        var key = new RouteKey("s7", TransportKind.Tcp, "192.168.0.2", 102, "0-1");
        router.TryRegister(new RouteEntry {
            Key = key,
            TransportClient = new FakeTransportClient(),
            ProtocolDriver = new FakeProtocolDriver("s7")
        });

        // ============================================================================
        // Act
        // ============================================================================
        bool removed = router.TryRemove(key, out RouteEntry? entry);

        // ============================================================================
        // Assert
        // ============================================================================
        // 移除成功、条目可取回、表已空——否则注销后路由会变成幽灵
        Assert.IsTrue(removed);
        Assert.IsNotNull(entry);
        Assert.AreEqual(0, router.Count);
    }

    // 不真正发包的传输替身，只满足 ITransportClient 契约
    private sealed class FakeTransportClient : ITransportClient {
        public string TransportId => "fake";
        public TransportKind Kind => TransportKind.Custom;

        /// <summary>替身不涉及真实连接，恒为可用。</summary>
        public bool IsConnectionAlive => true;

        public ValueTask DisposeAsync() => ValueTask.CompletedTask;

        public Task<OperationResult> ConnectAsync(TransportEndpoint endpoint, CancellationToken cancellationToken)
            => Task.FromResult(OperationResult.Ok);

        public Task<OperationResult> DisconnectAsync(CancellationToken cancellationToken)
            => Task.FromResult(OperationResult.Ok);

        public Task<OperationResult<byte[]>> SendAndReceiveAsync(byte[] request, TryGetFrameLength tryGetFrameLength, CancellationToken cancellationToken)
            => Task.FromResult(OperationResult<byte[]>.Ok(Array.Empty<byte>()));
    }

    // 不真正组帧的协议替身，只携带 ProtocolId
    private sealed class FakeProtocolDriver : IProtocolDriver {
        public FakeProtocolDriver(string id) {
            Metadata = new ProtocolMetadata { ProtocolId = id, DisplayName = id, PluginApiVersion = 1 };
        }

        public ProtocolMetadata Metadata { get; }

        public OperationResult<byte[]> BuildReadFrame(string address, int length)
            => OperationResult<byte[]>.Ok(Array.Empty<byte>());

        public OperationResult<byte[]> BuildWriteFrame(string address, byte[] payload)
            => OperationResult<byte[]>.Ok(Array.Empty<byte>());

        public Task<OperationResult<byte[]>> ReadAsync(ITransportClient client, string address, int length, CancellationToken cancellationToken)
            => Task.FromResult(OperationResult<byte[]>.Ok(Array.Empty<byte>()));

        public Task<OperationResult> WriteAsync(ITransportClient client, string address, byte[] payload, CancellationToken cancellationToken)
            => Task.FromResult(OperationResult.Ok);
    }
}
