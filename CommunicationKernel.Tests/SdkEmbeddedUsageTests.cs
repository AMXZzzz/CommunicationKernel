// -----------------------------------------------------------------------------
// 文件: SdkEmbeddedUsageTests.cs
// 层级: 测试
// 作用: 验证内核可作为 SDK 嵌入使用——不经 gRPC、不依赖插件目录。
//
// 对应场景：树莓派 / Linux 上的上位机程序直连 PLC。
// 该场景下没有独立宿主进程，也不会在输出目录摆一个 plugins 文件夹，
// 工厂由消费者在编译期直接提供。
// -----------------------------------------------------------------------------

using System;
using System.Collections.Generic;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using CommunicationKernel.Core.Protocol.Abstractions;
using CommunicationKernel.Core.Transport.Abstractions;
using CommunicationKernel.Core.Abstractions.Errors;
using CommunicationKernel.Core.Abstractions.Results;
using CommunicationKernel.Core.EngineRuntime;
using CommunicationKernel.Core.EngineRuntime.Models;
using CommunicationKernel.Core.EngineRouter;
using CommunicationKernel.Core.EngineRouter.Abstractions;
using CommunicationKernel.Plugins.Protocol.Modbus.Tcp;

namespace CommunicationKernel.Tests;

// 嵌入式 SDK：编译期注入工厂，走完整注册-读-写-注销闭环
[TestClass]
public class SdkEmbeddedUsageTests {

    // 静态装配不得触碰文件系统，协议清单来自编译期注入的工厂
    [TestMethod]
    public void StaticAssembly_ExposesProtocols_WithoutPluginDirectory() {
        // ============================================================================
        // Arrange
        // ============================================================================
        // 关键：全程不触碰文件系统
        var assembly = new StaticRouteAssemblyService(
            transportFactories: new ITransportFactory[] { new FakeTransportFactory() },
            protocolFactories:  new IProtocolDriverFactory[] { new ModbusTcpProtocolDriverFactory() });

        // ============================================================================
        // Act
        // ============================================================================
        IReadOnlyList<ProtocolMetadata> protocols = assembly.GetAvailableProtocols();

        // ============================================================================
        // Assert
        // ============================================================================
        Assert.HasCount(1, protocols);
        Assert.AreEqual("modbus-tcp", protocols[0].ProtocolId);
    }

    // 完整嵌入式流程：构造 → 注册 → 读 → 写 → 注销
    [TestMethod]
    public async Task EmbeddedEngine_CompletesRegisterReadWriteCycle() {
        // ============================================================================
        // Arrange
        // ============================================================================
        // 完整的嵌入式使用流程：构造 → 注册 → 读 → 写 → 注销
        var transport = new FakeTransportFactory();
        var assembly = new StaticRouteAssemblyService(
            transportFactories: new ITransportFactory[] { transport },
            protocolFactories:  new IProtocolDriverFactory[] { new ModbusTcpProtocolDriverFactory() });

        await using var engine = new EngineRuntime(
            assembly,
            new RouterOrchestrator(new ConnectionRouter(), new ReadCoordinator()));

        // ============================================================================
        // Act / Assert
        // ============================================================================
        OperationResult<string> registered = await engine.RegisterRouteAsync(
            new RegisterRouteCommand {
                RouteId       = "plc-1",
                ProtocolId    = "modbus-tcp",
                TransportKind = "Tcp",
                Address       = "192.168.1.10",
                Port          = 502,
                Station       = "1"
            },
            CancellationToken.None);

        Assert.IsTrue(registered.Success, registered.ErrorMessage);
        Assert.AreEqual(1, engine.RouteCount);

        // 读：伪传输回放一个合法的 MBAP + FC03 响应
        transport.NextResponse = BuildModbusReadResponse(unitId: 1, data: new byte[] { 0x12, 0x34 });
        OperationResult<byte[]> read = await engine.ReadByRouteIdAsync(
            "plc-1", "40001", 2, CancellationToken.None);

        Assert.IsTrue(read.Success, read.ErrorMessage);
        CollectionAssert.AreEqual(new byte[] { 0x12, 0x34 }, read.Value);

        // 写：回放 FC06 回显响应
        transport.NextResponse = BuildModbusWriteEcho(unitId: 1);
        OperationResult write = await engine.WriteByRouteIdAsync(
            "plc-1", "40001", new byte[] { 0x00, 0x2A }, CancellationToken.None);

        Assert.IsTrue(write.Success, write.ErrorMessage);

        Assert.IsTrue((await engine.UnregisterRouteAsync("plc-1", CancellationToken.None)).Success);
        Assert.AreEqual(0, engine.RouteCount);
    }

