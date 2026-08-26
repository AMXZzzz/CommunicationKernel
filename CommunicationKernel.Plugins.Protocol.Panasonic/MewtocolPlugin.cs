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
using CommunicationKernel.Plugin.Loader.Abstractions;
using CommunicationKernel.Plugins.Protocol.Panasonic.Internal;

namespace CommunicationKernel.Plugins.Protocol.Panasonic;

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
        PluginId    = "panasonic-mewtocol",
        DisplayName = "Panasonic MEWTOCOL-COM Plugin",
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
/// 松下 MEWTOCOL-COM 协议驱动工厂。
/// </summary>
/// <remarks>
/// 单一协议同时支持串口与以太网两种介质：MEWTOCOL-COM 的帧格式
/// （'%' + 两位十六进制站号 + '#' + 命令 + BCC + CR）与所走介质完全无关。
/// 原生跑在 RS-232/RS-485 上；经 FP 系列 ET-LAN 单元或通用 TCP 转串口
/// 透传装置时，同样的帧被原样封进 TCP（默认端口 9094）。
/// 因此不应拆成两个 ProtocolId——那只会让同一协议出现两份元信息。
/// </remarks>
public sealed class MewtocolProtocolDriverFactory : IProtocolDriverFactory
{
    /// <inheritdoc />
    public ProtocolMetadata Metadata { get; } = new()
    {
        ProtocolId          = "panasonic-mewtocol",
        DisplayName         = "Panasonic MEWTOCOL-COM",
        SupportedTransports = new[] { TransportKind.Serial, TransportKind.Tcp },
        RequiresStation     = true,
        StationHint         = "站号 1-99",
        PluginApiVersion    = 1
    };

