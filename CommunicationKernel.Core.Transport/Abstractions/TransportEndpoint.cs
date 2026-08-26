using System.Collections.Generic;

// -----------------------------------------------------------------------------
// 文件: TransportEndpoint.cs
// 层级: Core.Transport / Abstractions
// 作用: 描述一次传输连接所需的终端参数。
// 说明:
//   - 统一承载不同介质连接参数，避免上层关心具体介质细节。
//   - 串口介质使用 SerialPort/BaudRate；网络介质使用 Address/Port。
//   - Properties 预留扩展参数（如蓝牙地址类型、串口校验位等）。
// -----------------------------------------------------------------------------

namespace CommunicationKernel.Core.Transport.Abstractions;

/// <summary>
/// 一次传输连接所需的终端参数（串口名/波特率或 IP:端口）。
/// </summary>
public sealed class TransportEndpoint {
    /// <summary>
    /// 目标介质类型。
    /// </summary>
    public TransportKind Kind { get; init; }

    /// <summary>
    /// 网络地址（适用于 TCP/WiFi 等）。
    /// </summary>
    public string Address { get; init; } = string.Empty;

    /// <summary>
    /// 网络端口（适用于 TCP/WiFi 等）。
    /// </summary>
    public int Port { get; init; }

    /// <summary>
    /// 串口端口名（如 COM3、/dev/ttyUSB0）。
    /// </summary>
    public string? SerialPort { get; init; }

    /// <summary>
    /// 串口波特率（如 9600/115200）。
    /// </summary>
    public int? BaudRate { get; init; }

    /// <summary>
    /// 可扩展属性集合。
    /// </summary>
    public IDictionary<string, string> Properties { get; init; } = new Dictionary<string, string>();

    /// <summary>
    /// 返回适合诊断日志使用的端点字符串。
    /// </summary>
    public override string ToString() {
        // 串口链路没有 IP:Port 语义，使用 端口名@波特率 便于现场排障
        if (Kind == TransportKind.Serial) {
            return $"{Kind}:{SerialPort}@{BaudRate}";
        }

        // 网络或其他介质：统一输出 Address:Port，便于日志检索与链路追踪
        return $"{Kind}:{Address}:{Port}";
    }
}
