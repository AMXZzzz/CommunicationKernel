// -----------------------------------------------------------------------------
// 文件: PluginCatalog.cs
// 层级: Plugin.Context / Loader
// 作用: 提供插件"发现 -> 校验 -> 加载"流程的统一入口。
// 设计原则:
// 1) 校验与加载合并为单次加载：在 PluginLoadContext 内完成 ApiVersion 检查，
//    校验通过则保留上下文，否则 Unload()。消除原先双重加载的 IO 开销，
//    同时保证校验结论与实际运行实例来自同一次加载，杜绝验证绕过窗口。
// 2) 失败隔离：单插件异常不影响其他插件处理。
// 3) 版本前置：在进入业务流程前完成 API 兼容性判断。
// 4) 可诊断：通过统一结果对象保留错误码与可读消息，并输出结构化日志。
// -----------------------------------------------------------------------------

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using CommunicationKernel.Core.Abstractions.Errors;
using CommunicationKernel.Core.Abstractions.Versioning;
using CommunicationKernel.Plugin.Context.Abstractions;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace CommunicationKernel.Plugin.Context;

/// <summary>
/// -----------------------------------------------------------------------------
/// 文件: PluginCatalog.cs
/// 层级: Plugin.Context / Loader
/// 作用: 提供插件"发现 -> 校验 -> 加载"流程的统一入口。
/// 设计原则:
/// 1) 校验与加载合并为单次加载：在 PluginLoadContext 内完成 ApiVersion 检查，
///    校验通过则保留上下文，否则 Unload()。消除原先双重加载的 IO 开销，
///    同时保证校验结论与实际运行实例来自同一次加载，杜绝验证绕过窗口。
/// 2) 失败隔离：单插件异常不影响其他插件处理。
/// 3) 版本前置：在进入业务流程前完成 API 兼容性判断。
/// 4) 可诊断：通过统一结果对象保留错误码与可读消息，并输出结构化日志。
/// -----------------------------------------------------------------------------
/// </summary>
public sealed class PluginCatalog {
    // 加载过程的结构化日志；未注入时退化为空记录器
    private readonly ILogger<PluginCatalog> _logger;

    /// <summary>
    /// 初始化插件目录服务实例。
    /// </summary>
    /// <param name="logger">可选的日志记录器，用于记录插件加载过程中的信息和错误。</param>
    public PluginCatalog(ILogger<PluginCatalog>? logger = null) {
        // 未注入日志时用 NullLogger，保证扫描路径上任何 LogXxx 都可安全调用
        _logger = logger ?? NullLogger<PluginCatalog>.Instance;
    }

    // ============================================================================
    // 发现并加载
    // ============================================================================

    /// <summary>
    /// 扫描目录、校验并加载所有合法插件（单次加载，校验与运行实例同源）。
    /// </summary>
    public IReadOnlyList<PluginLoadResult> DiscoverAndLoad(string pluginDirectory) {

        // 目录缺失或未配置：无法扫描，返回空清单（宿主启动日志会据此告警）
        if (string.IsNullOrWhiteSpace(pluginDirectory) || !Directory.Exists(pluginDirectory)) {
            _logger.LogError("PluginCatalog: plugin directory not found: '{Directory}'.", pluginDirectory);
            return Array.Empty<PluginLoadResult>();
        }

        // 只扫顶层 *.dll：子目录由各插件自己的 LoadContext 解析，不递归以免误载依赖
        string[] dlls = Directory.GetFiles(pluginDirectory, "*.dll", SearchOption.TopDirectoryOnly);
        _logger.LogInformation("PluginCatalog: scanning {Count} DLL(s) in '{Directory}'.", dlls.Length, pluginDirectory);

        // 逐 DLL 隔离：单个插件失败不影响其余协议/传输继续注册
        var results = new List<PluginLoadResult>();
        foreach (string dll in dlls) {
            // 校验+加载在同一上下文内完成，失败返回 null 并由 TryLoadPlugin 自行 Unload
            PluginLoadResult? result = TryLoadPlugin(dll);

            // 合法插件才纳入清单，供 RouteAssembler 实例化工厂
            if (result is not null)
                results.Add(result);
        }

        // 启动诊断：loaded/total 对不上说明有 DLL 被跳过，应核对共享契约泄漏或版本
        _logger.LogInformation("PluginCatalog: loaded {Loaded}/{Total} plugin(s).", results.Count, dlls.Length);
        return results;
    }

    // ============================================================================
    // 单插件校验与加载
    // ============================================================================

    /// <summary>
    /// 在单一 PluginLoadContext 内完成校验与加载：校验通过则保留，否则 Unload。
    /// </summary>
    private PluginLoadResult? TryLoadPlugin(string assemblyPath) {
        // 独立可回收上下文：依赖隔离，校验失败可立即 Unload
        var loadContext = new PluginLoadContext(assemblyPath);
        try {

            // 把主 DLL 装进该上下文，后续 GetTypes 看到的是这份实例
            Assembly assembly = loadContext.LoadFromAssemblyPath(assemblyPath);

            // 清单类型是发现入口：没有 IPluginManifest 的 DLL 不是插件（可能是私有依赖）
            Type? manifestType = assembly.GetTypes()
                .FirstOrDefault(t => !t.IsAbstract && typeof(IPluginManifest).IsAssignableFrom(t));

            // 无清单则卸载上下文，避免残留 collectible ALC
            if (manifestType is null) {
                _logger.LogWarning("PluginCatalog: no IPluginManifest in '{Path}', skipped.", assemblyPath);
                loadContext.Unload();
                return null;
            }

            // 无参构造实例化清单并读取身份卡；失败同样卸载
            var manifest = (IPluginManifest?)Activator.CreateInstance(manifestType);
            if (manifest?.Descriptor is null) {
                _logger.LogWarning("PluginCatalog: invalid descriptor in '{Path}', skipped.", assemblyPath);
                loadContext.Unload();
                return null;
            }

            // API 版本必须与内核精确匹配，否则协议/传输契约会对不上
            PluginDescriptor descriptor = manifest.Descriptor;
            if (descriptor.ApiVersion != KernelVersions.PluginApiVersion) {
                _logger.LogWarning(
                    "PluginCatalog: '{PluginId}' API version mismatch (plugin={PluginVer}, kernel={KernelVer}), skipped.",
                    descriptor.PluginId, descriptor.ApiVersion, KernelVersions.PluginApiVersion);
                loadContext.Unload();
                return null;
            }

            // 校验通过：保留上下文，供后续工厂实例化使用
            _logger.LogInformation("PluginCatalog: loaded plugin '{PluginId}' v{Version} from '{Path}'.",
                descriptor.PluginId, descriptor.Version, assemblyPath);

            // 快照 Descriptor / Assembly / LoadContext，卸载权交给宿主生命周期
            return new PluginLoadResult {
                Descriptor  = descriptor,
                Assembly    = assembly,
                LoadContext = loadContext
            };
        } catch (ReflectionTypeLoadException ex) {
            // 缺共享契约或依赖版本冲突时常见：记下 LoaderException 并卸载
            _logger.LogError(ex, "PluginCatalog: type-load error in '{Path}': {Msg}.",
                assemblyPath, ex.LoaderExceptions.FirstOrDefault()?.Message ?? ex.Message);
            loadContext.Unload();
            return null;
        } catch (Exception ex) {
            // 其余异常（IO、BadImage、静态构造失败）一律隔离，不中断目录扫描
            _logger.LogError(ex, "PluginCatalog: failed to load '{Path}'.", assemblyPath);
            loadContext.Unload();
            return null;
        }
    }

}