    /// <inheritdoc />
    public IProtocolDriver CreateDriver(ProtocolDriverContext? context = null) =>
        // 站号从设备级配置解析，空则回落 1；帧格式与传输介质无关
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
        // 只组帧不发送，供诊断/预览使用
        OperationResult<MewtocolReadPlan> plan = PlanRead(address, length);
        return plan.Success
            ? OperationResult<byte[]>.Ok(plan.Value.Frame)
            : OperationResult<byte[]>.Fail(plan.ErrorMessage, plan.ErrorCode);
    }

    /// <summary>
    /// 单次 RD 读取的最大字数。
    /// </summary>
    /// <remarks>
    /// MEWTOCOL 响应是 ASCII：每字 4 个十六进制字符 + 约 12 字符头尾。
    /// 传输层单帧上限 1024 字节，约合 250 个字；取 128 字留足余量，
    /// 也是现场常用的分块尺寸。
    /// </remarks>
    private const int MaxReadWords = 128;

    /// <summary>一次读请求的计划：帧 + 响应解析所需的判定结果。</summary>
    private readonly record struct MewtocolReadPlan(byte[] Frame, bool IsBit, int WordCount);

    /// <summary>
    /// 规划一次读取：解析地址、决定命令类型、构建帧。
    /// </summary>
    /// <remarks>
    /// <b>数据区只由地址决定，与读取长度无关。</b>
    /// 历史实现写作 <c>addr.IsBit || length == 1</c>——读一个字节的 DT 寄存器
    /// 会被转成 RCS 单点读，返回的是完全不同数据区的位值，且全程 Success = true。
    /// 这与 Modbus 侧曾经的 <c>|| length == 1</c> 是同一个缺陷模式。
    /// <para>
    /// 判定结果随帧一并返回，避免解析响应时重新解析地址再判一次——
    /// 两处独立判定必须永远保持同步，是同类缺陷的温床。
    /// </para>
    /// </remarks>
    private OperationResult<MewtocolReadPlan> PlanRead(string address, int length) {
        if (length <= 0)
            return OperationResult<MewtocolReadPlan>.Fail(
                $"读取长度必须大于 0，实际 {length}", KernelErrorCode.InvalidArgument);

        OperationResult<MewtocolAddressInfo> parsed = MewtocolAddress.Parse(address, _defaultStation);
        if (!parsed.Success)
            return OperationResult<MewtocolReadPlan>.Fail(parsed.ErrorMessage, parsed.ErrorCode);

        MewtocolAddressInfo addr = parsed.Value;

        // 位地址走 RCS 读单点；字地址走 RD 读数据寄存器
        if (addr.IsBit)
            return OperationResult<MewtocolReadPlan>.Ok(
                new MewtocolReadPlan(MewtocolFrame.BuildReadContact(addr), IsBit: true, WordCount: 0));

        // length 单位为字节，向上取整到整字
        int wordCount = (length + 1) / 2;

        // 上限校验必须在构帧之前。
        //
        // MEWTOCOL 是 ASCII 协议：每个字在响应里占 4 个十六进制字符，
        // 加上头尾约 12 字符。传输层单帧上限 1024 字节，
        // 因此超过约 250 个字的请求根本不可能收全，只会读到一半后超时。
        // 取 128 字（256 字节）这一常用分块尺寸，留足余量。
        //
        // 没有这道校验时，超长请求会构出一个 PLC 不认或答不全的帧，
        // 表现为莫名其妙的超时，错误信息完全指不到真实原因。
        if (wordCount > MaxReadWords)
            return OperationResult<MewtocolReadPlan>.Fail(
                $"单次最多读取 {MaxReadWords} 个字（{MaxReadWords * 2} 字节），" +
                $"请求 {length} 字节合 {wordCount} 个字",
                KernelErrorCode.InvalidArgument);

        return OperationResult<MewtocolReadPlan>.Ok(
            new MewtocolReadPlan(MewtocolFrame.BuildReadData(addr, wordCount), IsBit: false, wordCount));
    }

    /// <inheritdoc />
    public OperationResult<byte[]> BuildWriteFrame(string address, byte[] payload) {
        if (payload is null || payload.Length == 0)
            return OperationResult<byte[]>.Fail("write payload is empty", KernelErrorCode.InvalidArgument);

        OperationResult<MewtocolAddressInfo> parsed = MewtocolAddress.Parse(address, _defaultStation);
        if (!parsed.Success)
            return OperationResult<byte[]>.Fail(parsed.ErrorMessage, parsed.ErrorCode);

        // 按 IsBit 选择 WCS 或 WD
        return OperationResult<byte[]>.Ok(BuildWriteFrameInternal(parsed.Value, payload));
    }

    /// <inheritdoc />
    public async Task<OperationResult<byte[]>> ReadAsync(
        ITransportClient client, string address, int length, CancellationToken cancellationToken) {

        OperationResult<MewtocolReadPlan> plan = PlanRead(address, length);
        if (!plan.Success)
            return OperationResult<byte[]>.Fail(plan.ErrorMessage, plan.ErrorCode);

        MewtocolReadPlan p = plan.Value;

        // 以 CR 为帧边界收齐响应（ASCII 协议，无需静默判帧）
        OperationResult<byte[]> response =
            await client.SendAndReceiveAsync(p.Frame, TryGetFrameLength, cancellationToken)
                .ConfigureAwait(false);
        if (!response.Success) return response;

        // 复用构建时的判定，不再重新解析地址——两处独立判定必然漂移
        return p.IsBit
            ? MewtocolFrame.ParseReadContactResponse(response.Value)
            : MewtocolFrame.ParseReadDataResponse(response.Value, p.WordCount);
    }

    /// <inheritdoc />
    public async Task<OperationResult> WriteAsync(
        ITransportClient client, string address, byte[] payload, CancellationToken cancellationToken) {

        OperationResult<byte[]> buildResult = BuildWriteFrame(address, payload);
        if (!buildResult.Success)
            return OperationResult.Fail(buildResult.ErrorMessage, buildResult.ErrorCode);

        OperationResult<byte[]> response =
            await client.SendAndReceiveAsync(buildResult.Value, TryGetFrameLength, cancellationToken)
                .ConfigureAwait(false);
        if (!response.Success)
            return OperationResult.Fail(response.ErrorMessage, response.ErrorCode);

        // 写响应只校验 '!' 错误标志
        return MewtocolFrame.ParseWriteResponse(response.Value);
    }

    /// <inheritdoc />
    public Task<OperationResult> ProbeAsync(
        ITransportClient client, CancellationToken cancellationToken)
        // DT0 一个字：无副作用短读，与内部 BuildPing 同源。
        => ProtocolProbe.ReadAsync(this, client, "DT0", 2, cancellationToken);

    /// <summary>
    /// 帧完整性判定：MEWTOCOL 响应以 CR（0x0D）收尾。
    /// </summary>
    /// <remarks>
    /// 该协议具备确定性帧边界，无需依赖时序静默。这一点在经
    /// TCP 转串口透传装置传输时尤为重要——串口侧的帧间静默
    /// 在以太网侧不被保证保留。
    /// </remarks>
    internal static bool TryGetFrameLength(ReadOnlySpan<byte> received, out int totalLength)
    {
        totalLength = 0;

        // 首字节必须是 '%' 或扩展头 '<'，否则该流已错位
        if (received.Length >= 1 && received[0] != (byte)'%' && received[0] != (byte)'<')
        {
            totalLength = -1;
            return true;
        }

        // 扫描 CR（0x0D）：MEWTOCOL 帧以回车为确定性边界
        for (int i = 0; i < received.Length; i++)
        {
            if (received[i] == 0x0D)
            {
                totalLength = i + 1;
                return true;
            }
        }

        return false;   // 尚未收到 CR
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
            // 大端：高字节在前；奇数字节末尾补 0，避免漏写半个字
            byte hi = payload[i * 2];
            byte lo = i * 2 + 1 < payload.Length ? payload[i * 2 + 1] : (byte)0;
            words[i] = (ushort)((hi << 8) | lo);
        }
        return words;
    }
}
