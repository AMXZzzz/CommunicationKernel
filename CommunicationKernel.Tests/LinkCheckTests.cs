// -----------------------------------------------------------------------------
// 文件: LinkCheckTests.cs
// 层级: 测试
// 作用: 验证链路巡检能把「注册后无人读写、但已断链」的路由翻成离线。
//
// 背景（真实复现过）：
//   路由状态原本只在「注册成功」与「每次读写」时更新。杀掉 Modbus 从站后
//   等了三分钟，界面卡片仍是绿的「在线」——因为期间没有任何读写去戳它。
//   显示「在线」而实际断开，比显示离线危险得多：操作员会据此认为数据是新的。
// -----------------------------------------------------------------------------

using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using CommunicationKernel.Core.Protocol.Abstractions;
using CommunicationKernel.Core.Transport.Abstractions;
using CommunicationKernel.Core.Abstractions.Errors;
using CommunicationKernel.Core.Abstractions.Results;
using CommunicationKernel.Core.EngineRouter;
using CommunicationKernel.Core.EngineRouter.Abstractions;
using CommunicationKernel.Core.EngineRuntime;
using CommunicationKernel.Core.EngineRuntime.Models;
using CommunicationKernel.Plugins.Protocol.Modbus.Tcp;

namespace CommunicationKernel.Tests;

[TestClass]
public class LinkCheckTests {

    /// <summary>断链后，巡检必须在无任何读写的情况下把路由翻成离线。</summary>
    [TestMethod]
    public async Task LinkCheck_PublishesOffline_WhenTransportDies_WithoutAnyIo() {
        // ====================================================================
        // Arrange：注册一条路由，此后不做任何读写
        // ====================================================================
        var transport = new KillableTransportFactory();
        await using var engine = NewEngine(transport, linkCheckIntervalMs: 100);

        var offlineSeen = new TaskCompletionSource<RouteStatusSnapshot>(
            TaskCreationOptions.RunContinuationsAsynchronously);

        engine.RouteStatusChanged += s => {
            if (!s.Online) offlineSeen.TrySetResult(s);
        };

        OperationResult<string> reg = await engine.RegisterRouteAsync(NewCommand(), CancellationToken.None);
        Assert.IsTrue(reg.Success, reg.ErrorMessage);

        // ====================================================================
        // Act：模拟 PLC 掉电——连接死掉，但没有任何人去读写
        // ====================================================================
        transport.Client.Kill();

        // ====================================================================
        // Assert
        // ====================================================================
        Task finished = await Task.WhenAny(offlineSeen.Task, Task.Delay(TimeSpan.FromSeconds(5)));
        Assert.AreSame(offlineSeen.Task, finished,
            "断链后巡检没有广播离线事件——界面会一直停在「在线」");

        RouteStatusSnapshot snapshot = await offlineSeen.Task;
        Assert.IsFalse(snapshot.Online);
        Assert.AreEqual(KernelErrorCode.TransportIoError, snapshot.ErrorCode);
    }

    /// <summary>连接正常时，巡检不得制造任何状态事件。</summary>
    [TestMethod]
    public async Task LinkCheck_StaysQuiet_WhileConnectionIsHealthy() {
        var transport = new KillableTransportFactory();
        await using var engine = NewEngine(transport, linkCheckIntervalMs: 50);

        int offlineEvents = 0;
        engine.RouteStatusChanged += s => { if (!s.Online) Interlocked.Increment(ref offlineEvents); };

        Assert.IsTrue((await engine.RegisterRouteAsync(NewCommand(), CancellationToken.None)).Success);

        // 连接一直健康：巡检跑十几轮也不该冒出离线事件，
        // 否则界面会被无意义的状态抖动刷屏
        await Task.Delay(700);

        Assert.AreEqual(0, offlineEvents);
    }

