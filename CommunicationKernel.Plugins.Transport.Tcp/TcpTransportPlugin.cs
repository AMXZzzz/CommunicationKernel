// -----------------------------------------------------------------------------
// 文件: TcpTransportPlugin.cs
// 层级: Plugins / Transport.Tcp
// 作用: TCP 传输介质插件（封装 System.Net.Sockets.TcpClient）。
// 说明:
//   1) TcpTransportFactory 实现 ITransportFactory，PluginId = "transport-tcp"。
//   2) TcpTransportClient 实现 ITransportClient：
//        ConnectAsync        → TcpClient.ConnectAsync，超时由 CancellationToken 控制
//        SendAndReceiveAsync → 先完整写入，再按协议给出的 TryGetFrameLength
//                              读满一整帧；多读到的字节留作残留供下一帧使用
//        DisconnectAsync     → 关闭并释放 TcpClient
//   3) 帧边界由协议决定，传输层不猜：
//        调用方传入 TryGetFrameLength 委托，传输层只负责"读够长度"。
//        早期版本靠"数据耗尽/静默若干毫秒"判定帧尾，在 TCP 分片与
//        粘包下会截断或粘连——现已彻底移除该策略。
//   4) 超时是"帧不完整"的兜底，不是"帧已结束"的判定：
//        FirstByteTimeoutMs        = 5 s   （等待 PLC 第一字节响应）
//        SubsequentByteTimeoutMs   = 1 s   （帧已开始后等待后续字节）
//        MaxResponseBytes          = 1024  （超出视为协议异常，防止无界增长）
//   5) 全部字节流操作使用 ArrayPool 减少 GC 分配；归还前 Array.Clear，
//      避免上一路由的报文残留被下一次租用读到。
// -----------------------------------------------------------------------------

using System;
using System.Buffers;
using System.Net.Sockets;
using System.Threading;
using System.Threading.Tasks;
using CommunicationKernel.Core.Transport.Abstractions;
using CommunicationKernel.Core.Transport.Framing;
using CommunicationKernel.Core.Abstractions.Errors;
using CommunicationKernel.Core.Abstractions.Results;
using CommunicationKernel.Plugin.Context.Abstractions;

namespace CommunicationKernel.Plugins.Transport.Tcp;

// =============================================================================
// Manifest
// =============================================================================

/// <summary>
/// TCP 传输插件清单，声明插件元数据。
/// </summary>
public sealed class TcpTransportPluginManifest : IPluginManifest
{
    /// <inheritdoc />
    public PluginDescriptor Descriptor { get; } = new()
    {
        PluginId    = "transport-tcp",
        DisplayName = "TCP Transport Plugin",
        Kind        = PluginKind.Transport,
        ApiVersion  = 1,
        Version     = "1.0.0",
        EntryType   = typeof(TcpTransportPluginManifest).FullName
    };
}

// =============================================================================
// Factory
// =============================================================================

/// <summary>
/// TCP 传输工厂，创建 <see cref="TcpTransportClient"/> 实例。
/// </summary>
public sealed class TcpTransportFactory : ITransportFactory
{
    /// <inheritdoc />
    public string TransportId     => "transport-tcp";

    /// <inheritdoc />
    public TransportKind Kind     => TransportKind.Tcp;

    /// <inheritdoc />
    public int PluginApiVersion   => 1;

    /// <inheritdoc />
    public ITransportClient CreateClient() => new TcpTransportClient();
}

// =============================================================================
// Client
// =============================================================================

/// <summary>
/// TCP 传输客户端，封装 <see cref="System.Net.Sockets.TcpClient"/>。
/// </summary>
public sealed class TcpTransportClient : ITransportClient
{
    // -------------------------------------------------------------------------
    // 常量
    // -------------------------------------------------------------------------

    /// <summary>等待 PLC 第一字节的超时（毫秒）。</summary>
    private const int FirstByteTimeoutMs = 5_000;

    /// <summary>
    /// 帧已开始接收后，等待后续字节的超时（毫秒）。
    /// </summary>
    /// <remarks>
    /// 这是"帧不完整"的判定阈值，<b>不是</b>"帧已结束"的判定阈值。
    /// 帧边界一律由协议提供的回调决定；此超时只用于避免半帧永久挂起。
    /// 取值需容忍链路拥塞与跨网段抖动，因此远大于旧实现的 50 ms。
    /// </remarks>
    private const int SubsequentByteTimeoutMs = 1_000;

