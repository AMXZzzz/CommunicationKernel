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
    private const int InterByteTimeoutMs = 20;
    private const int MaxResponseBytes   = 512;

    // -------------------------------------------------------------------------
    // 状态
    // -------------------------------------------------------------------------
    private System.IO.Ports.SerialPort? _port;
    private bool _disposed;

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
        byte[] request, CancellationToken cancellationToken)
    {
        ThrowIfDisposed();

        if (_port is null || !_port.IsOpen)
            return OperationResult<byte[]>.Fail(
                "SerialPort not open", KernelErrorCode.TransportIoError);

        try
        {
            // 分支1：清空接收缓冲，写入请求
            _port.DiscardInBuffer();
            await _port.BaseStream.WriteAsync(request, 0, request.Length, cancellationToken)
                .ConfigureAwait(false);
            await _port.BaseStream.FlushAsync(cancellationToken).ConfigureAwait(false);

            // 分支2：读取响应
            return await ReadResponseAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            return OperationResult<byte[]>.Fail(
                "SerialPort SendAndReceive cancelled", KernelErrorCode.TransportIoError);
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

    private async Task<OperationResult<byte[]>> ReadResponseAsync(CancellationToken cancellationToken)
    {
        byte[] buffer = ArrayPool<byte>.Shared.Rent(MaxResponseBytes);
        int    total  = 0;

        try
        {
            // 分支1：等待首字节（带首帧超时）
            using CancellationTokenSource firstByteCts =
                CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            firstByteCts.CancelAfter(FirstByteTimeoutMs);

            int read = await _port!.BaseStream
                .ReadAsync(buffer, total, MaxResponseBytes - total, firstByteCts.Token)
                .ConfigureAwait(false);

            if (read == 0)
                return OperationResult<byte[]>.Fail(
                    "SerialPort: no data received (stream closed)", KernelErrorCode.TransportIoError);

            total += read;

            // 分支2：继续读取后续字节（RTU/ASCII 帧间静默判断）
            while (total < MaxResponseBytes)
            {
                using CancellationTokenSource interByteCts =
                    CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
                interByteCts.CancelAfter(InterByteTimeoutMs);

                try
                {
                    read = await _port!.BaseStream
                        .ReadAsync(buffer, total, MaxResponseBytes - total, interByteCts.Token)
                        .ConfigureAwait(false);

                    if (read == 0) break;
                    total += read;
                }
                catch (OperationCanceledException)
                {
                    if (cancellationToken.IsCancellationRequested)
                        return OperationResult<byte[]>.Fail(
                            "SerialPort read cancelled", KernelErrorCode.TransportIoError);
                    break; // InterByteTimeout → 帧结束
                }
            }

            if (total == 0)
                return OperationResult<byte[]>.Fail(
                    "SerialPort response empty", KernelErrorCode.ProtocolError);

            byte[] result = new byte[total];
            Buffer.BlockCopy(buffer, 0, result, 0, total);
            return OperationResult<byte[]>.Ok(result);
        }
        catch (OperationCanceledException)
        {
            return OperationResult<byte[]>.Fail(
                "SerialPort first-byte timeout", KernelErrorCode.TransportIoError);
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(buffer);
        }
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
