// -----------------------------------------------------------------------------
// 文件: SerialPortTransportPlugin.cs
// 层级: Plugins / Transport.SerialPort
// 作用: 串口传输介质插件（封装 System.IO.Ports.SerialPort）。
// 说明:
//   1) SerialPortTransportFactory：PluginId = "transport-serial"。
//   2) SerialPortTransportClient：
//        ConnectAsync        → 从 TransportEndpoint 读取 SerialPort/BaudRate，
//                              可通过 Properties 扩展 DataBits/Parity/StopBits。
//        SendAndReceiveAsync → Write 全部请求字节，ReadTimeout 内循环 ReadAsync，
//                              静默判断策略与 TCP 相同（InterByteTimeoutMs=50ms）。
//        DisconnectAsync     → Close + Dispose
//   3) 读取策略：
//        FirstByteTimeoutMs = 3000（RTU 从站响应时间要求较严格）
//        InterByteTimeoutMs = 20（串口字节密集，帧间静默窗口更短）
//        MaxResponseBytes   = 512
//   4) Parity/DataBits/StopBits 可通过 endpoint.Properties 定制:
//        "Parity"   → None/Even/Odd/Mark/Space（默认 None）
//        "DataBits" → 5~8（默认 8）
//        "StopBits" → 1/1.5/2（默认 1）
// -----------------------------------------------------------------------------

using System;
using System.Buffers;
using System.IO.Ports;
using System.Threading;
using System.Threading.Tasks;
using CommunicationKernel.Communication.Transport.Abstractions;
using CommunicationKernel.Core.Abstractions.Errors;
using CommunicationKernel.Core.Abstractions.Results;
using CommunicationKernel.Plugin.Runtime.Abstractions;

namespace CommunicationKernel.Plugins.Transport.SerialPort;

// =============================================================================
// Manifest
// =============================================================================

/// <summary>
/// 串口传输插件清单，声明插件元数据。
/// </summary>
public sealed class SerialPortTransportPluginManifest : IPluginManifest
{
    /// <inheritdoc />
    public PluginDescriptor Descriptor { get; } = new()
    {
        PluginId    = "transport-serial",
        DisplayName = "Serial Port Transport Plugin",
        Kind        = PluginKind.Transport,
        ApiVersion  = 1,
        Version     = "1.0.0",
        EntryType   = typeof(SerialPortTransportPluginManifest).FullName
    };
}

// =============================================================================
// Factory
// =============================================================================

/// <summary>
/// 串口传输工厂，创建 <see cref="SerialPortTransportClient"/> 实例。
/// </summary>
public sealed class SerialPortTransportFactory : ITransportFactory
{
    /// <inheritdoc />
    public string TransportId     => "transport-serial";

    /// <inheritdoc />
    public TransportKind Kind     => TransportKind.Serial;

    /// <inheritdoc />
    public int PluginApiVersion   => 1;

    /// <inheritdoc />
    public ITransportClient CreateClient() => new SerialPortTransportClient();
}

// =============================================================================
// Client
// =============================================================================

/// <summary>
/// 串口传输客户端，封装 <see cref="System.IO.Ports.SerialPort"/>。
/// </summary>
public sealed class SerialPortTransportClient : ITransportClient
{
    // -------------------------------------------------------------------------
    // 常量
    // -------------------------------------------------------------------------
    private const int FirstByteTimeoutMs = 3_000;

    /// <summary>
    /// 帧已开始接收后，等待后续字节的超时（毫秒）。
    /// 这是"帧不完整"的兜底阈值，不是"帧已结束"的判定——帧边界由协议决定。
    /// 低波特率下单字节传输就需约 1 ms，取值须留足余量。
    /// </summary>
    private const int SubsequentByteTimeoutMs = 500;

    private const int MaxResponseBytes = 1024;

    // -------------------------------------------------------------------------
    // 状态
    // -------------------------------------------------------------------------
    private System.IO.Ports.SerialPort? _port;
    private bool _disposed;

    /// <summary>上一次读取中超出该帧的字节，下次读取优先消费。</summary>
    private byte[]? _residual;

    /// <summary><see cref="_residual"/> 中的有效字节数。</summary>
    private int _residualLength;

    // -------------------------------------------------------------------------
    // ITransportClient
    // -------------------------------------------------------------------------

    /// <inheritdoc />
    public string TransportId => "transport-serial";

    /// <inheritdoc />
    public TransportKind Kind => TransportKind.Serial;

