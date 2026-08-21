// -----------------------------------------------------------------------------
// 文件: MewtocolTcpPlugin.cs
// 层级: Plugins / Panasonic.Mewtocol
// 作用: 松下 MEWTOCOL-COM 协议插件（Manifest + Factory + Driver）。
// 协议说明:
//   MEWTOCOL-COM 本身是松下 PLC 的 ASCII 文本协议，原生跑在 RS-232/RS-485 串口上；
//   经 FP 系列以太网单元（ET-LAN）时，同样的帧被原样封装进 TCP（默认端口 9094）。
//   两种介质下的帧格式完全一致：'%' + 两位十六进制站号 + '#' + 命令 + BCC + CR。
//   因此本插件提供两个工厂共用同一驱动，仅所声明的传输介质不同：
//     panasonic-mewtocol-tcp     → 以太网
//     panasonic-mewtocol-serial  → 串口
//   支持操作:
//     RCS  读单触点（X / Y / R 位）
//     RD   读数据寄存器（DT / WR 字）
//     WCS  写单触点
//     WD   写数据寄存器（含多字）
// 设计约定:
//   1) 站号来自设备级配置（RegisterRoute.station）注入驱动作为默认值；
//      地址前缀 "01:DT100" 仅作为一条链路挂多台 PLC 时的逐变量覆盖手段。
//   2) 读路径：length=1（布尔） → RCS；length>1 → RD，字数=ceil(length/2)。
//   3) 写路径：IsBit → WCS；非Bit → WD，payload 按大端两字节拆词。
//   4) 本驱动只构建/解析 ASCII 帧，不含任何网络或串口代码；
//      建立连接与帧读取（CR 结尾 + 静默判帧）全部由 Transport 层负责。
//      正因如此，同一驱动可无改动地同时服务 TCP 与串口两种路由。
//   5) 数据寄存器字节序：MEWTOCOL 低字节先出，读写均需 SwapBytes 转换。
// -----------------------------------------------------------------------------

using System;
using System.Threading;
using System.Threading.Tasks;
using CommunicationKernel.Communication.Protocol.Abstractions;
using CommunicationKernel.Communication.Transport.Abstractions;
using CommunicationKernel.Core.Abstractions.Errors;
using CommunicationKernel.Core.Abstractions.Results;
using CommunicationKernel.Plugin.Runtime.Abstractions;
using CommunicationKernel.Plugins.Panasonic.MewtocolTcp.Internal;

namespace CommunicationKernel.Plugins.Panasonic.MewtocolTcp;

// =============================================================================
// Manifest
// =============================================================================

/// <summary>
/// 松下 MEWTOCOL TCP 插件 Manifest。
/// </summary>
public sealed class MewtocolTcpPluginManifest : IPluginManifest
{
    /// <inheritdoc />
    public PluginDescriptor Descriptor { get; } = new()
    {
        PluginId    = "panasonic-mewtocol-tcp",
        DisplayName = "Panasonic MEWTOCOL-COM TCP Plugin",
        Kind        = PluginKind.Protocol,
        ApiVersion  = 1,
        Version     = "1.0.0",
        EntryType   = typeof(MewtocolTcpPluginManifest).FullName
    };
}

// =============================================================================
// Factory
// =============================================================================

/// <summary>
/// 松下 MEWTOCOL 以太网协议驱动工厂（经 ET-LAN 单元，默认端口 9094）。
/// </summary>
public sealed class MewtocolTcpProtocolDriverFactory : IProtocolDriverFactory
{
    /// <inheritdoc />
    public ProtocolMetadata Metadata { get; } = new()
    {
        ProtocolId       = "panasonic-mewtocol-tcp",
        DisplayName      = "Panasonic MEWTOCOL (Ethernet/TCP)",
        TransportKind    = TransportKind.Tcp,
        RequiresStation  = true,
        StationHint      = "站号 1-99",
        PluginApiVersion = 1
    };

    /// <inheritdoc />
    public IProtocolDriver CreateDriver(ProtocolDriverContext? context = null) =>
        new MewtocolProtocolDriver(Metadata, MewtocolAddress.ResolveDefaultStation(context?.Station));
}

/// <summary>
/// 松下 MEWTOCOL-COM 串口协议驱动工厂（RS-232 / RS-485 原生介质）。
/// </summary>
/// <remarks>
/// 与 TCP 工厂共用同一驱动实现：MEWTOCOL-COM 的帧格式与所走介质无关，
/// 差异仅在 Transport 层（串口需配置串口号与波特率，而非 IP 与端口）。
/// RS-485 一主多从时，可用地址前缀 "NN:" 覆盖设备级默认站号。
/// </remarks>
public sealed class MewtocolSerialProtocolDriverFactory : IProtocolDriverFactory
{
    /// <inheritdoc />
    public ProtocolMetadata Metadata { get; } = new()
    {
        ProtocolId       = "panasonic-mewtocol-serial",
        DisplayName      = "Panasonic MEWTOCOL-COM (Serial RS-232/485)",
        TransportKind    = TransportKind.Serial,
        RequiresStation  = true,
        StationHint      = "站号 1-99",
        PluginApiVersion = 1
    };

