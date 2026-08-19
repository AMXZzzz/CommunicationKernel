// -----------------------------------------------------------------------------
// 文件: ModbusRtuPlugin.cs
// 层级: Plugins / Modbus.Rtu
// 作用: Modbus RTU 协议插件骨架（待实现）。
// 说明:
//   Modbus RTU 是串口/串口服务器上的 Modbus 二进制变体，
//   帧无起始标志，以帧间静默（3.5 字符时间）作为帧边界，
//   错误检测使用 CRC16（多项式 0xA001）。
//   当前为桩实现，返回 ProtocolError 占位，后续补充完整帧处理逻辑。
// -----------------------------------------------------------------------------

using System.Threading;
using System.Threading.Tasks;
using CommunicationKernel.Communication.Protocol.Abstractions;
using CommunicationKernel.Communication.Transport.Abstractions;
using CommunicationKernel.Core.Abstractions.Errors;
using CommunicationKernel.Core.Abstractions.Results;
using CommunicationKernel.Plugin.Runtime.Abstractions;

namespace CommunicationKernel.Plugins.Modbus.Rtu;

// =============================================================================
// Manifest
// =============================================================================

public sealed class ModbusRtuPluginManifest : IPluginManifest
{
    public PluginDescriptor Descriptor { get; } = new()
    {
        PluginId    = "modbus-rtu",
        DisplayName = "Modbus RTU Protocol Plugin",
        Kind        = PluginKind.Protocol,
        ApiVersion  = 1,
        Version     = "1.0.0",
        EntryType   = typeof(ModbusRtuPluginManifest).FullName
    };
}

// =============================================================================
// Factory
// =============================================================================

public sealed class ModbusRtuProtocolDriverFactory : IProtocolDriverFactory
{
    public ProtocolMetadata Metadata { get; } = new()
    {
        ProtocolId       = "modbus-rtu",
        DisplayName      = "Modbus RTU (CRC16, serial framing)",
        PluginApiVersion = 1
    };

    public IProtocolDriver CreateDriver() => new ModbusRtuProtocolDriver(Metadata);
}

// =============================================================================
// Driver（骨架，待实现）
// =============================================================================

internal sealed class ModbusRtuProtocolDriver : IProtocolDriver
{
    private static readonly OperationResult<byte[]> _notImplemented =
        OperationResult<byte[]>.Fail("Modbus RTU not yet implemented", KernelErrorCode.ProtocolError);

    private static readonly OperationResult _notImplementedWrite =
        OperationResult.Fail("Modbus RTU not yet implemented", KernelErrorCode.ProtocolError);

    public ProtocolMetadata Metadata { get; }

    internal ModbusRtuProtocolDriver(ProtocolMetadata metadata) => Metadata = metadata;

    public Task<OperationResult<byte[]>> BuildReadFrameAsync(
        string address, int length, CancellationToken cancellationToken)
        => Task.FromResult(_notImplemented);

    public Task<OperationResult<byte[]>> BuildWriteFrameAsync(
        string address, byte[] payload, CancellationToken cancellationToken)
        => Task.FromResult(_notImplemented);

    public Task<OperationResult<byte[]>> ReadAsync(
        ITransportClient client, string address, int length, CancellationToken cancellationToken)
        => Task.FromResult(_notImplemented);

    public Task<OperationResult> WriteAsync(
        ITransportClient client, string address, byte[] payload, CancellationToken cancellationToken)
        => Task.FromResult(_notImplementedWrite);
}