    // 协议与介质不匹配必须在注册期拒绝，而不是等到首次读写
    [TestMethod]
    public async Task EmbeddedEngine_RejectsProtocolTransportMismatch() {
        // ============================================================================
        // Arrange
        // ============================================================================
        // Modbus TCP 的 MBAP 封装依赖 TCP，配到串口上应在注册期即被拒绝，
        // 而不是等到首次读写才以无关的错误暴露
        var assembly = new StaticRouteAssemblyService(
            transportFactories: new ITransportFactory[] { new FakeTransportFactory(TransportKind.Serial) },
            protocolFactories:  new IProtocolDriverFactory[] { new ModbusTcpProtocolDriverFactory() });

        await using var engine = new EngineRuntime(
            assembly,
            new RouterOrchestrator(new ConnectionRouter(), new ReadCoordinator()));

        // ============================================================================
        // Act
        // ============================================================================
        OperationResult<string> result = await engine.RegisterRouteAsync(
            new RegisterRouteCommand {
                RouteId       = "bad",
                ProtocolId    = "modbus-tcp",
                TransportKind = "Serial",
                SerialPort    = "/dev/ttyUSB0",
                BaudRate      = 9600
            },
            CancellationToken.None);

        // ============================================================================
        // Assert
        // ============================================================================
        Assert.IsFalse(result.Success);
        StringAssert.Contains(result.ErrorMessage, "不支持");
    }

    // 传输或协议工厂列表为空必须立刻抛错，空引擎没有任何用处
    [TestMethod]
    public void StaticAssembly_RejectsEmptyFactoryLists() {
        // ============================================================================
        // Assert
        // ============================================================================
        Assert.ThrowsExactly<ArgumentException>(() => new StaticRouteAssemblyService(
            Array.Empty<ITransportFactory>(),
            new IProtocolDriverFactory[] { new ModbusTcpProtocolDriverFactory() }));

        Assert.ThrowsExactly<ArgumentException>(() => new StaticRouteAssemblyService(
            new ITransportFactory[] { new FakeTransportFactory() },
            Array.Empty<IProtocolDriverFactory>()));
    }

    // =========================================================================
    // 伪造的传输层：回放预设响应，不涉及真实网络
    // =========================================================================

    private static byte[] BuildModbusReadResponse(byte unitId, byte[] data) {
        // MBAP(7) + FC(1) + ByteCount(1) + Data
        byte[] frame = new byte[9 + data.Length];
        frame[0] = 0x00; frame[1] = 0x01;                   // 事务 ID，由伪传输在发送时回填
        frame[2] = 0x00; frame[3] = 0x00;                   // 协议 ID
        int length = 3 + data.Length;                        // UnitId + FC + ByteCount + Data
        frame[4] = (byte)(length >> 8); frame[5] = (byte)(length & 0xFF);
        frame[6] = unitId;
        frame[7] = 0x03;                                     // FC03
        frame[8] = (byte)data.Length;
        Buffer.BlockCopy(data, 0, frame, 9, data.Length);
        return frame;
    }

    private static byte[] BuildModbusWriteEcho(byte unitId) {
        // FC06 响应回显地址与值
        byte[] frame = new byte[12];
        frame[2] = 0x00; frame[3] = 0x00;
        frame[4] = 0x00; frame[5] = 0x06;
        frame[6] = unitId;
        frame[7] = 0x06;
        return frame;
    }

    private sealed class FakeTransportFactory : ITransportFactory {
        private readonly TransportKind _kind;
        private readonly FakeTransportClient _client = new();

        public FakeTransportFactory(TransportKind kind = TransportKind.Tcp) => _kind = kind;

        public string TransportId => "fake-transport";
        public TransportKind Kind => _kind;
        public int PluginApiVersion => 1;

        /// <summary>下一次 SendAndReceive 要回放的响应帧。</summary>
        public byte[] NextResponse { set => _client.NextResponse = value; }

        public ITransportClient CreateClient() => _client;
    }

    private sealed class FakeTransportClient : ITransportClient {
        public byte[] NextResponse { get; set; } = Array.Empty<byte>();

        public string TransportId => "fake-transport";
        public TransportKind Kind => TransportKind.Tcp;

        /// <summary>替身不涉及真实连接，恒为可用。</summary>
        public bool IsConnectionAlive => true;

        public Task<OperationResult> ConnectAsync(TransportEndpoint e, CancellationToken ct)
            => Task.FromResult(OperationResult.Ok);

        public Task<OperationResult> DisconnectAsync(CancellationToken ct)
            => Task.FromResult(OperationResult.Ok);

        public ValueTask DisposeAsync() => ValueTask.CompletedTask;

        public Task<OperationResult<byte[]>> SendAndReceiveAsync(
            byte[] request, TryGetFrameLength probe, CancellationToken ct) {

            // 回填请求的事务 ID，模拟设备正确应答
            byte[] response = (byte[])NextResponse.Clone();
            if (response.Length >= 2 && request.Length >= 2) {
                response[0] = request[0];
                response[1] = request[1];
            }
            return Task.FromResult(OperationResult<byte[]>.Ok(response));
        }
    }
}
