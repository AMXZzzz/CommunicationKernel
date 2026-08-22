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
using CommunicationKernel.Communication.Transport.Abstractions;
using CommunicationKernel.Core.Abstractions.Errors;
using CommunicationKernel.Core.Abstractions.Results;
using CommunicationKernel.Plugin.Runtime.Abstractions;

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
    /// 上一次读取中超出该帧的字节。下次读取优先消费，绝不丢弃。
    /// </summary>
    private byte[]? _residual;

    /// <summary><see cref="_residual"/> 中的有效字节数。</summary>
    private int _residualLength;

    // -------------------------------------------------------------------------
    // ITransportClient
    // -------------------------------------------------------------------------

    /// <inheritdoc />
    public string TransportId => "transport-tcp";

    /// <inheritdoc />
    public TransportKind Kind => TransportKind.Tcp;

    /// <inheritdoc />
    public async Task<OperationResult> ConnectAsync(
        TransportEndpoint endpoint, CancellationToken cancellationToken)
    {
        ThrowIfDisposed();

        if (string.IsNullOrWhiteSpace(endpoint.Address) || endpoint.Port <= 0)
            return OperationResult.Fail(
                $"invalid TCP endpoint: {endpoint}", KernelErrorCode.InvalidArgument);

        try
        {
            _tcp = new TcpClient { NoDelay = true };
            await _tcp.ConnectAsync(endpoint.Address, endpoint.Port, cancellationToken)
                .ConfigureAwait(false);
            _stream = _tcp.GetStream();
            return OperationResult.Ok;
        }
        catch (OperationCanceledException)
        {
            DisposeInternals();
            return OperationResult.Fail("TCP connect cancelled", KernelErrorCode.TransportIoError);
        }
        catch (Exception ex)
        {
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
            _residualLength = 0;

            await _stream.WriteAsync(request, 0, request.Length, cancellationToken)
                .ConfigureAwait(false);
            await _stream.FlushAsync(cancellationToken).ConfigureAwait(false);

            // 分支2：按协议给出的帧长读取恰好一帧
            return await ReadFrameAsync(tryGetFrameLength, cancellationToken).ConfigureAwait(false);
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
    // 响应读取
    // -------------------------------------------------------------------------

    /// <summary>
    /// 读取恰好一个完整帧，帧边界由协议提供的 <paramref name="tryGetFrameLength"/> 判定。
    /// </summary>
    /// <remarks>
    /// 超出本帧的字节保留在 <see cref="_residual"/> 中供下次调用消费，
    /// 绝不丢弃——丢弃会使请求与响应永久错位一格。
    /// </remarks>
    private async Task<OperationResult<byte[]>> ReadFrameAsync(
        TryGetFrameLength tryGetFrameLength, CancellationToken cancellationToken)
    {
        byte[] buffer = ArrayPool<byte>.Shared.Rent(MaxResponseBytes);
        int    total  = 0;

        try
        {
            // 先消费上一次多读到的残留字节
            if (_residualLength > 0)
            {
                Buffer.BlockCopy(_residual!, 0, buffer, 0, _residualLength);
                total = _residualLength;
                _residualLength = 0;
            }

            while (true)
            {
                // ── 用已有字节尝试判定帧长 ──
                if (total > 0)
                {
                    if (tryGetFrameLength(buffer.AsSpan(0, total), out int frameLength))
                    {
                        if (frameLength <= 0)
                            return OperationResult<byte[]>.Fail(
                                "协议判定响应帧非法（无法识别的帧头）", KernelErrorCode.ProtocolError);

                        if (frameLength > MaxResponseBytes)
                            return OperationResult<byte[]>.Fail(
                                $"响应帧声明 {frameLength} 字节，超出单帧上限 {MaxResponseBytes}",
                                KernelErrorCode.ProtocolError);

                        if (total >= frameLength)
                        {
                            // 已读满一帧：截取本帧，余下留作残留
                            byte[] frame = new byte[frameLength];
                            Buffer.BlockCopy(buffer, 0, frame, 0, frameLength);
                            SaveResidual(buffer, frameLength, total - frameLength);
                            return OperationResult<byte[]>.Ok(frame);
                        }
                    }
                }

                if (total >= MaxResponseBytes)
                    return OperationResult<byte[]>.Fail(
                        $"响应超过单帧上限 {MaxResponseBytes} 字节仍未成帧",
                        KernelErrorCode.ProtocolError);

                // ── 继续读取；首字节用长超时，后续字节用短超时 ──
                int timeoutMs = total == 0 ? FirstByteTimeoutMs : SubsequentByteTimeoutMs;
                using CancellationTokenSource readCts =
                    CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
                readCts.CancelAfter(timeoutMs);

                int read;
                try
                {
                    read = await _stream!.ReadAsync(buffer, total, MaxResponseBytes - total, readCts.Token)
                        .ConfigureAwait(false);
                }
                catch (OperationCanceledException)
                {
                    // 区分外部取消与内部超时：前者不应触发上层重连
                    if (cancellationToken.IsCancellationRequested)
                        return OperationResult<byte[]>.Fail(
                            "TCP 读取被取消", KernelErrorCode.Cancelled);

                    return OperationResult<byte[]>.Fail(
                        total == 0
                            ? $"等待响应首字节超时（{FirstByteTimeoutMs} ms）"
                            : $"响应帧不完整：已收 {total} 字节，后续字节等待超时（{SubsequentByteTimeoutMs} ms）",
                        KernelErrorCode.Timeout);
                }

                if (read == 0)
                    return OperationResult<byte[]>.Fail(
                        "TCP 连接被远端关闭", KernelErrorCode.TransportIoError);

                total += read;
            }
        }
        finally
        {
            // 归还前清零：缓冲区来自共享池，残留报文可能被其他消费方读到
            Array.Clear(buffer, 0, Math.Min(total, buffer.Length));
            ArrayPool<byte>.Shared.Return(buffer);
        }
    }

    /// <summary>保存超出本帧的字节，供下次 <see cref="ReadFrameAsync"/> 优先消费。</summary>
    private void SaveResidual(byte[] source, int offset, int length)
    {
        if (length <= 0)
        {
            _residualLength = 0;
            return;
        }

        _residual ??= new byte[MaxResponseBytes];
        Buffer.BlockCopy(source, offset, _residual, 0, length);
        _residualLength = length;
    }

    // -------------------------------------------------------------------------
    // 内部辅助
    // -------------------------------------------------------------------------

    private void DisposeInternals()
    {
        _stream?.Dispose();
        _stream = null;
        _tcp?.Dispose();
        _tcp = null;

        // 断链后残留字节属于上一条连接，必须丢弃，否则重连后首帧被污染
        _residualLength = 0;
        _residual       = null;
    }

    private void ThrowIfDisposed()
    {
        if (_disposed) throw new ObjectDisposedException(nameof(TcpTransportClient));
    }
}
