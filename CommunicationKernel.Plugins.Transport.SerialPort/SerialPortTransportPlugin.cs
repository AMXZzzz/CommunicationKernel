// -----------------------------------------------------------------------------
// 文件: SerialPortTransportPlugin.cs
// 层级: Plugins / Transport.SerialPort
// 作用: 串口传输介质插件（封装 System.IO.Ports.SerialPort）。
// 说明:
//   1) SerialPortTransportFactory：PluginId = "transport-serial"。
//   2) SerialPortTransportClient：
//        ConnectAsync        → 从 TransportEndpoint 读取 SerialPort/BaudRate，
//                              可通过 Properties 扩展 DataBits/Parity/StopBits。
//        SendAndReceiveAsync → Write 全部请求字节，再按协议给出的
//                              TryGetFrameLength 读满一整帧；
//                              多读到的字节留作残留供下一帧使用。
//        DisconnectAsync     → Close + Dispose
//   3) 帧边界由协议决定，传输层不猜：
//        Modbus RTU 传统上靠 3.5 字符静默判帧，但在 USB 转串口、
//        虚拟串口与 TCP 转串口透传网关上，字节到达时序被驱动重排，
//        静默窗口不再可靠。现由调用方传入 TryGetFrameLength，
//        传输层只负责"读够长度"。
//   4) 超时是"帧不完整"的兜底，不是"帧已结束"的判定：
//        FirstByteTimeoutMs      = 3000（RTU 从站响应时间要求较严格）
//        SubsequentByteTimeoutMs = 500 （低波特率下单字节即需约 1 ms，留足余量）
//        MaxResponseBytes        = 1024（超出视为协议异常，防止无界增长）
//   5) Parity/DataBits/StopBits 可通过 endpoint.Properties 定制:
//        "Parity"   → None/Even/Odd/Mark/Space（默认 None）
//        "DataBits" → 5~8（默认 8）
//        "StopBits" → 1/1.5/2（默认 1）
//   6) Linux/树莓派：设备名形如 /dev/ttyUSB0、/dev/ttyAMA0，
//      进程需属于 dialout 组；详见根目录《部署-Linux与树莓派.md》。
// -----------------------------------------------------------------------------

using System;
using System.Buffers;
using System.Collections.Generic;
using System.IO;
using System.IO.Ports;
using System.Threading;
using System.Threading.Tasks;
using CommunicationKernel.Core.Transport.Abstractions;
using CommunicationKernel.Core.Transport.Framing;
using CommunicationKernel.Core.Abstractions.Errors;
using CommunicationKernel.Core.Abstractions.Results;
using CommunicationKernel.Plugin.Context.Abstractions;

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
public sealed class SerialPortTransportFactory : ITransportFactory, ISerialPortEnumerator
{
    /// <inheritdoc />
    public string TransportId     => "transport-serial";

    /// <inheritdoc />
    public TransportKind Kind     => TransportKind.Serial;

    /// <inheritdoc />
    public int PluginApiVersion   => 1;

    /// <inheritdoc />
    public ITransportClient CreateClient() => new SerialPortTransportClient();

    /// <inheritdoc />
    public IReadOnlyList<SerialPortInfo> ListPorts()
    {
        // GetPortNames 在各平台的行为：
        //   Windows → 注册表里的 COMx
        //   Linux   → /dev/ttyS*、/dev/ttyUSB*、/dev/ttyACM* 等
        // 权限不足或平台不支持时抛异常；没有串口是正常状态而非错误，
        // 因此一律降级为空集合，让上层显示"未发现串口"而不是弹一个异常。
        string[] names;
        try
        {
            names = System.IO.Ports.SerialPort.GetPortNames();
        }
        catch (Exception)
        {
            return Array.Empty<SerialPortInfo>();
        }

        Array.Sort(names, StringComparer.OrdinalIgnoreCase);

        var result = new List<SerialPortInfo>(names.Length);
        // 为每个口附带 by-id 说明，方便操作员区分 USB 串口
        foreach (string name in names)
            result.Add(new SerialPortInfo(name, DescribePort(name)));

        return result;
    }

    /// <summary>为串口补一条面向操作员的说明。</summary>
    private static string DescribePort(string portName)
    {
        // Linux 上尽量给出 by-id 稳定路径：多个 USB 串口同时插着时，
        // /dev/ttyUSB0 与 ttyUSB1 的编号取决于枚举顺序，重启后可能对调。
        // 接错 PLC 的后果远比读不到数据严重，因此把稳定路径直接摆给操作员。
        if (!portName.StartsWith("/dev/", StringComparison.Ordinal))
            return string.Empty;

        try
        {
            const string byIdDir = "/dev/serial/by-id";
            if (!Directory.Exists(byIdDir)) return string.Empty;

            foreach (string link in Directory.GetFiles(byIdDir))
            {
                // 符号链接指向 ../../ttyUSB0 之类的相对路径
                string? target = File.ResolveLinkTarget(link, returnFinalTarget: true)?.FullName;
                if (string.Equals(target, portName, StringComparison.Ordinal))
                    return link;
            }
        }
        catch (Exception)
        {
            // 枚举 by-id 失败不影响主功能，返回空说明即可
        }

        return string.Empty;
    }
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