    /// <summary>单次响应最大字节数，超出视为协议错误。</summary>
    private const int MaxResponseBytes = 1024;

    // -------------------------------------------------------------------------
    // 状态
    // -------------------------------------------------------------------------
    private TcpClient?     _tcp;
    private NetworkStream? _stream;
    private bool           _disposed;

    /// <summary>
    /// 分帧读取器：残留处理、两级超时、上限保护、取消与超时的区分全在其中。
    /// </summary>
    /// <remarks>
    /// 与串口插件共用同一实现。此前两边各有一份逐行雷同的读循环，
    /// 同一处缺陷要改两遍——本项目已经栽过一次「同类缺陷只改了一个插件」的跟头。
    /// </remarks>
    private readonly FrameReader _frameReader = new(
        MaxResponseBytes, FirstByteTimeoutMs, SubsequentByteTimeoutMs, "TCP");

    // -------------------------------------------------------------------------
    // ITransportClient
    // -------------------------------------------------------------------------

    /// <inheritdoc />
    public string TransportId => "transport-tcp";

    /// <inheritdoc />
    public TransportKind Kind => TransportKind.Tcp;

    /// <inheritdoc />
    public bool IsConnectionAlive
    {
        get
        {
            Socket? socket = _tcp?.Client;
            if (socket is null) return false;

            try
            {
                if (!socket.Connected) return false;

                // Poll(SelectRead)=true 有两种含义：有数据可读，或对端已关闭。
                // 用 Available==0 区分——可读却一个字节都没有，就是对端关了连接。
                //
                // 注意这只能发现带 FIN/RST 的正常断链（对端进程退出、主动断开）。
                // 拔网线、掉电属于半开连接，不产生任何报文，这里查不出来，
                // 要靠下面 ConnectAsync 里开启的 TCP keepalive 兜底。
                return !(socket.Poll(0, SelectMode.SelectRead) && socket.Available == 0);
            }
            catch (SocketException)      { return false; }
            catch (ObjectDisposedException) { return false; }
        }
    }

    /// <inheritdoc />
    public async Task<OperationResult> ConnectAsync(
        TransportEndpoint endpoint, CancellationToken cancellationToken)
    {
        ThrowIfDisposed();

        // 地址与端口缺一不可，否则 TcpClient.ConnectAsync 会抛含义不清的异常
        if (string.IsNullOrWhiteSpace(endpoint.Address) || endpoint.Port <= 0)
            return OperationResult.Fail(
                $"invalid TCP endpoint: {endpoint}", KernelErrorCode.InvalidArgument);

        try
        {
            // NoDelay=true 关闭 Nagle，避免小帧（如 Modbus 请求）被延迟合并
            _tcp = new TcpClient { NoDelay = true };
            await _tcp.ConnectAsync(endpoint.Address, endpoint.Port, cancellationToken)
                .ConfigureAwait(false);
            _stream = _tcp.GetStream();

            EnableKeepAlive(_tcp.Client);
            return OperationResult.Ok;
        }
        catch (OperationCanceledException)
        {
            // 连接超时/取消后必须释放半开的 TcpClient，否则套接字泄漏
            DisposeInternals();
            return OperationResult.Fail("TCP connect cancelled", KernelErrorCode.TransportIoError);
        }
        catch (Exception ex)
        {
            // 连接失败（拒连、DNS、网络不可达）同样释放半开套接字
            DisposeInternals();
            return OperationResult.Fail(
                $"TCP connect failed ({endpoint}): {ex.Message}", KernelErrorCode.TransportIoError);
        }
    }

