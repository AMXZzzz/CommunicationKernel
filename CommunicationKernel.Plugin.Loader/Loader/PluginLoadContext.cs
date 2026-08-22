// -----------------------------------------------------------------------------
// 文件: PluginLoadContext.cs
// 层级: Plugin.Loader / Loader
// 作用: 为每个插件创建独立 AssemblyLoadContext，实现依赖隔离与可回收加载。
// 说明:
// 1) 每个插件使用独立上下文，避免不同插件间依赖版本冲突。
// 2) isCollectible=true 支持后续卸载（在无活动引用时由 GC 回收）。
// 3) 通过 AssemblyDependencyResolver 在插件目录内解析私有依赖。
// -----------------------------------------------------------------------------

using System;
using System.IO;
using System.Reflection;
using System.Runtime.Loader;

namespace CommunicationKernel.Plugin.Loader;

/// <summary>
/// -----------------------------------------------------------------------------
/// 文件: PluginLoadContext.cs
/// 层级: Plugin.Loader / Loader
/// 作用: 为每个插件创建独立 AssemblyLoadContext，实现依赖隔离与可回收加载。
/// 说明:
/// 1) 每个插件使用独立上下文，避免不同插件间依赖版本冲突。
/// 2) isCollectible=true 支持后续卸载（在无活动引用时由 GC 回收）。
/// 3) 通过 <see cref="AssemblyDependencyResolver"/> 在插件目录内解析私有依赖。
/// -----------------------------------------------------------------------------
/// </summary>
internal sealed class PluginLoadContext : AssemblyLoadContext {
    // 按插件主 DLL 所在目录解析 deps.json / 同目录私有依赖
    private readonly AssemblyDependencyResolver _resolver;

    /// <summary>
    /// 使用插件主程序集路径初始化加载上下文。
    /// </summary>
    /// <param name="pluginAssemblyPath">插件主程序集绝对路径。</param>
    public PluginLoadContext(string pluginAssemblyPath)
        // 上下文名含文件名与 GUID，便于诊断；isCollectible 允许后续 Unload
        : base($"plugin:{Path.GetFileNameWithoutExtension(pluginAssemblyPath)}:{Guid.NewGuid():N}", isCollectible: true) {
        // 绑定到该插件目录，私有依赖优先在此解析，避免与其他插件版本冲突
        _resolver = new AssemblyDependencyResolver(pluginAssemblyPath);
    }

    /// <summary>
    /// 解析并加载指定程序集。
    /// </summary>
    /// <param name="assemblyName">待解析程序集名称。</param>
    /// <returns>解析成功返回程序集；否则返回 null，让默认上下文继续尝试。</returns>
    protected override Assembly? Load(AssemblyName assemblyName) {
        // 先在插件目录内找私有依赖（协议/传输插件自带的第三方库）
        string? path = _resolver.ResolveAssemblyToPath(assemblyName);

        // 解析不到则返回 null，交给默认上下文继续找（共享契约如 Core.Abstractions）
        return path is null ? null : LoadFromAssemblyPath(path);
    }
}