    /// <summary>
    /// 连接是否可能已失步：请求发出去了，响应却没读完。
    /// </summary>
    /// <remarks>
    /// 与 TCP 侧同源的缺陷，修法也必须一致。<c>DiscardInBuffer()</c> 只能清掉
    /// <b>已经到达</b>的字节；被取消的那次请求，其响应可能还在线上传输中，
    /// 清不掉。它随后落进缓冲，就成了下一次请求读到的"响应"，此后永久错位一格。
    /// <para>
    /// 串口这边比 TCP 更要命：Modbus RTU 与 MEWTOCOL <b>都没有事务 ID</b>，
    /// 错位的响应帧格式完整、CRC/BCC 也全对，没有任何一层能认出它不属于本次请求——
    /// 现场表现是每个寄存器都稳定地显示上一次的值，而且一直"成功"。
    /// </para>
    /// <para>
    /// 一旦置位就以 <see cref="KernelErrorCode.TransportIoError"/> 失败，
    /// 交给上层重连——重开串口是唯一能确保重新对齐的手段。
    /// </para>
    /// </remarks>
    private bool _desynced;

    /// <summary>
    /// 分帧读取器：残留处理、两级超时、上限保护、取消与超时的区分全在其中。
    /// </summary>
    /// <remarks>
    /// 与 TCP 插件共用同一实现。此前两边各有一份逐行雷同的读循环，
    /// 同一处缺陷要改两遍——本项目已经栽过一次「同类缺陷只改了一个插件」的跟头。
    /// </remarks>
    private readonly FrameReader _frameReader = new(
        MaxResponseBytes, FirstByteTimeoutMs, SubsequentByteTimeoutMs, "串口");

    // -------------------------------------------------------------------------
    // ITransportClient
    // -------------------------------------------------------------------------

    /// <inheritdoc />
    public string TransportId => "transport-serial";

    /// <inheritdoc />
    public TransportKind Kind => TransportKind.Serial;

    /// <inheritdoc />
    /// <remarks>
    /// 串口只能查到「端口句柄还开着」。USB 转串口被拔掉时驱动会让端口失效，
    /// 这里能查出来；但线缆脱落、PLC 掉电对串口而言毫无迹象——
    /// 那种情况只能靠真正发一帧出去、等不到响应才发现。
    /// </remarks>
    public bool IsConnectionAlive
    {
        get
        {
            try { return _port?.IsOpen == true; }
            catch (Exception) { return false; }
        }
    }

    /// <inheritdoc />
    public Task<OperationResult> ConnectAsync(
        TransportEndpoint endpoint, CancellationToken cancellationToken)
    {
        ThrowIfDisposed();

        // 串口名缺失时尽早失败，避免 SerialPort 构造抛含义不清的异常
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
            // Properties 可覆盖校验/数据位/停止位，缺省 8N1
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

            // 新打开的串口上不存在"欠着的响应"，失步标记必须清零。
            // 不能只依赖调用方先 Disconnect 再 Connect——传输层的状态应当自洽。
            _desynced = false;

            return Task.FromResult(OperationResult.Ok);
        }
        catch (Exception ex)
        {
            // 打开失败（被占用、权限不足、设备不存在）必须释放半开对象
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

        // 帧边界必须由协议给出（RTU 按功能码推定、ASCII 扫 CRLF）
        if (tryGetFrameLength is null)
            return OperationResult<byte[]>.Fail(
                "tryGetFrameLength is required", KernelErrorCode.InvalidArgument);

        if (_port is null || !_port.IsOpen)
            return OperationResult<byte[]>.Fail(
                "SerialPort not open", KernelErrorCode.TransportIoError);

        // 上一次请求发出后没能读完响应：请求/响应的对应关系已不可信，
        // 直接以 IO 错误失败，让上层重开串口。
        if (_desynced)
            return OperationResult<byte[]>.Fail(
                "上一次请求被中断，串口可能失步，需要重连", KernelErrorCode.TransportIoError);

        bool requestSent = false;
        try
        {
            // 分支1：清空接收缓冲与上一帧残留，写入请求
            _port.DiscardInBuffer();
            _frameReader.DiscardResidual();
            await _port.BaseStream.WriteAsync(request, 0, request.Length, cancellationToken)
                .ConfigureAwait(false);
            await _port.BaseStream.FlushAsync(cancellationToken).ConfigureAwait(false);

            // 从这一刻起，从站欠我们一个响应。中途退出即意味着失步
            requestSent = true;

            // 分支2：按协议帧长读取恰好一帧
            OperationResult<byte[]> frame = await _frameReader
                .ReadFrameAsync(_port.BaseStream, tryGetFrameLength, cancellationToken)
                .ConfigureAwait(false);

            // 读到完整一帧才算把这次响应消费干净
            if (frame.Success) requestSent = false;
            return frame;
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
        finally
        {
            // 请求已发出但没读完响应：标记失步，下次收发直接要求重连。
            // 放 finally 而非各 catch 分支：将来新增退出路径也不会漏。
            if (requestSent) _desynced = true;
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
    // 参数解析辅助
    // -------------------------------------------------------------------------

    private static Parity ParseParity(TransportEndpoint ep)
    {
        // 未配置时默认 None（8N1）
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
        // 仅接受 5-8；非法值回落 8，避免 SerialPort 构造抛 ArgumentOutOfRange
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
            // 先 Close 再 Dispose，避免驱动缓冲未刷完
            if (_port.IsOpen) _port.Close();
            _port.Dispose();
            _port = null;
        }

        // 失步标记随串口一起清掉：重开的串口上没有欠着的响应。
        // 忘了清会让这条路由重连之后仍然每次失败，表现为设备再也连不上。
        _desynced = false;
    }

    private void ThrowIfDisposed()
    {
        // 已 Dispose 后禁止再 Connect/Send，避免对已关闭串口操作
        if (_disposed) throw new ObjectDisposedException(nameof(SerialPortTransportClient));
    }
}
