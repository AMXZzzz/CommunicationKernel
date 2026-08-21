// -----------------------------------------------------------------------------
// 文件: ModbusTcpPlugin.cs
// 层级: Plugins / Modbus.Tcp
// 作用: Modbus TCP 协议插件（Manifest + Factory）。
// 说明:
//   本插件只声明元信息并选定 MBAP 封装；全部协议语义与驱动实现来自
//   共享的 Modbus.Core，与 RTU / ASCII 两个插件完全一致。
// -----------------------------------------------------------------------------

using CommunicationKernel.Communication.Protocol.Abstractions;
using CommunicationKernel.Communication.Transport.Abstractions;
using CommunicationKernel.Plugin.Runtime.Abstractions;
using CommunicationKernel.Plugins.Modbus.Core;

namespace CommunicationKernel.Plugins.Modbus.Tcp;

/// <summary>Modbus TCP 插件清单。</summary>
public sealed class ModbusTcpPluginManifest : IPluginManifest {
    /// <inheritdoc />
    public PluginDescriptor Descriptor { get; } = new() {
        PluginId    = "modbus-tcp",
        DisplayName = "Modbus TCP Protocol Plugin",
        Kind        = PluginKind.Protocol,
        ApiVersion  = 1,
        Version     = "1.0.0",
        EntryType   = typeof(ModbusTcpPluginManifest).FullName
    };
}

/// <summary>Modbus TCP 协议驱动工厂。</summary>
public sealed class ModbusTcpProtocolDriverFactory : IProtocolDriverFactory {
    /// <inheritdoc />
    public ProtocolMetadata Metadata { get; } = new() {
        ProtocolId       = "modbus-tcp",
        DisplayName      = "Modbus TCP (MBAP)",
        // MBAP 封装依赖 TCP 的可靠有序流，无串口对应形式
        SupportedTransports = new[] { TransportKind.Tcp },
        RequiresStation  = true,
        StationHint      = "从站地址 1-247",
        PluginApiVersion = 1
    };

    /// <inheritdoc />
    public IProtocolDriver CreateDriver(ProtocolDriverContext? context = null) =>
        new ModbusProtocolDriver(
            Metadata,
            new ModbusTcpEnvelope(),
            ModbusAddress.ResolveDefaultUnitId(context?.Station));
}
