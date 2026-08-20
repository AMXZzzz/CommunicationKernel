// -----------------------------------------------------------------------------
// 文件: ModbusRtuPlugin.cs
// 层级: Plugins / Modbus.Rtu
// 作用: Modbus RTU 协议插件（CRC16 帧，串口/串口服务器场景）。
// 说明:
//   Modbus RTU 是串口上的 Modbus 二进制变体：
//     帧格式: [SlaveId(1)] [FC(1)] [Data(N)] [CRC-Lo(1)] [CRC-Hi(1)]
//     CRC16 多项式: 0xA001（LSB first，Modbus 标准）
//   本插件支持 FC01/FC03（读）、FC05/FC06/FC10（写）。
//   地址格式: [slaveId:]address，与 Modbus TCP 相同，SlaveId 默认 1。
// -----------------------------------------------------------------------------

using System;
using System.Threading;
using System.Threading.Tasks;
using CommunicationKernel.Communication.Protocol.Abstractions;
using CommunicationKernel.Communication.Transport.Abstractions;
using CommunicationKernel.Core.Abstractions.Errors;
using CommunicationKernel.Core.Abstractions.Results;
using CommunicationKernel.Plugin.Runtime.Abstractions;
using CommunicationKernel.Plugins.Modbus.Rtu.Internal;

namespace CommunicationKernel.Plugins.Modbus.Rtu;

// =============================================================================
// Manifest
// =============================================================================

/// <summary>
/// Modbus RTU 插件清单，声明插件元数据（PluginId、版本、类型）。
/// </summary>
public sealed class ModbusRtuPluginManifest : IPluginManifest
{
    /// <inheritdoc />
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

/// <summary>
/// Modbus RTU 协议驱动工厂。
/// </summary>
public sealed class ModbusRtuProtocolDriverFactory : IProtocolDriverFactory
{
    /// <inheritdoc />
    public ProtocolMetadata Metadata { get; } = new()
    {
        ProtocolId       = "modbus-rtu",
        DisplayName      = "Modbus RTU (CRC16, serial framing)",
        PluginApiVersion = 1
    };

    /// <inheritdoc />
    public IProtocolDriver CreateDriver() => new ModbusRtuProtocolDriver(Metadata);
}

// =============================================================================
// Driver
// =============================================================================

/// <summary>
/// Modbus RTU 协议驱动实现。
/// </summary>
internal sealed class ModbusRtuProtocolDriver : IProtocolDriver
{
    // -------------------------------------------------------------------------
    // 构造
    // -------------------------------------------------------------------------
    internal ModbusRtuProtocolDriver(ProtocolMetadata metadata) => Metadata = metadata;

    // -------------------------------------------------------------------------
    // IProtocolDriver
    // -------------------------------------------------------------------------

    /// <inheritdoc />
    public ProtocolMetadata Metadata { get; }

    /// <inheritdoc />
    public OperationResult<byte[]> BuildReadFrame(string address, int length) {
        OperationResult<ModbusRtuAddressInfo> addrResult = ModbusRtuAddress.Parse(address);
        if (!addrResult.Success)
            return OperationResult<byte[]>.Fail(addrResult.ErrorMessage, addrResult.ErrorCode);

        ModbusRtuAddressInfo addr = addrResult.Value;
        return OperationResult<byte[]>.Ok(ModbusRtuFrame.BuildReadFrame(
            addr.SlaveId, addr.IsCoil, addr.RegisterAddress, (ushort)Math.Min(length, ushort.MaxValue)));
    }

    /// <inheritdoc />
    public OperationResult<byte[]> BuildWriteFrame(string address, byte[] payload) {
        OperationResult<ModbusRtuAddressInfo> addrResult = ModbusRtuAddress.Parse(address);
        if (!addrResult.Success)
            return OperationResult<byte[]>.Fail(addrResult.ErrorMessage, addrResult.ErrorCode);

        ModbusRtuAddressInfo addr = addrResult.Value;
        byte[] frame = addr.IsCoil
            ? ModbusRtuFrame.BuildWriteSingleCoil(addr.SlaveId, addr.RegisterAddress, payload.Length > 0 && payload[0] != 0)
            : payload.Length == 2
                ? ModbusRtuFrame.BuildWriteSingleRegister(addr.SlaveId, addr.RegisterAddress, (ushort)((payload[0] << 8) | payload[1]))
                : ModbusRtuFrame.BuildWriteMultipleRegisters(addr.SlaveId, addr.RegisterAddress, payload);

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

        OperationResult<ModbusRtuAddressInfo> addrResult = ModbusRtuAddress.Parse(address);
        if (!addrResult.Success)
            return OperationResult<byte[]>.Fail(addrResult.ErrorMessage, addrResult.ErrorCode);

        return addrResult.Value.IsCoil
            ? ModbusRtuFrame.ParseReadCoilsResponse(response.Value)
            : ModbusRtuFrame.ParseReadRegistersResponse(response.Value);
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

        return ModbusRtuFrame.ParseWriteResponse(response.Value);
    }
}
