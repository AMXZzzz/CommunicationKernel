namespace CommunicationKernel.Communication.Protocol.Abstractions;

/// <summary>
/// -----------------------------------------------------------------------------
/// 文件: ProtocolMetadata.cs
/// 层级: Communication.Protocol / Abstractions
/// 作用: 描述协议插件的基础元信息。
/// 说明:
/// - 用于协议发现、选择、展示与版本兼容校验。
/// - 保持轻量，避免运行时状态泄漏到元信息对象中。
/// -----------------------------------------------------------------------------
/// </summary>
public sealed class ProtocolMetadata {
    /// <summary>
    /// 协议唯一标识（如 modbus-tcp、siemens-s7）。
    /// </summary>
    public string ProtocolId { get; init; } = string.Empty;

    /// <summary>
    /// 协议展示名。
    /// </summary>
    public string DisplayName { get; init; } = string.Empty;

    /// <summary>
    /// 插件 API 版本。
    /// </summary>
    public int PluginApiVersion { get; init; }
}
