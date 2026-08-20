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

    public PluginCatalog(ILogger<PluginCatalog>? logger = null) {
        _logger = logger ?? NullLogger<PluginCatalog>.Instance;
    }

    /// <summary>
    /// 扫描目录、校验并加载所有合法插件（单次加载，校验与运行实例同源）。
    /// </summary>
    public IReadOnlyList<PluginLoadResult> DiscoverAndLoad(string pluginDirectory) {
        if (string.IsNullOrWhiteSpace(pluginDirectory) || !Directory.Exists(pluginDirectory)) {
            _logger.LogError("PluginCatalog: plugin directory not found: '{Directory}'.", pluginDirectory);
            return Array.Empty<PluginLoadResult>();
        }

        string[] dlls = Directory.GetFiles(pluginDirectory, "*.dll", SearchOption.TopDirectoryOnly);
        _logger.LogInformation("PluginCatalog: scanning {Count} DLL(s) in '{Directory}'.", dlls.Length, pluginDirectory);

        var results = new List<PluginLoadResult>();
        foreach (string dll in dlls) {
            PluginLoadResult? result = TryLoadPlugin(dll);
            if (result is not null)
                results.Add(result);
        }

        _logger.LogInformation("PluginCatalog: loaded {Loaded}/{Total} plugin(s).", results.Count, dlls.Length);
        return results;
    }

    /// <summary>
    /// DiscoverAndValidate — 保留旧签名供现有外部代码兼容，内部委托 DiscoverAndLoad。
    /// </summary>
    public IReadOnlyList<PluginValidationResult> DiscoverAndValidate(string pluginDirectory) {
        if (string.IsNullOrWhiteSpace(pluginDirectory) || !Directory.Exists(pluginDirectory)) {
            _logger.LogError("PluginCatalog: plugin directory not found: '{Directory}'.", pluginDirectory);
            return new[] {
                new PluginValidationResult {
                    AssemblyPath = pluginDirectory ?? string.Empty,
                    IsValid      = false,
                    ErrorCode    = KernelErrorCode.PluginNotFound,
                    Message      = "Plugin directory not found"
                }
            };
        }

        var validationResults = new List<PluginValidationResult>();
        foreach (string dll in Directory.GetFiles(pluginDirectory, "*.dll", SearchOption.TopDirectoryOnly))
            validationResults.Add(ValidateOnly(dll));

        return validationResults;
    }

    /// <summary>
    /// LoadValidPlugins — 保留旧签名供外部调用；内部按校验结果加载。
    /// </summary>
    public IReadOnlyList<PluginLoadResult> LoadValidPlugins(IEnumerable<PluginValidationResult> validations) {
        ArgumentNullException.ThrowIfNull(validations);

        var loaded = new List<PluginLoadResult>();
        foreach (PluginValidationResult v in validations.Where(x => x.IsValid && x.Descriptor != null)) {
            PluginLoadResult? result = TryLoadPlugin(v.AssemblyPath);
            if (result is not null)
                loaded.Add(result);
        }
        return loaded;
    }

    // ── Private helpers ────────────────────────────────────────────────────────

    /// <summary>
    /// 在单一 PluginLoadContext 内完成校验与加载：校验通过则保留，否则 Unload。
    /// </summary>
    private PluginLoadResult? TryLoadPlugin(string assemblyPath) {
        var loadContext = new PluginLoadContext(assemblyPath);
        try {
            Assembly assembly = loadContext.LoadFromAssemblyPath(assemblyPath);

            Type? manifestType = assembly.GetTypes()
                .FirstOrDefault(t => !t.IsAbstract && typeof(IPluginManifest).IsAssignableFrom(t));

            if (manifestType is null) {
                _logger.LogWarning("PluginCatalog: no IPluginManifest in '{Path}', skipped.", assemblyPath);
                loadContext.Unload();
                return null;
            }

            var manifest = (IPluginManifest?)Activator.CreateInstance(manifestType);
            if (manifest?.Descriptor is null) {
                _logger.LogWarning("PluginCatalog: invalid descriptor in '{Path}', skipped.", assemblyPath);
                loadContext.Unload();
                return null;
            }

            PluginDescriptor descriptor = manifest.Descriptor;
            if (descriptor.ApiVersion != KernelVersions.PluginApiVersion) {
                _logger.LogWarning(
                    "PluginCatalog: '{PluginId}' API version mismatch (plugin={PluginVer}, kernel={KernelVer}), skipped.",
                    descriptor.PluginId, descriptor.ApiVersion, KernelVersions.PluginApiVersion);
                loadContext.Unload();
                return null;
            }

            _logger.LogInformation("PluginCatalog: loaded plugin '{PluginId}' v{Version} from '{Path}'.",
                descriptor.PluginId, descriptor.Version, assemblyPath);

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
    /// 轻量校验（仅用于 DiscoverAndValidate 兼容路径），不保留加载上下文。
    /// </summary>
    private PluginValidationResult ValidateOnly(string assemblyPath) {
        var loadContext = new PluginLoadContext(assemblyPath);
        try {
            Assembly assembly = loadContext.LoadFromAssemblyPath(assemblyPath);

            Type? manifestType = assembly.GetTypes()
                .FirstOrDefault(t => !t.IsAbstract && typeof(IPluginManifest).IsAssignableFrom(t));

            if (manifestType is null) {
                loadContext.Unload();
                return Fail(assemblyPath, KernelErrorCode.PluginLoadFailed, "No IPluginManifest implementation");
            }

            var manifest = (IPluginManifest?)Activator.CreateInstance(manifestType);
            if (manifest?.Descriptor is null) {
                loadContext.Unload();
                return Fail(assemblyPath, KernelErrorCode.PluginLoadFailed, "Invalid plugin descriptor");
            }

            PluginDescriptor descriptor = manifest.Descriptor;
            loadContext.Unload();

            if (descriptor.ApiVersion != KernelVersions.PluginApiVersion)
                return new PluginValidationResult {
                    AssemblyPath = assemblyPath, IsValid = false,
                    ErrorCode    = KernelErrorCode.PluginApiVersionMismatch,
                    Message      = $"API version mismatch: {descriptor.ApiVersion}", Descriptor = descriptor
                };

            return new PluginValidationResult {
                AssemblyPath = assemblyPath, IsValid = true,
                ErrorCode    = KernelErrorCode.None, Message = "OK", Descriptor = descriptor
            };
        } catch (ReflectionTypeLoadException ex) {
            loadContext.Unload();
            return Fail(assemblyPath, KernelErrorCode.PluginIsolationError,
                ex.LoaderExceptions.FirstOrDefault()?.Message ?? ex.Message);
        } catch (Exception ex) {
            loadContext.Unload();
            return Fail(assemblyPath, KernelErrorCode.PluginLoadFailed, ex.Message);
        }
    }

    private static PluginValidationResult Fail(string path, KernelErrorCode code, string msg) =>
        new PluginValidationResult { AssemblyPath = path, IsValid = false, ErrorCode = code, Message = msg };
}
