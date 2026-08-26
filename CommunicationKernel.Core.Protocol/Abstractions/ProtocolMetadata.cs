using CommunicationKernel.Core.Transport.Abstractions;

// -----------------------------------------------------------------------------
// 文件: ProtocolMetadata.cs
// 层级: Core.Protocol / Abstractions
// 作用: 描述协议插件的基础元信息。
// 说明:
//   - 用于协议发现、选择、展示与版本兼容校验。
//   - 保持轻量，避免运行时状态泄漏到元信息对象中。
//   - SupportedTransports / RequiresStation / StationHint 供 UI 动态渲染设备表单：
//     UI 依据这些字段决定展示「IP+端口」还是「串口+波特率」、是否展示站号输入框，
//     从而无需在 UI 层硬编码任何协议知识。
// -----------------------------------------------------------------------------

namespace CommunicationKernel.Core.Protocol.Abstractions;

/// <summary>
/// 协议插件元信息：发现、选择、展示与版本兼容校验，不含运行时状态。
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
    /// 该协议可运行在哪些传输介质之上。
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>帧格式与传输介质是两个独立维度，不可绑定。</b>
    /// 典型反例：Modbus RTU 的帧格式为「从站号 + PDU + CRC16」，它既能跑在
    /// RS-485 上，也能经 TCP 转串口透传装置（Moxa NPort、USR-TCP232 等）
    /// 跑在以太网上——现场这种接法非常普遍。若把协议锁死为单一介质，
    /// 这类设备将完全无法接入。
    /// </para>
    /// <para>
    /// UI 在此列表长度大于 1 时应展示介质选择器，并按<b>所选介质</b>
    /// （而非协议）渲染连接参数表单。
    /// </para>
    /// </remarks>
    public IReadOnlyList<TransportKind> SupportedTransports { get; init; }
        = new[] { TransportKind.Tcp };

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
