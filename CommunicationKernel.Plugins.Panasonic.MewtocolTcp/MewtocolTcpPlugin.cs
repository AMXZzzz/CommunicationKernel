// -----------------------------------------------------------------------------
// 文件: MewtocolTcpPlugin.cs
// 层级: Plugins / Panasonic.MewtocolTcp
// 作用: 松下 MEWTOCOL-COM TCP 协议插件（Manifest + Factory + Driver）。
// 协议说明:
//   MEWTOCOL-COM 是松下 PLC 的 ASCII over TCP 文本协议，默认端口 9094。
//   帧以 CR（\r）结尾，Transport 层须配置为 CR 定界读取。
//   支持操作:
//     RCS  读单触点（X / Y / R 位）
//     RD   读数据寄存器（DT / WR 字）
//     WCS  写单触点
//     WD   写数据寄存器（含多字）
// 设计约定:
//   1) 站号嵌入地址前缀："01:DT100"（站1，DT100），默认站 1。
//   2) 读路径：length=1（布尔） → RCS；length>1 → RD，字数=ceil(length/2)。
//   3) 写路径：IsBit → WCS；非Bit → WD，payload 按大端两字节拆词。
//   4) Transport 层负责 TCP 建立与 CR 定界帧读取（本驱动不含网络代码）。
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
/// 松下 MEWTOCOL TCP 协议驱动工厂。
/// </summary>
public sealed class MewtocolTcpProtocolDriverFactory : IProtocolDriverFactory
{
    /// <inheritdoc />
    public ProtocolMetadata Metadata { get; } = new()
    {
        ProtocolId       = "panasonic-mewtocol-tcp",
        DisplayName      = "Panasonic MEWTOCOL-COM (TCP/ASCII)",
        PluginApiVersion = 1
    };

    /// <inheritdoc />
    public IProtocolDriver CreateDriver() => new MewtocolTcpProtocolDriver(Metadata);
}

// =============================================================================
// Driver
// =============================================================================

/// <summary>
/// 松下 MEWTOCOL TCP 协议驱动实现。
/// </summary>
internal sealed class MewtocolTcpProtocolDriver : IProtocolDriver
{
    // -------------------------------------------------------------------------
    // 构造
    // -------------------------------------------------------------------------

    internal MewtocolTcpProtocolDriver(ProtocolMetadata metadata)
    {
        Metadata = metadata;
    }

    // -------------------------------------------------------------------------
    // IProtocolDriver
    // -------------------------------------------------------------------------

    /// <inheritdoc />
    public ProtocolMetadata Metadata { get; }

    /// <inheritdoc />
    public OperationResult<byte[]> BuildReadFrame(string address, int length) {
        OperationResult<MewtocolAddressInfo> parsed = MewtocolAddress.Parse(address);
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

        OperationResult<MewtocolAddressInfo> parsed = MewtocolAddress.Parse(address);
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

        OperationResult<MewtocolAddressInfo> parsed = MewtocolAddress.Parse(address);
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
