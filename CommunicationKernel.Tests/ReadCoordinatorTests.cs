using System;
using System.Threading;
using System.Threading.Tasks;
using CommunicationKernel.Communication.Transport.Abstractions;
using CommunicationKernel.Core.Abstractions.Results;
using CommunicationKernel.Engine.Router;
using CommunicationKernel.Engine.Router.Models;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace CommunicationKernel.Tests;

[TestClass]
public sealed class ReadCoordinatorTests {

    [TestMethod]
    public async Task SameKey_ShouldMergeInflightRead() {
        var coordinator = new ReadCoordinator();
        var route = new RouteKey("modbus", TransportKind.Tcp, "127.0.0.1", 502, "1");
        var key = new ReadRequestKey(route, "D100", 2);

        int callCount = 0;
        async Task<OperationResult<byte[]>> Action(CancellationToken ct) {
            Interlocked.Increment(ref callCount);
            await Task.Delay(80, ct);
            return OperationResult<byte[]>.Ok(new byte[] { 0x01, 0x02 });
        }

        Task<OperationResult<byte[]>> t1 = coordinator.ExecuteAsync(key, Action, CancellationToken.None);
        Task<OperationResult<byte[]>> t2 = coordinator.ExecuteAsync(key, Action, CancellationToken.None);
        OperationResult<byte[]>[] results = await Task.WhenAll(t1, t2);

        Assert.AreEqual(1, callCount);
        Assert.IsTrue(results[0].Success);
        Assert.IsTrue(results[1].Success);
    }

    [TestMethod]
    public async Task DifferentKey_ShouldNotMerge() {
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

        await Task.WhenAll(
            coordinator.ExecuteAsync(k1, Action, CancellationToken.None),
            coordinator.ExecuteAsync(k2, Action, CancellationToken.None));

        Assert.AreEqual(2, callCount);
    }

    [TestMethod]
    public async Task ActionThrows_ShouldReturnFail_AndAllowNextCall() {
        var coordinator = new ReadCoordinator();
        var route = new RouteKey("s7", TransportKind.Tcp, "192.168.0.2", 102, "0-1");
        var key = new ReadRequestKey(route, "DB1.DBD0", 4);

        OperationResult<byte[]> first = await coordinator.ExecuteAsync(
            key,
            _ => throw new InvalidOperationException("boom"),
            CancellationToken.None);

        OperationResult<byte[]> second = await coordinator.ExecuteAsync(
            key,
            _ => Task.FromResult(OperationResult<byte[]>.Ok(new byte[] { 0x11 })),
            CancellationToken.None);

        Assert.IsFalse(first.Success);
        Assert.IsTrue(second.Success);
    }

    [TestMethod]
    public async Task CancelledAction_ShouldReturnCancelled() {
        var coordinator = new ReadCoordinator();
        var route = new RouteKey("modbus", TransportKind.Tcp, "127.0.0.1", 502, "1");
        var key = new ReadRequestKey(route, "D200", 2);

        using var cts = new CancellationTokenSource();
        cts.Cancel();

        OperationResult<byte[]> result = await coordinator.ExecuteAsync(
            key,
            async ct => {
                await Task.Delay(10, ct);
                return OperationResult<byte[]>.Ok(new byte[] { 0x00, 0x01 });
            },
            cts.Token);

        Assert.IsFalse(result.Success);
        Assert.AreEqual("Cancelled", result.ErrorMessage);
    }
}
