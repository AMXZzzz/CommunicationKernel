// -----------------------------------------------------------------------------
// 文件: ModbusTcpPlugin.cs
// 层级: Plugins / Modbus.Tcp
// 作用: Modbus TCP 协议插件（Manifest + Factory + Driver）。
// 协议说明:
//   Modbus TCP = ISO TCP 封装 Modbus PDU，端口 502，MBAP Header 7 字节。
//   支持功能码:
//     FC01  读线圈（Coil）
//     FC03  读保持寄存器（Holding Register）
//     FC05  写单线圈
//     FC06  写单寄存器
//     FC10  写多寄存器
// 设计约定:
//   1) Transaction ID 由 Driver 实例维护（Interlocked 递增，线程安全）。
//   2) Unit ID（从站 ID）嵌入地址字符串前缀（如 "1:40001"），默认 1。
//   3) 读路径：length 参数以字节为单位；布尔量（1 byte）→ FC01，其余 → FC03。
//   4) 写路径：payload 1 字节且为线圈地址 → FC05；2 字节 → FC06；>2 字节 → FC10。
//   5) 协议帧细节完全封装在 Internal 命名空间，外层不可见。
// -----------------------------------------------------------------------------

using System;
using System.Threading;
using System.Threading.Tasks;
using CommunicationKernel.Communication.Protocol.Abstractions;
using CommunicationKernel.Communication.Transport.Abstractions;
using CommunicationKernel.Core.Abstractions.Errors;
using CommunicationKernel.Core.Abstractions.Results;
using CommunicationKernel.Plugin.Runtime.Abstractions;
using CommunicationKernel.Plugins.Modbus.Tcp.Internal;

namespace CommunicationKernel.Plugins.Modbus.Tcp;

// =============================================================================
// Manifest
// =============================================================================

/// <summary>
/// Modbus TCP 插件 Manifest，声明插件元数据与 API 版本。
/// </summary>
public sealed class ModbusTcpPluginManifest : IPluginManifest
{
    /// <inheritdoc />
    public PluginDescriptor Descriptor { get; } = new()
    {
        PluginId    = "modbus-tcp",
        DisplayName = "Modbus TCP Protocol Plugin",
        Kind        = PluginKind.Protocol,
        ApiVersion  = 1,
        Version     = "1.0.0",
        EntryType   = typeof(ModbusTcpPluginManifest).FullName
    };
}

// =============================================================================
// Factory
// =============================================================================

/// <summary>
/// Modbus TCP 协议驱动工厂。
/// </summary>
public sealed class ModbusTcpProtocolDriverFactory : IProtocolDriverFactory
{
    /// <inheritdoc />
    public ProtocolMetadata Metadata { get; } = new()
    {
        ProtocolId       = "modbus-tcp",
        DisplayName      = "Modbus TCP (MBAP)",
        TransportKind    = TransportKind.Tcp,
        RequiresStation  = true,
        StationHint      = "从站地址 1-247",
        PluginApiVersion = 1
    };

    /// <inheritdoc />
    public IProtocolDriver CreateDriver(ProtocolDriverContext? context = null) =>
        new ModbusTcpProtocolDriver(Metadata, ModbusAddress.ResolveDefaultUnitId(context?.Station));
}

// =============================================================================
// Driver
// =============================================================================

/// <summary>
/// Modbus TCP 协议驱动实现。
/// </summary>
internal sealed class ModbusTcpProtocolDriver : IProtocolDriver
{
    // -------------------------------------------------------------------------
    // 字段
    // -------------------------------------------------------------------------

    /// <summary>事务 ID 计数器（Interlocked，线程安全）。</summary>
    private int _transactionIdCounter;

    /// <summary>
    /// 本路由的默认从站 ID，来自设备级站号配置。
    /// 地址中的 "N:" 前缀可覆盖它（RS-485 一主多从场景）。
    /// </summary>
    private readonly byte _defaultUnitId;

    // -------------------------------------------------------------------------
    // 构造
    // -------------------------------------------------------------------------

    internal ModbusTcpProtocolDriver(ProtocolMetadata metadata, byte defaultUnitId)
    {
        Metadata       = metadata;
        _defaultUnitId = defaultUnitId;
    }

