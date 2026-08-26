// -----------------------------------------------------------------------------
// 文件: RegisterRouteCommand.cs
// 层级: Core.EngineRuntime / Models
// 作用: 注册一条 PLC 路由所需的全部参数（协议、介质、地址、串口、站号）。
// -----------------------------------------------------------------------------

namespace CommunicationKernel.Core.EngineRuntime.Models;

/// <summary>注册一条路由所需的全部参数。</summary>
/// <remarks>
/// 本类型此前是 <c>EngineRuntime</c> 的嵌套类型，造成两个问题：
/// SDK 消费者要写 <c>new EngineRuntime.RegisterRouteCommand { … }</c>；
/// 且 <c>IRouteAssemblyService</c> 的参数类型指向上层具体类，
/// 任何人想另行实现该抽象都被迫依赖 <c>EngineRuntime</c>。
/// 提为顶层后，抽象与实现之间不再有类型泄漏。
/// </remarks>
public sealed class RegisterRouteCommand {
    /// <summary>调用方分配的路由标识，在宿主内全局唯一。</summary>
    public required string RouteId { get; init; }

    /// <summary>协议插件标识，须与 <c>ProtocolMetadata.ProtocolId</c> 完全一致。</summary>
    public required string ProtocolId { get; init; }

    /// <summary>可选的传输插件标识；为空时按 <see cref="TransportKind"/> 选择。</summary>
    public string? TransportId { get; init; }

    /// <summary>传输介质（"Tcp" / "Serial"），须在协议声明的支持列表内。</summary>
    public required string TransportKind { get; init; }

    /// <summary>TCP 地址；串口路由留空。</summary>
    public string? Address { get; init; }

    /// <summary>TCP 端口；串口路由为 0。</summary>
    public int Port { get; init; }

    /// <summary>设备级站号原文，由协议插件自行解释。</summary>
    public string? Station { get; init; }

    /// <summary>串口名（如 COM3 / /dev/ttyUSB0）；TCP 路由留空。</summary>
    public string? SerialPort { get; init; }

    /// <summary>串口波特率；TCP 路由为 0。</summary>
    public int BaudRate { get; init; }

    /// <summary>本路由要求的最小 I/O 间隔（毫秒），用于串口帧间静默。</summary>
    public int MinIoIntervalMs { get; init; }

    /// <summary>串口校验位（None / Even / Odd / Mark / Space）；空表示取插件默认。</summary>
    public string? Parity { get; init; }

    /// <summary>串口数据位（5-8）；0 表示取插件默认。</summary>
    public int DataBits { get; init; }

    /// <summary>串口停止位（One / OnePointFive / Two）；空表示取插件默认。</summary>
    public string? StopBits { get; init; }
}
