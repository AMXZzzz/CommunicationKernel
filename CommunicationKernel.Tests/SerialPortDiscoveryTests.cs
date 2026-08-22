// -----------------------------------------------------------------------------
// 文件: SerialPortDiscoveryTests.cs
// 层级: Tests
// 作用: 验证「串口清单由宿主枚举」这条链路的引擎侧行为。
//
// 背景：串口长在跑通讯的那台机器上。宿主在树莓派、上位机在办公室 PC 时，
// 上位机列出的 COM1/COM2 与 PLC 毫无关系，选中后注册必然失败，
// 而错误信息会指向"打不开 COM1"，完全误导。因此枚举发生在宿主侧，
// 具体实现在传输插件里，引擎只负责发现与汇总——它不持有任何串口知识。
// -----------------------------------------------------------------------------

using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using CommunicationKernel.Communication.Protocol.Abstractions;
using CommunicationKernel.Communication.Transport.Abstractions;
using CommunicationKernel.Core.Abstractions.Results;
using CommunicationKernel.Engine;
using CommunicationKernel.Plugins.Modbus.Tcp;

namespace CommunicationKernel.Tests;

[TestClass]
public class SerialPortDiscoveryTests {

    [TestMethod]
    public void Enumerate_ReturnsPorts_FromFactoryImplementingEnumerator() {
        var assembly = BuildAssembly(
            new EnumeratingTransportFactory(TransportKind.Serial,
                new SerialPortDescriptor("/dev/ttyUSB0", "usb-FTDI_FT232R-if00-port0"),
                new SerialPortDescriptor("/dev/ttyAMA0", "")));

        IReadOnlyList<SerialPortDescriptor> ports = assembly.GetAvailableSerialPorts();

        Assert.HasCount(2, ports);
        Assert.AreEqual("/dev/ttyUSB0", ports[0].PortName);
        Assert.AreEqual("usb-FTDI_FT232R-if00-port0", ports[0].Description);
    }

    [TestMethod]
    public void Enumerate_ReturnsEmpty_WhenNoFactoryImplementsEnumerator() {
        // 纯以太网现场：没装串口插件。空清单是正常状态而非错误，
        // 界面据此提示"未发现串口"并保留手工输入。
        var assembly = BuildAssembly(new PlainTransportFactory(TransportKind.Tcp));

        Assert.IsEmpty(assembly.GetAvailableSerialPorts());
    }

    [TestMethod]
    public void Enumerate_IsolatesThrowingFactory_AndKeepsOthers() {
        // 单个插件枚举失败（权限不足、平台不支持）不能让整份清单变空——
        // 否则一个坏插件会让所有串口都配不出来。
        var assembly = BuildAssembly(
            new ThrowingTransportFactory(),
            new EnumeratingTransportFactory(TransportKind.Serial,
                new SerialPortDescriptor("COM3", "")));

        IReadOnlyList<SerialPortDescriptor> ports = assembly.GetAvailableSerialPorts();

        Assert.HasCount(1, ports);
        Assert.AreEqual("COM3", ports[0].PortName);
    }

    [TestMethod]
    public void Enumerate_DeduplicatesSamePort_ReportedByMultipleFactories() {
        // 同一物理串口可能被多个工厂各报一次；下拉框里出现两个 COM3
        // 会让操作员以为有两个设备。
        var assembly = BuildAssembly(
            new EnumeratingTransportFactory(TransportKind.Serial,
                new SerialPortDescriptor("COM3", "")),
            new EnumeratingTransportFactory(TransportKind.Serial,
                new SerialPortDescriptor("com3", ""),
                new SerialPortDescriptor("COM5", "")));

        IReadOnlyList<SerialPortDescriptor> ports = assembly.GetAvailableSerialPorts();

        Assert.HasCount(2, ports);
        CollectionAssert.AreEquivalent(
            new[] { "COM3", "COM5" }, ports.Select(p => p.PortName).ToArray());
    }

    [TestMethod]
    public void Enumerate_SkipsBlankPortNames() {
        // 空设备名进了下拉框就是一个选不中也用不了的空项
        var assembly = BuildAssembly(
            new EnumeratingTransportFactory(TransportKind.Serial,
                new SerialPortDescriptor("", ""),
                new SerialPortDescriptor("   ", ""),
                new SerialPortDescriptor("COM7", "")));

        IReadOnlyList<SerialPortDescriptor> ports = assembly.GetAvailableSerialPorts();

        Assert.HasCount(1, ports);
        Assert.AreEqual("COM7", ports[0].PortName);
    }

    [TestMethod]
    public void RealSerialPlugin_ListPorts_NeverThrows() {
        // 真实插件：本机有没有串口都不该抛异常。
        // 没有串口是正常状态（CI 运行器上就没有），不是错误。
        var factory = new CommunicationKernel.Plugins.Transport.SerialPort.SerialPortTransportFactory();

        IReadOnlyList<SerialPortDescriptor> ports = factory.ListPorts();

        Assert.IsNotNull(ports);
        foreach (SerialPortDescriptor port in ports)
            Assert.IsFalse(string.IsNullOrWhiteSpace(port.PortName));
    }

    // =========================================================================
    // 辅助
    // =========================================================================

    private static StaticRouteAssemblyService BuildAssembly(params ITransportFactory[] transports)
        => new(transports, new IProtocolDriverFactory[] { new ModbusTcpProtocolDriverFactory() });

    /// <summary>实现了枚举接口的传输工厂替身。</summary>
    private sealed class EnumeratingTransportFactory : ITransportFactory, ISerialPortEnumerator {
        private readonly TransportKind _kind;
        private readonly SerialPortDescriptor[] _ports;

        public EnumeratingTransportFactory(TransportKind kind, params SerialPortDescriptor[] ports) {
            _kind  = kind;
            _ports = ports;
        }

        public string TransportId => "fake-enumerating";
        public TransportKind Kind => _kind;
        public int PluginApiVersion => 1;
        public ITransportClient CreateClient() => throw new NotSupportedException();
        public IReadOnlyList<SerialPortDescriptor> ListPorts() => _ports;
    }

    /// <summary>不实现枚举接口的传输工厂替身（如 TCP 插件）。</summary>
    private sealed class PlainTransportFactory : ITransportFactory {
        private readonly TransportKind _kind;
        public PlainTransportFactory(TransportKind kind) => _kind = kind;

        public string TransportId => "fake-plain";
        public TransportKind Kind => _kind;
        public int PluginApiVersion => 1;
        public ITransportClient CreateClient() => throw new NotSupportedException();
    }

    /// <summary>枚举时抛异常的传输工厂替身。</summary>
    private sealed class ThrowingTransportFactory : ITransportFactory, ISerialPortEnumerator {
        public string TransportId => "fake-throwing";
        public TransportKind Kind => TransportKind.Serial;
        public int PluginApiVersion => 1;
        public ITransportClient CreateClient() => throw new NotSupportedException();
        public IReadOnlyList<SerialPortDescriptor> ListPorts() =>
            throw new UnauthorizedAccessException("模拟权限不足");
    }
}
