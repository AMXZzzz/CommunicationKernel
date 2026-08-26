// -----------------------------------------------------------------------------
// 文件: ModbusRtuPlugin.cs
// 层级: Plugins / Modbus.Rtu
// 作用: Modbus RTU 协议插件（Manifest + Factory）。
// 说明:
//   本插件只声明元信息并选定 CRC16 封装；全部协议语义与驱动实现来自
//   共享的 Modbus.Core。
// -----------------------------------------------------------------------------

using CommunicationKernel.Communication.Protocol.Abstractions;
using CommunicationKernel.Communication.Transport.Abstractions;
using CommunicationKernel.Plugin.Context.Abstractions;
using CommunicationKernel.Plugins.Protocol.Modbus.Core;

namespace CommunicationKernel.Plugins.Protocol.Modbus.Rtu;

// ============================================================================
// Manifest
// ============================================================================

/// <summary>Modbus RTU 插件清单。</summary>
public sealed class ModbusRtuPluginManifest : IPluginManifest {
    /// <inheritdoc />
    public PluginDescriptor Descriptor { get; } = new() {
        PluginId    = "modbus-rtu",
        DisplayName = "Modbus RTU Protocol Plugin",
        Kind        = PluginKind.Protocol,
        ApiVersion  = 1,
        Version     = "1.0.0",
        EntryType   = typeof(ModbusRtuPluginManifest).FullName
    };
}

// ============================================================================
// Factory
// ============================================================================

/// <summary>Modbus RTU 协议驱动工厂。</summary>
public sealed class ModbusRtuProtocolDriverFactory : IProtocolDriverFactory {
    /// <inheritdoc />
    public ProtocolMetadata Metadata { get; } = new() {
        ProtocolId       = "modbus-rtu",
        DisplayName      = "Modbus RTU (CRC16)",

        // RTU 帧格式与介质无关：除 RS-485 外，还广泛用于
        // TCP 转串口透传装置（Moxa NPort、USR-TCP232 等），
        // 网关把 TCP 载荷原样转发到串口，以太网侧跑的仍是 RTU 帧。
        SupportedTransports = new[] { TransportKind.Serial, TransportKind.Tcp },

        RequiresStation  = true,
        StationHint      = "从站地址 1-247",
        PluginApiVersion = 1
    };

    /// <inheritdoc />
    public IProtocolDriver CreateDriver(ProtocolDriverContext? context = null) =>
        // 选定 CRC16 封装；站号从设备级配置解析，空则回落 1
        new ModbusProtocolDriver(
            Metadata,
            new ModbusRtuEnvelope(),
            ModbusAddress.ResolveDefaultUnitId(context?.Station));
}