    /// <inheritdoc />
    public Task<OperationResult> ConnectAsync(
        TransportEndpoint endpoint, CancellationToken cancellationToken)
    {
        ThrowIfDisposed();

        if (string.IsNullOrWhiteSpace(endpoint.SerialPort))
            return Task.FromResult(OperationResult.Fail(
                "SerialPort name is required in TransportEndpoint.SerialPort",
                KernelErrorCode.InvalidArgument));

        if (endpoint.BaudRate is null or <= 0)
            return Task.FromResult(OperationResult.Fail(
                "BaudRate is required in TransportEndpoint.BaudRate",
                KernelErrorCode.InvalidArgument));

        try
        {
            Parity   parity   = ParseParity(endpoint);
            int      dataBits = ParseDataBits(endpoint);
            StopBits stopBits = ParseStopBits(endpoint);

            _port = new System.IO.Ports.SerialPort(
                endpoint.SerialPort,
                endpoint.BaudRate.Value,
                parity,
                dataBits,
                stopBits)
            {
                ReadTimeout  = FirstByteTimeoutMs,
                WriteTimeout = 2_000,
                // 保证串口驱动缓冲尽快交付
                ReceivedBytesThreshold = 1
            };

            _port.Open();
            _port.DiscardInBuffer();
            _port.DiscardOutBuffer();

            return Task.FromResult(OperationResult.Ok);
        }
        catch (Exception ex)
        {
            DisposeInternals();
            return Task.FromResult(OperationResult.Fail(
                $"SerialPort connect failed ({endpoint}): {ex.Message}",
                KernelErrorCode.TransportIoError));
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

        if (_port is null || !_port.IsOpen)
            return OperationResult<byte[]>.Fail(
                "SerialPort not open", KernelErrorCode.TransportIoError);

        try
        {
            // 分支1：清空接收缓冲与上一帧残留，写入请求
            _port.DiscardInBuffer();
            _residualLength = 0;
            await _port.BaseStream.WriteAsync(request, 0, request.Length, cancellationToken)
                .ConfigureAwait(false);
            await _port.BaseStream.FlushAsync(cancellationToken).ConfigureAwait(false);

            // 分支2：按协议帧长读取恰好一帧
            return await ReadFrameAsync(tryGetFrameLength, cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            // 外部取消映射为 Cancelled，避免被上层判定为链路故障而触发重连
            return OperationResult<byte[]>.Fail(
                "SerialPort SendAndReceive cancelled", KernelErrorCode.Cancelled);
        }
        catch (Exception ex)
        {
            return OperationResult<byte[]>.Fail(
                $"SerialPort SendAndReceive error: {ex.Message}", KernelErrorCode.TransportIoError);
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
    /// 串口原本可用 3.5 字符帧间静默分帧，但该策略在低波特率下会误判
    /// （9600 bps 下单字节传输就要约 1 ms，固定 20 ms 阈值容易把帧切断），
    /// 且经 TCP 转串口透传装置时静默根本不被保留。统一改为按协议帧长读取。
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
                if (total > 0 && tryGetFrameLength(buffer.AsSpan(0, total), out int frameLength))
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
                        byte[] frame = new byte[frameLength];
                        Buffer.BlockCopy(buffer, 0, frame, 0, frameLength);
                        SaveResidual(buffer, frameLength, total - frameLength);
                        return OperationResult<byte[]>.Ok(frame);
                    }
                }

                if (total >= MaxResponseBytes)
                    return OperationResult<byte[]>.Fail(
                        $"响应超过单帧上限 {MaxResponseBytes} 字节仍未成帧",
                        KernelErrorCode.ProtocolError);

                int timeoutMs = total == 0 ? FirstByteTimeoutMs : SubsequentByteTimeoutMs;
                using CancellationTokenSource readCts =
                    CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
                readCts.CancelAfter(timeoutMs);

                int read;
                try
                {
                    read = await _port!.BaseStream
                        .ReadAsync(buffer, total, MaxResponseBytes - total, readCts.Token)
                        .ConfigureAwait(false);
                }
                catch (OperationCanceledException)
                {
                    if (cancellationToken.IsCancellationRequested)
                        return OperationResult<byte[]>.Fail(
                            "串口读取被取消", KernelErrorCode.Cancelled);

                    return OperationResult<byte[]>.Fail(
                        total == 0
                            ? $"等待响应首字节超时（{FirstByteTimeoutMs} ms）"
                            : $"响应帧不完整：已收 {total} 字节，后续字节等待超时（{SubsequentByteTimeoutMs} ms）",
                        KernelErrorCode.Timeout);
                }

                if (read == 0)
                    return OperationResult<byte[]>.Fail(
                        "串口流已关闭", KernelErrorCode.TransportIoError);

                total += read;
            }
        }
        finally
        {
            // 归还前清零，避免残留报文经共享池泄漏给其他消费方
            Array.Clear(buffer, 0, Math.Min(total, buffer.Length));
            ArrayPool<byte>.Shared.Return(buffer);
        }
    }

    /// <summary>保存超出本帧的字节，供下次读取优先消费。</summary>
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
    // 参数解析辅助
    // -------------------------------------------------------------------------

    private static Parity ParseParity(TransportEndpoint ep)
    {
        if (!ep.Properties.TryGetValue("Parity", out string? val))
            return Parity.None;

        return val?.ToUpperInvariant() switch
        {
            "EVEN"  => Parity.Even,
            "ODD"   => Parity.Odd,
            "MARK"  => Parity.Mark,
            "SPACE" => Parity.Space,
            _       => Parity.None
        };
    }

    private static int ParseDataBits(TransportEndpoint ep)
    {
        if (ep.Properties.TryGetValue("DataBits", out string? val)
            && int.TryParse(val, out int bits)
            && bits is >= 5 and <= 8)
            return bits;
        return 8;
    }

    private static StopBits ParseStopBits(TransportEndpoint ep)
    {
        if (!ep.Properties.TryGetValue("StopBits", out string? val))
            return StopBits.One;

        return val switch
        {
            "1"   => StopBits.One,
            "1.5" => StopBits.OnePointFive,
            "2"   => StopBits.Two,
            _     => StopBits.One
        };
    }

    // -------------------------------------------------------------------------
    // 内部辅助
    // -------------------------------------------------------------------------

    private void DisposeInternals()
    {
        if (_port is not null)
        {
            if (_port.IsOpen) _port.Close();
            _port.Dispose();
            _port = null;
        }
    }

    private void ThrowIfDisposed()
    {
        if (_disposed) throw new ObjectDisposedException(nameof(SerialPortTransportClient));
    }
}
