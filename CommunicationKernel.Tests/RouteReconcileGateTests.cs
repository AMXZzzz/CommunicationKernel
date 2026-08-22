// -----------------------------------------------------------------------------
// 文件: RouteReconcileGateTests.cs
// 层级: Tests
// 作用: 验证宿主重启后自动重注册的两条时序保证。
//
// 场景还原：
//   Host.App 的路由是纯内存的，进程一重启全部消失。此时一台设备上挂着的
//   几十个变量会在同一瞬间全部收到 RouteNotFound。
//   若逐个发起重注册，宿主一秒内就要处理几十次同一条路由的 RegisterRoute；
//   若失败后不节流，每个轮询周期都会再来一轮，把失败放大成持续请求风暴。
// -----------------------------------------------------------------------------

using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using CommunicationKernel.Host.Sdk;

namespace CommunicationKernel.Tests;

[TestClass]
public class RouteReconcileGateTests {

    [TestMethod]
    public async Task ConcurrentCalls_OnSameRoute_CollapseToSingleInvocation() {
        var gate = new RouteReconcileGate(TimeSpan.Zero);

        int invocations = 0;
        var release = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);

        async Task<bool> Operation() {
            Interlocked.Increment(ref invocations);
            return await release.Task;
        }

        // 50 个变量同时发现路由不存在
        Task<bool>[] callers = Enumerable.Range(0, 50)
            .Select(_ => gate.RunAsync("plc-1", Operation))
            .ToArray();

        release.SetResult(true);
        bool[] results = await Task.WhenAll(callers);

        Assert.AreEqual(1, invocations, "50 个并发请求必须合并成一次实际注册调用");
        Assert.IsTrue(results.All(r => r), "合并后的调用者都应拿到同一个成功结果");
    }

    [TestMethod]
    public async Task ConcurrentCalls_OnDifferentRoutes_RunIndependently() {
        // 合并只应发生在同一条路由内部。不同设备之间互不影响，
        // 否则一台设备卡住会拖住其余全部设备的恢复。
        var gate = new RouteReconcileGate(TimeSpan.Zero);

        var invoked = new List<string>();
        var sync = new object();

        Task<bool> Operation(string id) => gate.RunAsync(id, async () => {
            await Task.Yield();
            lock (sync) invoked.Add(id);
            return true;
        });

        await Task.WhenAll(Operation("plc-1"), Operation("plc-2"), Operation("plc-3"));

        CollectionAssert.AreEquivalent(
            new[] { "plc-1", "plc-2", "plc-3" }, invoked);
    }

    [TestMethod]
    public async Task SecondCall_WithinMinInterval_IsThrottled_WithoutInvoking() {
        // PLC 拔线时重注册也会失败。没有最小间隔，每个轮询周期都会再打一次。
        DateTime now = new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc);
        var gate = new RouteReconcileGate(TimeSpan.FromSeconds(5), () => now);

        int invocations = 0;
        Task<bool> Operation() => gate.RunAsync("plc-1", async () => {
            await Task.Yield();
            Interlocked.Increment(ref invocations);
            return false;                       // 注册失败（PLC 不可达）
        });

        Assert.IsFalse(await Operation());
        Assert.AreEqual(1, invocations);

        // 4 秒后再来一次：仍在 5 秒窗口内，必须直接判失败且不发起调用
        now = now.AddSeconds(4);
        Assert.IsFalse(await Operation());
        Assert.AreEqual(1, invocations, "节流窗口内不得发起第二次实际调用");
    }

    [TestMethod]
    public async Task Call_AfterMinInterval_InvokesAgain() {
        // 节流是限速，不是永久熔断——过了窗口必须重新尝试，
        // 否则 PLC 修好后设备再也不会自行恢复。
        DateTime now = new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc);
        var gate = new RouteReconcileGate(TimeSpan.FromSeconds(5), () => now);

        int invocations = 0;
        Task<bool> Operation(bool result) => gate.RunAsync("plc-1", async () => {
            await Task.Yield();
            Interlocked.Increment(ref invocations);
            return result;
        });

        Assert.IsFalse(await Operation(false));

        now = now.AddSeconds(6);
        Assert.IsTrue(await Operation(true), "过了节流窗口应重新尝试并可成功");
        Assert.AreEqual(2, invocations);
    }

    [TestMethod]
    public async Task FailedInvocation_DoesNotLeaveStaleInflightEntry() {
        // 在途记录若因异常没被摘掉，该路由此后会永远复用一个已完成的失败任务，
        // 再也无法重新注册——且不报任何错。
        DateTime now = new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc);
        var gate = new RouteReconcileGate(TimeSpan.Zero, () => now);

        await Assert.ThrowsExactlyAsync<InvalidOperationException>(
            () => gate.RunAsync("plc-1", () => throw new InvalidOperationException("注册炸了")));

        // 同一路由必须还能再次发起
        bool second = await gate.RunAsync("plc-1", () => Task.FromResult(true));
        Assert.IsTrue(second, "上一次抛异常后在途记录未清理，路由被永久卡死");
    }

    [TestMethod]
    public async Task CancelledInvocation_ReportsFailure_AndClearsInflight() {
        var gate = new RouteReconcileGate(TimeSpan.Zero);

        bool cancelled = await gate.RunAsync("plc-1",
            () => throw new OperationCanceledException());

        Assert.IsFalse(cancelled, "取消按失败处理，由调用方继续退避");

        bool retry = await gate.RunAsync("plc-1", () => Task.FromResult(true));
        Assert.IsTrue(retry);
    }

    [TestMethod]
    public async Task BlankRouteId_IsRejected_WithoutInvoking() {
        var gate = new RouteReconcileGate(TimeSpan.Zero);

        int invocations = 0;
        Task<bool> Operation(string id) => gate.RunAsync(id, () => {
            Interlocked.Increment(ref invocations);
            return Task.FromResult(true);
        });

        Assert.IsFalse(await Operation(null!));
        Assert.IsFalse(await Operation(""));
        Assert.IsFalse(await Operation("   "));
        Assert.AreEqual(0, invocations);
    }

    [TestMethod]
    public async Task RouteId_IsMatchedCaseInsensitively() {
        // 路由 ID 在 gRPC 契约与本地配置之间来回传递，大小写不应产生两条独立记录，
        // 否则合并与节流都会被绕过。
        var gate = new RouteReconcileGate(TimeSpan.FromSeconds(5));

        int invocations = 0;
        Task<bool> Operation(string id) => gate.RunAsync(id, async () => {
            await Task.Yield();
            Interlocked.Increment(ref invocations);
            return false;
        });

        Assert.IsFalse(await Operation("PLC-1"));
        Assert.IsFalse(await Operation("plc-1"));

        Assert.AreEqual(1, invocations, "大小写不同的同一路由必须共用一条节流记录");
    }
}
