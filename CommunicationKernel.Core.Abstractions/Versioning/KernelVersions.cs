// -----------------------------------------------------------------------------
// 文件: KernelVersions.cs
// 层级: Core.Abstractions / Versioning
// 作用: 集中声明内核与插件之间的 API 版本契约。
// -----------------------------------------------------------------------------

namespace CommunicationKernel.Core.Abstractions.Versioning;

/// <summary>
/// 内核版本常量。插件加载时与清单中的 ApiVersion 比对，不匹配则拒绝加载。
/// </summary>
public static class KernelVersions {
    /// <summary>
    /// 当前插件 API 版本。传输/协议插件声明的版本必须与此一致，
    /// 否则宿主报 <c>PluginApiVersionMismatch</c> 并跳过该插件。
    /// </summary>
    public const int PluginApiVersion = 1;
}
