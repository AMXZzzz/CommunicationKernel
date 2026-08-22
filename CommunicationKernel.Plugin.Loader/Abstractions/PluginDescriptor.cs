// -----------------------------------------------------------------------------
// 文件: PluginDescriptor.cs
// 层级: Plugin.Loader / Abstractions
// 作用: 描述插件元数据，用于插件发现、版本校验、分类路由与运维展示。
// 说明:
// - Descriptor 是插件“身份卡”，由插件自行声明。
// - 宿主基于该对象做 API 版本兼容判断，避免运行期协议不匹配。
// - 该对象应保持可序列化、可记录、可诊断，不包含运行时句柄。
// -----------------------------------------------------------------------------

namespace CommunicationKernel.Plugin.Loader.Abstractions;

/// <summary>
/// -----------------------------------------------------------------------------
/// 文件: PluginDescriptor.cs
/// 层级: Plugin.Loader / Abstractions
/// 作用: 描述插件元数据，用于插件发现、版本校验、分类路由与运维展示。
/// 说明:
/// - Descriptor 是插件“身份卡”，由插件自行声明。
/// - 宿主基于该对象做 API 版本兼容判断，避免运行期协议不匹配。
/// - 该对象应保持可序列化、可记录、可诊断，不包含运行时句柄。
/// -----------------------------------------------------------------------------
/// </summary>
public sealed class PluginDescriptor {
    /// <summary>
    /// 插件唯一标识（逻辑主键）。
    /// 建议在同一产品线内保持稳定且全局唯一。
    /// </summary>
    public required string PluginId { get; init; }

    /// <summary>
    /// 插件展示名称（用于 UI、日志、诊断页面）。
    /// </summary>
    public required string DisplayName { get; init; }

    /// <summary>
    /// 插件能力分类（传输类或协议类）。
    /// </summary>
    public required PluginKind Kind { get; init; }

    /// <summary>
    /// 插件声明的 API 版本。
    /// 运行时会与内核要求版本进行精确匹配校验。
    /// </summary>
    public required int ApiVersion { get; init; }

    /// <summary>
    /// 插件自身语义版本（例如 1.2.3）。
    /// 该字段用于运维展示与问题回溯，不参与强校验。
    /// </summary>
    public string? Version { get; init; }

    /// <summary>
    /// 插件入口类型全名（可选）。
    /// 当需要按约定激活具体类型时可使用该字段。
    /// </summary>
    public string? EntryType { get; init; }

    /// <summary>
    /// 插件程序集实际路径（可选）。
    /// 通常在发现阶段由运行时回填，便于定位文件来源。
    /// </summary>
    public string? AssemblyPath { get; init; }
}
