using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using CommunicationKernel.Core.Abstractions.Errors;
using CommunicationKernel.Core.Abstractions.Versioning;
using CommunicationKernel.Plugin.Runtime.Abstractions;

namespace CommunicationKernel.Plugin.Runtime.Loader;

/// <summary>
/// -----------------------------------------------------------------------------
/// 文件: PluginCatalog.cs
/// 层级: Plugin.Runtime / Loader
/// 作用: 提供插件“发现 -> 校验 -> 加载”流程的统一入口。
/// 设计原则:
/// 1) 发现与校验分离：先确认可用性，再进入正式加载。
/// 2) 失败隔离：单插件异常不影响其他插件处理。
/// 3) 版本前置：在加载到业务流程前完成 API 兼容性判断。
/// 4) 可诊断：通过统一结果对象保留错误码与可读消息。
/// -----------------------------------------------------------------------------
/// </summary>
public sealed class PluginCatalog {
    /// <summary>
    /// 扫描指定目录中的 DLL，并逐个执行校验。
    /// </summary>
    /// <param name="pluginDirectory">插件目录绝对或相对路径。</param>
    /// <returns>每个程序集对应一条校验结果。</returns>
    public IReadOnlyList<PluginValidationResult> DiscoverAndValidate(string pluginDirectory) {
        // 分支1：目录参数为空或目录不存在。
        // 含义：发现阶段无法进行，直接返回统一错误结果，便于上层快速诊断部署路径问题。
        if (string.IsNullOrWhiteSpace(pluginDirectory) || !Directory.Exists(pluginDirectory)) {
            return new[] {
                new PluginValidationResult {
                    AssemblyPath = pluginDirectory ?? string.Empty,
                    IsValid = false,
                    ErrorCode = KernelErrorCode.PluginNotFound,
                    Message = "Plugin directory not found"
                }
            };
        }

        // 正常路径：目录存在，按 DLL 文件逐个执行校验。
        // 含义：发现阶段只负责产出“每个程序集的可用性快照”，不在此处抛出中断异常。
        var results = new List<PluginValidationResult>();
        foreach (string dll in Directory.GetFiles(pluginDirectory, "*.dll", SearchOption.TopDirectoryOnly)) {
            // 单文件校验：每个 DLL 独立处理，保障失败隔离。
            results.Add(ValidateAssembly(dll));
        }

        return results;
    }

    /// <summary>
    /// 按校验结果加载所有可用插件。
    /// </summary>
    /// <param name="validations">发现阶段的校验结果集合。</param>
    /// <returns>成功加载的插件结果集合。</returns>
    public IReadOnlyList<PluginLoadResult> LoadValidPlugins(IEnumerable<PluginValidationResult> validations) {
        // 参数防御：调用方传空集合引用时立即抛出，避免静默掩盖流程错误。
        ArgumentNullException.ThrowIfNull(validations);

        var loaded = new List<PluginLoadResult>();
        foreach (PluginValidationResult validation in validations.Where(x => x.IsValid && x.Descriptor != null)) {
            try {
                // 分支1（成功路径）：为当前插件创建独立加载上下文并加载程序集。
                // 含义：每个插件有独立依赖边界，避免版本冲突。
                var loadContext = new PluginLoadContext(validation.AssemblyPath);
                Assembly assembly = loadContext.LoadFromAssemblyPath(validation.AssemblyPath);

                loaded.Add(new PluginLoadResult {
                    Descriptor = validation.Descriptor!,
                    Assembly = assembly,
                    LoadContext = loadContext
                });
            } catch {
                // 分支2（失败路径）：该插件加载失败。
                // 含义：失败隔离，不阻塞其他已校验通过插件的加载过程。
            }
        }

        return loaded;
    }

    /// <summary>
    /// 校验单个插件程序集是否满足运行时加载条件。
    /// </summary>
    /// <param name="assemblyPath">插件程序集路径。</param>
    /// <returns>校验结果。</returns>
    private static PluginValidationResult ValidateAssembly(string assemblyPath) {
        try {
            // 步骤1：在独立上下文中加载程序集。
            // 含义：保证校验阶段与宿主默认上下文隔离，降低依赖污染风险。
            var loadContext = new PluginLoadContext(assemblyPath);
            Assembly assembly = loadContext.LoadFromAssemblyPath(assemblyPath);

            // 步骤2：查找可实例化的 IPluginManifest 实现。
            // 分支意义：没有 Manifest 代表插件不符合最小约定。
            Type? manifestType = assembly
                .GetTypes()
                .FirstOrDefault(type => !type.IsAbstract && typeof(IPluginManifest).IsAssignableFrom(type));

            if (manifestType is null) {
                // 分支A：未找到 Manifest，立即卸载校验上下文并返回失败。
                loadContext.Unload();
                return new PluginValidationResult {
                    AssemblyPath = assemblyPath,
                    IsValid = false,
                    ErrorCode = KernelErrorCode.PluginLoadFailed,
                    Message = "No IPluginManifest implementation"
                };
            }

            // 步骤3：实例化 Manifest 并提取 Descriptor。
            // 分支意义：Descriptor 为空表示插件元数据不完整。
            var manifest = (IPluginManifest?)Activator.CreateInstance(manifestType);
            if (manifest?.Descriptor is null) {
                // 分支B：Descriptor 无效，卸载上下文并返回失败。
                loadContext.Unload();
                return new PluginValidationResult {
                    AssemblyPath = assemblyPath,
                    IsValid = false,
                    ErrorCode = KernelErrorCode.PluginLoadFailed,
                    Message = "Invalid plugin descriptor"
                };
            }

            // 步骤4：校验插件 API 版本是否与内核匹配。
            // 分支意义：版本不匹配时禁止加载，避免运行期协议/接口错位。
            PluginDescriptor descriptor = manifest.Descriptor;
            if (descriptor.ApiVersion != KernelVersions.PluginApiVersion) {
                // 分支C：版本不匹配，卸载校验上下文并返回兼容性错误。
                loadContext.Unload();
                return new PluginValidationResult {
                    AssemblyPath = assemblyPath,
                    IsValid = false,
                    ErrorCode = KernelErrorCode.PluginApiVersionMismatch,
                    Message = $"Plugin API version mismatch: {descriptor.ApiVersion}",
                    Descriptor = descriptor
                };
            }

            // 分支D：校验通过，卸载临时上下文并返回成功结果。
            // 说明：这里只做校验；正式加载会在 LoadValidPlugins 再次创建上下文。
            loadContext.Unload();
            return new PluginValidationResult {
                AssemblyPath = assemblyPath,
                IsValid = true,
                ErrorCode = KernelErrorCode.None,
                Message = "OK",
                Descriptor = descriptor
            };
        } catch (ReflectionTypeLoadException reflectionTypeLoadException) {
            // 分支E：类型加载异常，多见于插件依赖缺失或版本冲突。
            return new PluginValidationResult {
                AssemblyPath = assemblyPath,
                IsValid = false,
                ErrorCode = KernelErrorCode.PluginIsolationError,
                Message = reflectionTypeLoadException.LoaderExceptions.FirstOrDefault()?.Message
                    ?? reflectionTypeLoadException.Message
            };
        } catch (Exception exception) {
            // 分支F：其他异常统一映射为加载失败，保证错误语义稳定。
            return new PluginValidationResult {
                AssemblyPath = assemblyPath,
                IsValid = false,
                ErrorCode = KernelErrorCode.PluginLoadFailed,
                Message = exception.Message
            };
        }
    }
}
