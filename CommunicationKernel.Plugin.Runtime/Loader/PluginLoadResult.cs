using System.Reflection;
using System.Runtime.Loader;
using CommunicationKernel.Plugin.Runtime.Abstractions;

namespace CommunicationKernel.Plugin.Runtime.Loader;

/// <summary>
/// -----------------------------------------------------------------------------
/// 文件: PluginLoadResult.cs
/// 层级: Plugin.Runtime / Loader
/// 作用: 表示插件成功加载后的结果快照。
/// 说明:
/// - 聚合 Descriptor、Assembly 与 LoadContext，便于宿主后续组装与管理。
/// - LoadContext 被显式保留，便于后续实现插件卸载与诊断。
/// -----------------------------------------------------------------------------
/// </summary>
public sealed class PluginLoadResult {
    /// <summary>
    /// 插件元数据描述。
    /// </summary>
    public required PluginDescriptor Descriptor { get; init; }

    /// <summary>
    /// 已加载的插件程序集。
    /// </summary>
    public required Assembly Assembly { get; init; }

    /// <summary>
    /// 插件所属的加载上下文。
    /// </summary>
    public required AssemblyLoadContext LoadContext { get; init; }
}
