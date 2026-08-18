using CommunicationKernel.Core.Abstractions.Errors;
using CommunicationKernel.Plugin.Runtime.Abstractions;

namespace CommunicationKernel.Plugin.Runtime.Loader;

/// <summary>
/// -----------------------------------------------------------------------------
/// 文件: PluginValidationResult.cs
/// 层级: Plugin.Runtime / Loader
/// 作用: 表示单个插件程序集在“发现/校验”阶段的结果。
/// 说明:
/// - IsValid=true 表示可参与加载。
/// - ErrorCode/Message 用于诊断失败原因。
/// - Descriptor 在校验成功或部分失败（如版本不匹配）时可携带上下文。
/// -----------------------------------------------------------------------------
/// </summary>
public sealed class PluginValidationResult {
    /// <summary>
    /// 被校验的插件程序集路径。
    /// </summary>
    public required string AssemblyPath { get; init; }

    /// <summary>
    /// 校验是否通过。
    /// </summary>
    public bool IsValid { get; init; }

    /// <summary>
    /// 失败或状态对应的统一内核错误码。
    /// </summary>
    public KernelErrorCode ErrorCode { get; init; }

    /// <summary>
    /// 人类可读的校验消息。
    /// </summary>
    public string Message { get; init; } = string.Empty;

    /// <summary>
    /// 校验过程提取到的插件描述信息。
    /// </summary>
    public PluginDescriptor? Descriptor { get; init; }
}
