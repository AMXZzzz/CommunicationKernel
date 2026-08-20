// -----------------------------------------------------------------------------
// 文件: ModbusAsciiPlugin.cs
// 层级: Plugins / Modbus.Ascii
// 作用: Modbus ASCII 协议插件（LRC 校验，':' 帧，CR/LF 结尾）。
// 说明:
//   Modbus ASCII 将每字节编码为两个大写 ASCII 十六进制字符：
//     帧格式: ':' + HEX(SlaveId,2) + HEX(FC,2) + HEX(Data) + HEX(LRC,2) + CR + LF
//     LRC: 所有 PDU 字节之和取低 8 位后取二补数
//   本插件支持 FC01/FC03（读）、FC05/FC06/FC10（写）。
//   地址格式: [slaveId:]address，与 RTU 相同，SlaveId 默认 1。
// -----------------------------------------------------------------------------

using System;
using System.Threading;
using System.Threading.Tasks;
using CommunicationKernel.Communication.Protocol.Abstractions;
using CommunicationKernel.Communication.Transport.Abstractions;
using CommunicationKernel.Core.Abstractions.Errors;
using CommunicationKernel.Core.Abstractions.Results;
using CommunicationKernel.Plugin.Runtime.Abstractions;
using CommunicationKernel.Plugins.Modbus.Ascii.Internal;

namespace CommunicationKernel.Plugins.Modbus.Ascii;

// =============================================================================
// Manifest
// =============================================================================

/// <summary>
/// Modbus ASCII 插件清单，声明插件元数据。
/// </summary>
public sealed class ModbusAsciiPluginManifest : IPluginManifest
{
    /// <inheritdoc />
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

/// <summary>
/// Modbus ASCII 协议驱动工厂。
/// </summary>
public sealed class ModbusAsciiProtocolDriverFactory : IProtocolDriverFactory
{
    /// <inheritdoc />
    public ProtocolMetadata Metadata { get; } = new()
    {
        ProtocolId       = "modbus-ascii",
        DisplayName      = "Modbus ASCII (LRC, ':' framing, CRLF)",
        PluginApiVersion = 1
    };

    /// <inheritdoc />
    public IProtocolDriver CreateDriver() => new ModbusAsciiProtocolDriver(Metadata);
}

// =============================================================================
// Driver
// =============================================================================

/// <summary>
/// Modbus ASCII 协议驱动实现。
/// </summary>
internal sealed class ModbusAsciiProtocolDriver : IProtocolDriver
{
    internal ModbusAsciiProtocolDriver(ProtocolMetadata metadata) => Metadata = metadata;

    /// <inheritdoc />
    public ProtocolMetadata Metadata { get; }

    /// <inheritdoc />
    public OperationResult<byte[]> BuildReadFrame(string address, int length) {
        OperationResult<ModbusAsciiAddressInfo> addrResult = ModbusAsciiAddress.Parse(address);
        if (!addrResult.Success)
            return OperationResult<byte[]>.Fail(addrResult.ErrorMessage, addrResult.ErrorCode);

        ModbusAsciiAddressInfo addr = addrResult.Value;
        return OperationResult<byte[]>.Ok(ModbusAsciiFrame.BuildReadFrame(
            addr.SlaveId, addr.IsCoil, addr.RegisterAddress, (ushort)Math.Min(length, ushort.MaxValue)));
    }

    /// <inheritdoc />
    public OperationResult<byte[]> BuildWriteFrame(string address, byte[] payload) {
        OperationResult<ModbusAsciiAddressInfo> addrResult = ModbusAsciiAddress.Parse(address);
        if (!addrResult.Success)
            return OperationResult<byte[]>.Fail(addrResult.ErrorMessage, addrResult.ErrorCode);

        ModbusAsciiAddressInfo addr = addrResult.Value;
        byte[] frame = addr.IsCoil
            ? ModbusAsciiFrame.BuildWriteSingleCoil(addr.SlaveId, addr.RegisterAddress, payload.Length > 0 && payload[0] != 0)
            : payload.Length == 2
                ? ModbusAsciiFrame.BuildWriteSingleRegister(addr.SlaveId, addr.RegisterAddress, (ushort)((payload[0] << 8) | payload[1]))
                : ModbusAsciiFrame.BuildWriteMultipleRegisters(addr.SlaveId, addr.RegisterAddress, payload);

        return OperationResult<byte[]>.Ok(frame);
    }

    /// <inheritdoc />
    public async Task<OperationResult<byte[]>> ReadAsync(
        ITransportClient client, string address, int length, CancellationToken cancellationToken) {

        OperationResult<byte[]> buildResult = BuildReadFrame(address, length);
        if (!buildResult.Success) return buildResult;

        OperationResult<byte[]> response =
            await client.SendAndReceiveAsync(buildResult.Value, cancellationToken).ConfigureAwait(false);
        if (!response.Success) return response;

        OperationResult<ModbusAsciiAddressInfo> addrResult = ModbusAsciiAddress.Parse(address);
        if (!addrResult.Success)
            return OperationResult<byte[]>.Fail(addrResult.ErrorMessage, addrResult.ErrorCode);

        return addrResult.Value.IsCoil
            ? ModbusAsciiFrame.ParseReadCoilsResponse(response.Value)
            : ModbusAsciiFrame.ParseReadRegistersResponse(response.Value);
    }

    /// <inheritdoc />
    public async Task<OperationResult> WriteAsync(
        ITransportClient client, string address, byte[] payload, CancellationToken cancellationToken) {

        OperationResult<byte[]> buildResult = BuildWriteFrame(address, payload);
        if (!buildResult.Success)
            return OperationResult.Fail(buildResult.ErrorMessage, buildResult.ErrorCode);

        OperationResult<byte[]> response =
            await client.SendAndReceiveAsync(buildResult.Value, cancellationToken).ConfigureAwait(false);
        if (!response.Success)
            return OperationResult.Fail(response.ErrorMessage, response.ErrorCode);

        return ModbusAsciiFrame.ParseWriteResponse(response.Value);
    }
}
