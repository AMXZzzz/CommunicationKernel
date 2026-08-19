// -----------------------------------------------------------------------------
// 文件: ModbusAsciiPlugin.cs
// 层级: Plugins / Modbus.Ascii
// 作用: Modbus ASCII 协议插件骨架（待实现）。
// 说明:
//   Modbus ASCII 在 Modbus RTU 基础上将每字节编码为两个 ASCII 十六进制字符，
//   帧以 ':' 开头，以 CR LF 结尾，错误检测使用 LRC（纵向冗余校验）。
//   当前为桩实现，返回 ProtocolError 占位，后续补充完整帧处理逻辑。
// -----------------------------------------------------------------------------

using System.Threading;
using System.Threading.Tasks;
using CommunicationKernel.Communication.Protocol.Abstractions;
using CommunicationKernel.Communication.Transport.Abstractions;
using CommunicationKernel.Core.Abstractions.Errors;
using CommunicationKernel.Core.Abstractions.Results;
using CommunicationKernel.Plugin.Runtime.Abstractions;

namespace CommunicationKernel.Plugins.Modbus.Ascii;

// =============================================================================
// Manifest
// =============================================================================

public sealed class ModbusAsciiPluginManifest : IPluginManifest
{
    public PluginDescriptor Descriptor { get; } = new()
    {
        PluginId    = "modbus-ascii",
        DisplayName = "Modbus ASCII Protocol Plugin",
        Kind        = PluginKind.Protocol,
        ApiVersion  = 1,
        Version     = "1.0.0",
        EntryType   = typeof(ModbusAsciiPluginManifest).FullName
    };
}

// =============================================================================
// Factory
// =============================================================================

public sealed class ModbusAsciiProtocolDriverFactory : IProtocolDriverFactory
{
    public ProtocolMetadata Metadata { get; } = new()
    {
        ProtocolId       = "modbus-ascii",
        DisplayName      = "Modbus ASCII (LRC, ':' framing)",
        PluginApiVersion = 1
    };

    public IProtocolDriver CreateDriver() => new ModbusAsciiProtocolDriver(Metadata);
}

// =============================================================================
// Driver（骨架，待实现）
// =============================================================================

internal sealed class ModbusAsciiProtocolDriver : IProtocolDriver
{
    private static readonly OperationResult<byte[]> _notImplemented =
        OperationResult<byte[]>.Fail("Modbus ASCII not yet implemented", KernelErrorCode.ProtocolError);

    private static readonly OperationResult _notImplementedWrite =
        OperationResult.Fail("Modbus ASCII not yet implemented", KernelErrorCode.ProtocolError);

    public ProtocolMetadata Metadata { get; }

    internal ModbusAsciiProtocolDriver(ProtocolMetadata metadata) => Metadata = metadata;

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
