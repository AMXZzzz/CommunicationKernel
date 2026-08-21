using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using CommunicationKernel.Core.Abstractions.Errors;
using CommunicationKernel.Core.Abstractions.Versioning;
using CommunicationKernel.Plugin.Runtime.Abstractions;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace CommunicationKernel.Plugin.Runtime.Loader;

/// <summary>
/// -----------------------------------------------------------------------------
/// 文件: PluginCatalog.cs
/// 层级: Plugin.Runtime / Loader
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
    private readonly ILogger<PluginCatalog> _logger;

    /// <summary>
    /// 初始化插件目录服务实例。
    /// </summary>
    /// <param name="logger">可选的日志记录器，用于记录插件加载过程中的信息和错误。</param>
    public PluginCatalog(ILogger<PluginCatalog>? logger = null) {
        _logger = logger ?? NullLogger<PluginCatalog>.Instance;
    }

    /// <summary>
    /// 扫描目录、校验并加载所有合法插件（单次加载，校验与运行实例同源）。
    /// </summary>
    public IReadOnlyList<PluginLoadResult> DiscoverAndLoad(string pluginDirectory) {

        //! 扫描目录、校验并加载所有合法插件（单次加载，校验与运行实例同源）。
        if (string.IsNullOrWhiteSpace(pluginDirectory) || !Directory.Exists(pluginDirectory)) {
            _logger.LogError("PluginCatalog: plugin directory not found: '{Directory}'.", pluginDirectory);
            return Array.Empty<PluginLoadResult>();
        }

        //! 扫描目录下所有 DLL 文件
        string[] dlls = Directory.GetFiles(pluginDirectory, "*.dll", SearchOption.TopDirectoryOnly);
        _logger.LogInformation("PluginCatalog: scanning {Count} DLL(s) in '{Directory}'.", dlls.Length, pluginDirectory);

        //! 尝试加载每个 DLL，并收集加载结果
        var results = new List<PluginLoadResult>();
        foreach (string dll in dlls) {
            PluginLoadResult? result = TryLoadPlugin(dll);
            if (result is not null)
                results.Add(result);
        }

        //! 记录加载结果
        _logger.LogInformation("PluginCatalog: loaded {Loaded}/{Total} plugin(s).", results.Count, dlls.Length);
        return results;
    }

    // ── Private helpers ────────────────────────────────────────────────────────

    /// <summary>
    /// 在单一 PluginLoadContext 内完成校验与加载：校验通过则保留，否则 Unload。
    /// </summary>
    private PluginLoadResult? TryLoadPlugin(string assemblyPath) {
        var loadContext = new PluginLoadContext(assemblyPath);
        try {

            //! 加载插件程序集
            Assembly assembly = loadContext.LoadFromAssemblyPath(assemblyPath);

            //! 查找实现 IPluginManifest 的类型
            Type? manifestType = assembly.GetTypes()
                .FirstOrDefault(t => !t.IsAbstract && typeof(IPluginManifest).IsAssignableFrom(t));

            //! 校验插件清单类型是否存在
            if (manifestType is null) {
                _logger.LogWarning("PluginCatalog: no IPluginManifest in '{Path}', skipped.", assemblyPath);
                loadContext.Unload();
                return null;
            }

            //! 创建插件清单实例并获取描述符
            var manifest = (IPluginManifest?)Activator.CreateInstance(manifestType);
            if (manifest?.Descriptor is null) {
                _logger.LogWarning("PluginCatalog: invalid descriptor in '{Path}', skipped.", assemblyPath);
                loadContext.Unload();
                return null;
            }

            //! 检查插件 API 版本是否与内核兼容
            PluginDescriptor descriptor = manifest.Descriptor;
            if (descriptor.ApiVersion != KernelVersions.PluginApiVersion) {
                _logger.LogWarning(
                    "PluginCatalog: '{PluginId}' API version mismatch (plugin={PluginVer}, kernel={KernelVer}), skipped.",
                    descriptor.PluginId, descriptor.ApiVersion, KernelVersions.PluginApiVersion);
                loadContext.Unload();
                return null;
            }

            //! 成功加载插件，记录日志并返回结果
            _logger.LogInformation("PluginCatalog: loaded plugin '{PluginId}' v{Version} from '{Path}'.",
                descriptor.PluginId, descriptor.Version, assemblyPath);

            //! 返回加载结果，包括插件描述符、程序集和加载上下文
            return new PluginLoadResult {
                Descriptor  = descriptor,
                Assembly    = assembly,
                LoadContext = loadContext
            };
        } catch (ReflectionTypeLoadException ex) {
            _logger.LogError(ex, "PluginCatalog: type-load error in '{Path}': {Msg}.",
                assemblyPath, ex.LoaderExceptions.FirstOrDefault()?.Message ?? ex.Message);
            loadContext.Unload();
            return null;
        } catch (Exception ex) {
            _logger.LogError(ex, "PluginCatalog: failed to load '{Path}'.", assemblyPath);
            loadContext.Unload();
            return null;
        }
    }

    /// <summary>
    /// 创建一个失败的插件验证结果对象。
    /// </summary>
    /// <param name="path"></param>
    /// <param name="code"></param>
    /// <param name="msg"></param>
    /// <returns></returns>
    private static PluginValidationResult Fail(string path, KernelErrorCode code, string msg) =>
        new PluginValidationResult { AssemblyPath = path, IsValid = false, ErrorCode = code, Message = msg };
}
