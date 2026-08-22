// -----------------------------------------------------------------------------
// 文件: ModbusAsciiPlugin.cs
// 层级: Plugins / Modbus.Ascii
// 作用: Modbus ASCII 协议插件（Manifest + Factory）。
// 说明:
//   本插件只声明元信息并选定 ASCII-LRC 封装；全部协议语义与驱动实现来自
//   共享的 Modbus.Core。
// -----------------------------------------------------------------------------

using CommunicationKernel.Communication.Protocol.Abstractions;
using CommunicationKernel.Communication.Transport.Abstractions;
using CommunicationKernel.Plugin.Loader.Abstractions;
using CommunicationKernel.Plugins.Protocol.Modbus.Core;

namespace CommunicationKernel.Plugins.Protocol.Modbus.Ascii;

// ============================================================================
// Manifest
// ============================================================================

/// <summary>Modbus ASCII 插件清单。</summary>
public sealed class ModbusAsciiPluginManifest : IPluginManifest {
    /// <inheritdoc />
    public PluginDescriptor Descriptor { get; } = new() {
        PluginId    = "modbus-ascii",
        DisplayName = "Modbus ASCII Protocol Plugin",
        Kind        = PluginKind.Protocol,
        ApiVersion  = 1,
        Version     = "1.0.0",
        EntryType   = typeof(ModbusAsciiPluginManifest).FullName
    };
}

// ============================================================================
// Factory
// ============================================================================

/// <summary>Modbus ASCII 协议驱动工厂。</summary>
public sealed class ModbusAsciiProtocolDriverFactory : IProtocolDriverFactory {
    /// <inheritdoc />
    public ProtocolMetadata Metadata { get; } = new() {
        ProtocolId       = "modbus-ascii",
        DisplayName      = "Modbus ASCII (LRC)",

        // 与 RTU 同理：ASCII 帧格式与介质无关，
        // 同样支持经 TCP 转串口透传装置接入
        SupportedTransports = new[] { TransportKind.Serial, TransportKind.Tcp },

        RequiresStation  = true,
        StationHint      = "从站地址 1-247",
        PluginApiVersion = 1
    };

    /// <inheritdoc />
    public IProtocolDriver CreateDriver(ProtocolDriverContext? context = null) =>
        // 选定 ASCII-LRC 封装；站号从设备级配置解析，空则回落 1
        new ModbusProtocolDriver(
            Metadata,
            new ModbusAsciiEnvelope(),
            ModbusAddress.ResolveDefaultUnitId(context?.Station));
}
