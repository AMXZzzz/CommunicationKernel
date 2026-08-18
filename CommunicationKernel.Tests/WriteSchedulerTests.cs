using System;
using System.Diagnostics;
using System.Threading;
using System.Threading.Tasks;
using CommunicationKernel.Communication.Transport.Abstractions;
using CommunicationKernel.Core.Abstractions.Results;
using CommunicationKernel.Engine.Router;
using CommunicationKernel.Engine.Router.Models;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace CommunicationKernel.Tests;

[TestClass]
public sealed class WriteSchedulerTests {

    [TestMethod]
    public async Task SameRouteKey_WritesShouldBeSerialized() {
        var scheduler = new WriteScheduler();
        var key = new RouteKey("modbus", TransportKind.Tcp, "127.0.0.1", 502, "1");

        int inFlight = 0;
        int maxInFlight = 0;

        async Task<OperationResult> Action(CancellationToken ct) {
            int now = Interlocked.Increment(ref inFlight);
            if (now > maxInFlight)
                maxInFlight = now;

            await Task.Delay(120, ct);
            Interlocked.Decrement(ref inFlight);
            return OperationResult.Ok;
        }

        Task<OperationResult> t1 = scheduler.ScheduleAsync(key, Action, CancellationToken.None);
        Task<OperationResult> t2 = scheduler.ScheduleAsync(key, Action, CancellationToken.None);

        OperationResult[] results = await Task.WhenAll(t1, t2);

        Assert.IsTrue(results[0].Success);
        Assert.IsTrue(results[1].Success);
        Assert.AreEqual(1, maxInFlight);
    }

    [TestMethod]
    public async Task DifferentRouteKey_WritesCanRunInParallel() {
        var scheduler = new WriteScheduler();
        var key1 = new RouteKey("modbus", TransportKind.Tcp, "127.0.0.1", 502, "1");
        var key2 = new RouteKey("modbus", TransportKind.Tcp, "127.0.0.1", 502, "2");

        async Task<OperationResult> Action(CancellationToken ct) {
            await Task.Delay(180, ct);
            return OperationResult.Ok;
        }

        var sw = Stopwatch.StartNew();
        Task<OperationResult> t1 = scheduler.ScheduleAsync(key1, Action, CancellationToken.None);
        Task<OperationResult> t2 = scheduler.ScheduleAsync(key2, Action, CancellationToken.None);
        OperationResult[] results = await Task.WhenAll(t1, t2);
        sw.Stop();

        Assert.IsTrue(results[0].Success);
        Assert.IsTrue(results[1].Success);
        Assert.IsLessThan(sw.ElapsedMilliseconds, 320L, $"elapsed={sw.ElapsedMilliseconds}ms");
    }

    [TestMethod]
    public async Task ScheduleAsync_WhenActionThrows_ShouldReturnFail() {
        var scheduler = new WriteScheduler();
        var key = new RouteKey("s7", TransportKind.Tcp, "192.168.1.10", 102, "0-1");

        OperationResult result = await scheduler.ScheduleAsync(
            key,
            _ => throw new InvalidOperationException("boom"),
            CancellationToken.None);

        Assert.IsFalse(result.Success);
        StringAssert.Contains(result.ErrorMessage, "boom");
    }

    [TestMethod]
    public async Task ScheduleAsync_WhenCancelled_ShouldReturnCancelled() {
        var scheduler = new WriteScheduler();
        var key = new RouteKey("mewtocol", TransportKind.Serial, "", 0, "1");
        using var cts = new CancellationTokenSource();
        cts.Cancel();

        OperationResult result = await scheduler.ScheduleAsync(
            key,
            async ct => {
                await Task.Delay(10, ct);
                return OperationResult.Ok;
            },
            cts.Token);

        Assert.IsFalse(result.Success);
        Assert.AreEqual("Cancelled", result.ErrorMessage);
    }
}