    /// <inheritdoc />
    public IProtocolDriver CreateDriver(ProtocolDriverContext? context = null) =>
        new MewtocolProtocolDriver(Metadata, MewtocolAddress.ResolveDefaultStation(context?.Station));
}

// =============================================================================
// Driver
// =============================================================================

/// <summary>
/// 松下 MEWTOCOL 协议驱动实现，与传输介质无关。
/// TCP 与串口两个工厂共用本实现，仅注入的 <see cref="ProtocolMetadata"/> 不同。
/// </summary>
internal sealed class MewtocolProtocolDriver : IProtocolDriver
{
    // -------------------------------------------------------------------------
    // 构造
    // -------------------------------------------------------------------------

    /// <summary>
    /// 本路由的默认站号，来自设备级站号配置。
    /// 地址中的 "NN:" 前缀可覆盖它（一条链路挂多台 PLC 的场景）。
    /// </summary>
    private readonly byte _defaultStation;

    internal MewtocolProtocolDriver(ProtocolMetadata metadata, byte defaultStation)
    {
        Metadata        = metadata;
        _defaultStation = defaultStation;
    }

    // -------------------------------------------------------------------------
    // IProtocolDriver
    // -------------------------------------------------------------------------

    /// <inheritdoc />
    public ProtocolMetadata Metadata { get; }

    /// <inheritdoc />
    public OperationResult<byte[]> BuildReadFrame(string address, int length) {
        OperationResult<MewtocolAddressInfo> parsed = MewtocolAddress.Parse(address, _defaultStation);
        if (!parsed.Success)
            return OperationResult<byte[]>.Fail(parsed.ErrorMessage, parsed.ErrorCode);

        MewtocolAddressInfo addr = parsed.Value;
        byte[] frame = (addr.IsBit || length == 1)
            ? MewtocolFrame.BuildReadContact(addr)
            : MewtocolFrame.BuildReadData(addr, (length + 1) / 2);
        return OperationResult<byte[]>.Ok(frame);
    }

    /// <inheritdoc />
    public OperationResult<byte[]> BuildWriteFrame(string address, byte[] payload) {
        if (payload is null || payload.Length == 0)
            return OperationResult<byte[]>.Fail("write payload is empty", KernelErrorCode.InvalidArgument);

        OperationResult<MewtocolAddressInfo> parsed = MewtocolAddress.Parse(address, _defaultStation);
        if (!parsed.Success)
            return OperationResult<byte[]>.Fail(parsed.ErrorMessage, parsed.ErrorCode);

        return OperationResult<byte[]>.Ok(BuildWriteFrameInternal(parsed.Value, payload));
    }

    /// <inheritdoc />
    public async Task<OperationResult<byte[]>> ReadAsync(
        ITransportClient client, string address, int length, CancellationToken cancellationToken) {

        OperationResult<byte[]> buildResult = BuildReadFrame(address, length);
        if (!buildResult.Success) return buildResult;

        OperationResult<byte[]> response =
            await client.SendAndReceiveAsync(buildResult.Value, cancellationToken).ConfigureAwait(false);
        if (!response.Success) return response;

        OperationResult<MewtocolAddressInfo> parsed = MewtocolAddress.Parse(address, _defaultStation);
        bool isBit = !parsed.Success || parsed.Value.IsBit || length == 1;
        return isBit
            ? MewtocolFrame.ParseReadContactResponse(response.Value)
            : MewtocolFrame.ParseReadDataResponse(response.Value, (length + 1) / 2);
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

        return MewtocolFrame.ParseWriteResponse(response.Value);
    }

    // -------------------------------------------------------------------------
    // 内部辅助
    // -------------------------------------------------------------------------

    /// <summary>
    /// 根据地址类型和 payload 大小选择写命令（WCS 或 WD）。
    /// </summary>
    private static byte[] BuildWriteFrameInternal(MewtocolAddressInfo addr, byte[] payload)
    {
        // 触点写（WCS）：IsBit 地址 + 1 字节 payload
        if (addr.IsBit)
            return MewtocolFrame.BuildWriteContact(addr, payload[0] != 0);

        // 数据字写（WD）：将 payload 按大端拆分为 ushort 数组
        ushort[] words = PayloadToWords(payload);
        return MewtocolFrame.BuildWriteData(addr, words);
    }

    /// <summary>
    /// 将字节 payload 转换为大端 ushort 数组（奇数字节末尾补 0）。
    /// </summary>
    private static ushort[] PayloadToWords(byte[] payload)
    {
        int wordCount = (payload.Length + 1) / 2;
        var words     = new ushort[wordCount];
        for (int i = 0; i < wordCount; i++)
        {
            byte hi = payload[i * 2];
            byte lo = i * 2 + 1 < payload.Length ? payload[i * 2 + 1] : (byte)0;
            words[i] = (ushort)((hi << 8) | lo);
        }
        return words;
    }
}
