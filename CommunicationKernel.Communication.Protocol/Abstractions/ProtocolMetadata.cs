using CommunicationKernel.Communication.Transport.Abstractions;

namespace CommunicationKernel.Communication.Protocol.Abstractions;

/// <summary>
/// -----------------------------------------------------------------------------
/// 文件: ProtocolMetadata.cs
/// 层级: Communication.Protocol / Abstractions
/// 作用: 描述协议插件的基础元信息。
/// 说明:
/// - 用于协议发现、选择、展示与版本兼容校验。
/// - 保持轻量，避免运行时状态泄漏到元信息对象中。
/// - TransportKind / RequiresStation / StationHint 供 UI 动态渲染设备表单：
///   UI 依据这些字段决定展示「IP+端口」还是「串口+波特率」、是否展示站号输入框，
///   从而无需在 UI 层硬编码任何协议知识。
/// -----------------------------------------------------------------------------
/// </summary>
public sealed class ProtocolMetadata {
    /// <summary>
    /// 协议唯一标识（如 modbus-tcp、siemens-s7-1200）。
    /// 这是 RegisterRoute.protocol_id 的取值，UI 必须原样回传，不得使用 DisplayName。
    /// </summary>
    public string ProtocolId { get; init; } = string.Empty;

    /// <summary>
    /// 协议展示名，仅用于界面显示，不参与任何匹配逻辑。
    /// </summary>
    public string DisplayName { get; init; } = string.Empty;

    /// <summary>
    /// 该协议运行在何种传输介质之上。
    /// UI 据此自动填充 RegisterRoute.transport_kind，并切换连接参数表单。
    /// </summary>
    public TransportKind TransportKind { get; init; } = TransportKind.Tcp;

    /// <summary>
    /// 该协议是否需要站号 / 从站地址。
    /// 为 true 时 UI 应展示站号输入框，并将其填入 RegisterRoute.station；
    /// 为 false 时（如 S7 以 Rack/Slot 固化在 TSAP 中）UI 应隐藏该输入框。
    /// </summary>
    public bool RequiresStation { get; init; }

    /// <summary>
    /// 站号输入框的取值范围提示文案，供 UI 直接显示给操作员。
    /// 例如 "从站地址 1-247"。<see cref="RequiresStation"/> 为 false 时忽略。
    /// </summary>
    public string StationHint { get; init; } = string.Empty;

    /// <summary>
    /// 插件 API 版本。
    /// </summary>
    public int PluginApiVersion { get; init; }
}
