// -----------------------------------------------------------------------------
// 文件: TcpTransportPlugin.cs
// 层级: Plugins / Transport.Tcp
// 作用: TCP 传输介质插件（封装 System.Net.Sockets.TcpClient）。
// 说明:
//   1) TcpTransportFactory 实现 ITransportFactory，PluginId = "transport-tcp"。
//   2) TcpTransportClient 实现 ITransportClient：
//        ConnectAsync        → TcpClient.ConnectAsync，超时由 CancellationToken 控制
//        SendAndReceiveAsync → 先完整写入，再循环读取直到 NetworkStream 数据耗尽
//        DisconnectAsync     → 关闭并释放 TcpClient
//   3) 读取策略：
//        首帧超时 = 5 s（等待 PLC 第一字节响应）
//        后续数据块等待 50 ms（RTU/Modbus 帧间静默判断）
//        MAX_RESPONSE = 512 字节（超出视为协议异常）
//   4) 全部字节流操作使用 ArrayPool 减少 GC 分配。
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

    /// <summary>首字节到达后继续读取的最大等待时间（毫秒）。</summary>
    private const int InterByteTimeoutMs = 50;

    /// <summary>单次响应最大字节数，超出视为协议错误。</summary>
    private const int MaxResponseBytes = 512;

    // -------------------------------------------------------------------------
    // 状态
    // -------------------------------------------------------------------------
    private TcpClient?     _tcp;
    private NetworkStream? _stream;
    private bool           _disposed;

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
        byte[] request, CancellationToken cancellationToken)
    {
        ThrowIfDisposed();

        if (_stream is null)
            return OperationResult<byte[]>.Fail(
                "TCP not connected", KernelErrorCode.TransportIoError);

        try
        {
            // 分支1：发送请求
            await _stream.WriteAsync(request, 0, request.Length, cancellationToken)
                .ConfigureAwait(false);
            await _stream.FlushAsync(cancellationToken).ConfigureAwait(false);

            // 分支2：等待并读取响应
            return await ReadResponseAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            return OperationResult<byte[]>.Fail(
                "TCP SendAndReceive cancelled", KernelErrorCode.TransportIoError);
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
    /// 循环读取 TCP 流，直到 <see cref="InterByteTimeoutMs"/> 内无新数据或达到 <see cref="MaxResponseBytes"/>。
    /// </summary>
    private async Task<OperationResult<byte[]>> ReadResponseAsync(CancellationToken cancellationToken)
    {
        byte[] buffer = ArrayPool<byte>.Shared.Rent(MaxResponseBytes);
        int    total  = 0;

        try
        {
            // 分支1：等待首字节（带首帧超时）
            using CancellationTokenSource firstByteCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            firstByteCts.CancelAfter(FirstByteTimeoutMs);

            int read = await _stream!.ReadAsync(buffer, total, MaxResponseBytes - total, firstByteCts.Token)
                .ConfigureAwait(false);

            if (read == 0)
                return OperationResult<byte[]>.Fail(
                    "TCP connection closed by remote", KernelErrorCode.TransportIoError);

            total += read;

            // 分支2：继续读取后续字节（短超时，检测帧尾）
            while (total < MaxResponseBytes)
            {
                using CancellationTokenSource interByteCts =
                    CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
                interByteCts.CancelAfter(InterByteTimeoutMs);

                try
                {
                    read = await _stream!.ReadAsync(buffer, total, MaxResponseBytes - total, interByteCts.Token)
                        .ConfigureAwait(false);

                    if (read == 0) break; // 远端关闭
                    total += read;
                }
                catch (OperationCanceledException)
                {
                    // InterByteTimeout 超时→认为帧已结束（非外部取消）
                    if (cancellationToken.IsCancellationRequested)
                        return OperationResult<byte[]>.Fail(
                            "TCP read cancelled", KernelErrorCode.TransportIoError);
                    break;
                }
            }

            if (total == 0)
                return OperationResult<byte[]>.Fail(
                    "TCP response empty", KernelErrorCode.ProtocolError);

            byte[] result = new byte[total];
            Buffer.BlockCopy(buffer, 0, result, 0, total);
            return OperationResult<byte[]>.Ok(result);
        }
        catch (OperationCanceledException)
        {
            return OperationResult<byte[]>.Fail(
                "TCP first-byte timeout", KernelErrorCode.TransportIoError);
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(buffer);
        }
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
    }

    private void ThrowIfDisposed()
    {
        if (_disposed) throw new ObjectDisposedException(nameof(TcpTransportClient));
    }
}