    // -------------------------------------------------------------------------
    // IProtocolDriver
    // -------------------------------------------------------------------------

    /// <inheritdoc />
    public ProtocolMetadata Metadata { get; }

    /// <inheritdoc />
    public OperationResult<byte[]> BuildReadFrame(string address, int length) {
        OperationResult<ModbusAddressInfo> addrResult = ModbusAddress.Parse(address, _defaultUnitId);
        if (!addrResult.Success)
            return OperationResult<byte[]>.Fail(addrResult.ErrorMessage, addrResult.ErrorCode);

        ModbusAddressInfo addr  = addrResult.Value;
        ushort tid              = NextTransactionId();
        bool   isCoil           = addr.IsCoil || length == 1;
        ushort quantity         = isCoil
            ? (ushort)Math.Max(1, length * 8)
            : (ushort)((length + 1) / 2);

        return OperationResult<byte[]>.Ok(ModbusFrame.BuildReadFrame(tid, addr.UnitId, isCoil, addr.RegisterAddress, quantity));
    }

    /// <inheritdoc />
    public OperationResult<byte[]> BuildWriteFrame(string address, byte[] payload) {
        if (payload is null || payload.Length == 0)
            return OperationResult<byte[]>.Fail("write payload is empty", KernelErrorCode.InvalidArgument);

        OperationResult<ModbusAddressInfo> addrResult = ModbusAddress.Parse(address, _defaultUnitId);
        if (!addrResult.Success)
            return OperationResult<byte[]>.Fail(addrResult.ErrorMessage, addrResult.ErrorCode);

        return OperationResult<byte[]>.Ok(BuildWriteFrameInternal(NextTransactionId(), addrResult.Value, payload));
    }

    /// <inheritdoc />
    public async Task<OperationResult<byte[]>> ReadAsync(
        ITransportClient client, string address, int length, CancellationToken cancellationToken) {

        OperationResult<byte[]> buildResult = BuildReadFrame(address, length);
        if (!buildResult.Success) return buildResult;

        OperationResult<byte[]> response =
            await client.SendAndReceiveAsync(buildResult.Value, cancellationToken).ConfigureAwait(false);
        if (!response.Success) return response;

        OperationResult<ModbusAddressInfo> addrResult = ModbusAddress.Parse(address, _defaultUnitId);
        bool isCoil = addrResult.Success && (addrResult.Value.IsCoil || length == 1);
        return isCoil
            ? ModbusFrame.ParseReadCoilsResponse(response.Value)
            : ModbusFrame.ParseReadRegistersResponse(response.Value);
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

        return ModbusFrame.ParseWriteResponse(response.Value);
    }

    // -------------------------------------------------------------------------
    // 内部辅助
    // -------------------------------------------------------------------------

    /// <summary>
    /// 根据 payload 长度与地址类型选择正确的写功能码帧。
    /// </summary>
    private static byte[] BuildWriteFrameInternal(ushort tid, ModbusAddressInfo addr, byte[] payload)
    {
        // 线圈写（FC05）：payload[0] != 0 → ON，= 0 → OFF
        if (addr.IsCoil && payload.Length == 1)
            return ModbusFrame.BuildWriteSingleCoil(tid, addr.UnitId, addr.RegisterAddress, payload[0] != 0);

        // 单寄存器写（FC06）：2 字节 payload
        if (payload.Length <= 2)
        {
            ushort value = payload.Length == 2
                ? (ushort)((payload[0] << 8) | payload[1])
                : (ushort)payload[0];
            return ModbusFrame.BuildWriteSingleRegister(tid, addr.UnitId, addr.RegisterAddress, value);
        }

        // 多寄存器写（FC10）：> 2 字节 payload
        return ModbusFrame.BuildWriteMultipleRegisters(tid, addr.UnitId, addr.RegisterAddress, payload);
    }

    /// <summary>线程安全地获取下一个事务 ID（循环 0-65535）。</summary>
    private ushort NextTransactionId()
    {
        int next = Interlocked.Increment(ref _transactionIdCounter) & 0xFFFF;
        return (ushort)next;
    }
}