    /// <inheritdoc />
    public async Task<OperationResult<byte[]>> SendAndReceiveAsync(
        byte[] request, TryGetFrameLength tryGetFrameLength, CancellationToken cancellationToken)
    {
        ThrowIfDisposed();

        // 帧边界必须由协议给出，传输层绝不自行猜测
        if (tryGetFrameLength is null)
            return OperationResult<byte[]>.Fail(
                "tryGetFrameLength is required", KernelErrorCode.InvalidArgument);

        if (_stream is null)
            return OperationResult<byte[]>.Fail(
                "TCP not connected", KernelErrorCode.TransportIoError);

        try
        {
            // 分支1：丢弃上一次请求遗留的残留字节，再发送请求。
            //
            // 残留的作用域是「一次帧读取之内」——组帧时多读到的字节必须留着，
            // 丢了会把下一帧截断。但跨请求就不同了：路由层的 I/O 门保证同一
            // 时刻只有一个在途请求，所以一次响应读完后还留在缓冲里的字节，
            // 只可能是重复响应，或早先超时请求的迟到响应。
            // 把它当成本次请求的响应会让请求/响应永久错位一格——
            // 之后每一次读到的都是上一次的数据，且不报任何错。
            //
            // 串口侧一直是这么做的（DiscardInBuffer + 归零），TCP 侧此前漏了，
            // 两个传输的语义因此不一致。
            _frameReader.DiscardResidual();

            await _stream.WriteAsync(request, 0, request.Length, cancellationToken)
                .ConfigureAwait(false);
            await _stream.FlushAsync(cancellationToken).ConfigureAwait(false);

            // 分支2：按协议给出的帧长读取恰好一帧
            return await _frameReader
                .ReadFrameAsync(_stream, tryGetFrameLength, cancellationToken)
                .ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            // 外部主动取消：必须映射为 Cancelled 而非 IO 错误。
            // 上层的重连判据包含 TransportIoError，若在此误报，
            // 每次用户停轮询/关页面都会触发一轮断开重连，批量停止时形成重连风暴。
            return OperationResult<byte[]>.Fail(
                "TCP SendAndReceive cancelled", KernelErrorCode.Cancelled);
        }
        catch (Exception ex)
        {
            return OperationResult<byte[]>.Fail(
                $"TCP SendAndReceive error: {ex.Message}", KernelErrorCode.TransportIoError);
        }
    }

    /// <inheritdoc />
    public Task<OperationResult> DisconnectAsync(CancellationToken cancellationToken)
    {
        DisposeInternals();
        return Task.FromResult(OperationResult.Ok);
    }

    /// <inheritdoc />
    public ValueTask DisposeAsync()
    {
        _disposed = true;
        DisposeInternals();
        return ValueTask.CompletedTask;
    }

    // -------------------------------------------------------------------------
    // 内部辅助
    // -------------------------------------------------------------------------

    /// <summary>
    /// 开启 TCP keepalive，让半开连接能被内核发现。
    /// </summary>
    /// <remarks>
    /// 拔网线、PLC 掉电这类断链不会发出 FIN/RST，套接字层面看上去一切正常，
    /// <see cref="IsConnectionAlive"/> 查不出来，读取则会一直挂到超时。
    /// keepalive 让内核定期发探测包，几次无应答后把连接标记为已断，
    /// 之后 Poll 与读写都会立刻失败。
    ///
    /// 系统默认是 2 小时后才首次探测，对产线毫无意义，因此显式收紧到
    /// 15 秒无流量即探测、每 5 秒重试、3 次无应答判死（最坏约 30 秒发现）。
    /// 失败不影响主功能：仅退化为「靠读写超时发现断链」。
    /// </remarks>
    private static void EnableKeepAlive(Socket socket)
    {
        try
        {
            socket.SetSocketOption(SocketOptionLevel.Socket, SocketOptionName.KeepAlive, true);
            socket.SetSocketOption(SocketOptionLevel.Tcp, SocketOptionName.TcpKeepAliveTime, 15);
            socket.SetSocketOption(SocketOptionLevel.Tcp, SocketOptionName.TcpKeepAliveInterval, 5);
            socket.SetSocketOption(SocketOptionLevel.Tcp, SocketOptionName.TcpKeepAliveRetryCount, 3);
        }
        catch (Exception)
        {
            // 部分平台不支持逐项设置 keepalive 参数；保持默认即可，不影响通讯
        }
    }

    private void DisposeInternals()
    {
        _stream?.Dispose();
        _stream = null;
        _tcp?.Dispose();
        _tcp = null;

        // 断链后残留字节属于上一条连接，必须丢弃，否则重连后首帧被污染
        _frameReader.DiscardResidual();
    }

    private void ThrowIfDisposed()
    {
        // 已 Dispose 后禁止再 Connect/Send，避免对已关闭套接字操作
        if (_disposed) throw new ObjectDisposedException(nameof(TcpTransportClient));
    }
}