    /// <summary>闲置路由必须发协议心跳，不能只 Poll 套接字。</summary>
    [TestMethod]
    public async Task LinkCheck_IdleRoute_SendsProtocolProbe() {
        var transport = new KillableTransportFactory();
        await using var engine = NewEngine(transport, linkCheckIntervalMs: 80);

        int offlineEvents = 0;
        engine.RouteStatusChanged += s => { if (!s.Online) Interlocked.Increment(ref offlineEvents); };

        Assert.IsTrue((await engine.RegisterRouteAsync(NewCommand(), CancellationToken.None)).Success);
        await Task.Delay(400);

        Assert.IsGreaterThan(0, transport.Client.ExchangeCalls,
            "闲置路由没有发协议心跳，从站空闲超时后会把 TCP 拆掉");
        Assert.AreEqual(0, offlineEvents, "心跳应答了，不该被标成离线");
    }

    /// <summary>间隔 &lt;= 0 时巡检关闭：状态完全由读写驱动。</summary>
    [TestMethod]
    public async Task LinkCheck_Disabled_WhenIntervalNotPositive() {
        var transport = new KillableTransportFactory();
        await using var engine = NewEngine(transport, linkCheckIntervalMs: 0);

        int offlineEvents = 0;
        engine.RouteStatusChanged += s => { if (!s.Online) Interlocked.Increment(ref offlineEvents); };

        Assert.IsTrue((await engine.RegisterRouteAsync(NewCommand(), CancellationToken.None)).Success);
        transport.Client.Kill();
        await Task.Delay(500);

        Assert.AreEqual(0, offlineEvents, "巡检已关闭，不该有任何巡检产生的离线事件");
    }

    // ========================================================================
    // 辅助
    // ========================================================================

    private static EngineRuntime NewEngine(ITransportFactory transport, int linkCheckIntervalMs) =>
        new(new StaticRouteAssemblyService(
                new[] { transport },
                new IProtocolDriverFactory[] { new ModbusTcpProtocolDriverFactory() }),
            new RouterOrchestrator(new ConnectionRouter(), new ReadCoordinator()),
            logger: null,
            linkCheckIntervalMs: linkCheckIntervalMs);

    private static RegisterRouteCommand NewCommand() => new() {
        RouteId       = "plc-1",
        ProtocolId    = "modbus-tcp",
        TransportKind = "Tcp",
        Address       = "192.168.1.10",
        Port          = 502,
        Station       = "1"
    };

    /// <summary>可被「拔电」的传输替身。</summary>
    private sealed class KillableTransportFactory : ITransportFactory {
        internal KillableTransportClient Client { get; } = new();

        public string TransportId => "fake-killable";
        public TransportKind Kind => TransportKind.Tcp;
        public int PluginApiVersion => 1;
        public ITransportClient CreateClient() => Client;
    }

    private sealed class KillableTransportClient : ITransportClient {
        private volatile bool _dead;

        internal void Kill() => _dead = true;

        internal int ExchangeCalls;

        public string TransportId => "fake-killable";
        public TransportKind Kind => TransportKind.Tcp;
        public bool IsConnectionAlive => !_dead;

        public Task<OperationResult> ConnectAsync(TransportEndpoint e, CancellationToken ct)
            => Task.FromResult(OperationResult.Ok);

        public Task<OperationResult> DisconnectAsync(CancellationToken ct)
            => Task.FromResult(OperationResult.Ok);

        public ValueTask DisposeAsync() => ValueTask.CompletedTask;

        public Task<OperationResult<byte[]>> SendAndReceiveAsync(
            byte[] request, TryGetFrameLength probe, CancellationToken ct)
        {
            Interlocked.Increment(ref ExchangeCalls);
            if (_dead)
                return Task.FromResult(OperationResult<byte[]>.Fail("已断开", KernelErrorCode.TransportIoError));
            // 空应答会让协议层报解析失败；探活把协议失败视为「对端还在答」
            return Task.FromResult(OperationResult<byte[]>.Ok(Array.Empty<byte>()));
        }
    }
}
