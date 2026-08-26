// -----------------------------------------------------------------------------
// 文件: ReadCoordinatorTests.cs
// 层级: 测试
// 作用: 覆盖 ReadCoordinator 的同键合并、异键隔离与失败后可恢复。
// 说明:
//   多变量轮询同一地址时，合并为一次 I/O 能避免把 PLC 打满；
//   合并失败后必须清掉在途记录，否则该键会永久卡死。
// -----------------------------------------------------------------------------

using System;
using System.Threading;
using System.Threading.Tasks;
using CommunicationKernel.Communication.Transport.Abstractions;
using CommunicationKernel.Core.Abstractions.Results;
using CommunicationKernel.Core.EngineRouter;
using CommunicationKernel.Core.EngineRouter.Models;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace CommunicationKernel.Tests;

// 读合并器：同键共享一次 I/O，异键互不干扰
[TestClass]
public sealed class ReadCoordinatorTests {

    // 同一 ReadRequestKey 的并发读必须合并为一次真实 I/O
    [TestMethod]
    public async Task SameKey_ShouldMergeInflightRead() {
        // ============================================================================
        // Arrange
        // ============================================================================
        // 两个调用共用同一路由、地址、长度
        var coordinator = new ReadCoordinator();
        var route = new RouteKey("modbus", TransportKind.Tcp, "127.0.0.1", 502, "1");
        var key = new ReadRequestKey(route, "D100", 2);

        int callCount = 0;
        async Task<OperationResult<byte[]>> Action(CancellationToken ct) {
            Interlocked.Increment(ref callCount);
            await Task.Delay(80, ct);
            return OperationResult<byte[]>.Ok(new byte[] { 0x01, 0x02 });
        }

        // ============================================================================
        // Act
        // ============================================================================
        // 在第一次尚未完成时立刻发起第二次
        Task<OperationResult<byte[]>> t1 = coordinator.ExecuteAsync(key, Action, CancellationToken.None);
        Task<OperationResult<byte[]>> t2 = coordinator.ExecuteAsync(key, Action, CancellationToken.None);
        OperationResult<byte[]>[] results = await Task.WhenAll(t1, t2);

        // ============================================================================
        // Assert
        // ============================================================================
        // 底层只执行一次，两个调用方都拿到成功结果
        Assert.AreEqual(1, callCount);
        Assert.IsTrue(results[0].Success);
        Assert.IsTrue(results[1].Success);
    }

    // 不同地址不得合并——否则会把 D101 的请求当成 D100 的响应
    [TestMethod]
    public async Task DifferentKey_ShouldNotMerge() {
        // ============================================================================
        // Arrange
        // ============================================================================
        var coordinator = new ReadCoordinator();
        var route = new RouteKey("modbus", TransportKind.Tcp, "127.0.0.1", 502, "1");
        var k1 = new ReadRequestKey(route, "D100", 2);
        var k2 = new ReadRequestKey(route, "D101", 2);

        int callCount = 0;
        async Task<OperationResult<byte[]>> Action(CancellationToken ct) {
            Interlocked.Increment(ref callCount);
            await Task.Delay(50, ct);
            return OperationResult<byte[]>.Ok(new byte[] { 0x00 });
        }

        // ============================================================================
        // Act
        // ============================================================================
        await Task.WhenAll(
            coordinator.ExecuteAsync(k1, Action, CancellationToken.None),
            coordinator.ExecuteAsync(k2, Action, CancellationToken.None));

        // ============================================================================
        // Assert
        // ============================================================================
        // 两个不同键必须各自打一次 PLC
        Assert.AreEqual(2, callCount);
    }

    // 底层抛异常必须转成 Fail，且不得留下脏的在途记录
    [TestMethod]
    public async Task ActionThrows_ShouldReturnFail_AndAllowNextCall() {
        // ============================================================================
        // Arrange
        // ============================================================================
        var coordinator = new ReadCoordinator();
        var route = new RouteKey("s7", TransportKind.Tcp, "192.168.0.2", 102, "0-1");
        var key = new ReadRequestKey(route, "DB1.DBD0", 4);

        // ============================================================================
        // Act
        // ============================================================================
        // 第一次故意抛错，第二次正常返回
        OperationResult<byte[]> first = await coordinator.ExecuteAsync(
            key,
            _ => throw new InvalidOperationException("boom"),
            CancellationToken.None);

        OperationResult<byte[]> second = await coordinator.ExecuteAsync(
            key,
            _ => Task.FromResult(OperationResult<byte[]>.Ok(new byte[] { 0x11 })),
            CancellationToken.None);

        // ============================================================================
        // Assert
        // ============================================================================
        // 失败不得污染后续：同一键必须还能再次执行
        Assert.IsFalse(first.Success);
        Assert.IsTrue(second.Success);
    }

    // 调用方取消必须映射为 Cancelled，而不是超时或 I/O 错误
    [TestMethod]
    public async Task CancelledAction_ShouldReturnCancelled() {
        // ============================================================================
        // Arrange
        // ============================================================================
        var coordinator = new ReadCoordinator();
        var route = new RouteKey("modbus", TransportKind.Tcp, "127.0.0.1", 502, "1");
        var key = new ReadRequestKey(route, "D200", 2);

        using var cts = new CancellationTokenSource();
        cts.Cancel();

        // ============================================================================
        // Act
        // ============================================================================
        OperationResult<byte[]> result = await coordinator.ExecuteAsync(
            key,
            async ct => {
                await Task.Delay(10, ct);
                return OperationResult<byte[]>.Ok(new byte[] { 0x00, 0x01 });
            },
            cts.Token);

        // ============================================================================
        // Assert
        // ============================================================================
        // 取消是调用方意愿，不得被报成链路故障
        Assert.IsFalse(result.Success);
        Assert.AreEqual("Cancelled", result.ErrorMessage);
    }
}
