// -----------------------------------------------------------------------------
// 文件: PluginRouteAssemblyService.cs
// 层级: Core.EngineRuntime
// 作用: 扫描插件目录，动态加载协议与传输工厂，再委托 RouteAssembler 装配路由。
// -----------------------------------------------------------------------------

using System.Reflection;
using CommunicationKernel.Core.Protocol.Abstractions;
using CommunicationKernel.Core.Transport.Abstractions;
using CommunicationKernel.Core.Abstractions.Results;
using CommunicationKernel.Core.EngineRuntime.Models;
using CommunicationKernel.Core.EngineRouter.Abstractions;
using CommunicationKernel.Plugin.Context;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace CommunicationKernel.Core.EngineRuntime;

/// <summary>运行时扫描插件目录，动态加载协议与传输工厂的装配服务。</summary>
/// <remarks>
/// 适合需要热插拔第三方协议、不重新编译即可扩展的宿主。
/// 若工厂在编译期即可确定（SDK 消费者、容器部署、单元测试），
/// 应改用 <see cref="StaticRouteAssemblyService"/>——它不触碰文件系统。
/// </remarks>
public sealed class PluginRouteAssemblyService : IRouteAssemblyService {

    private readonly RouteAssembler _assembler;
    private readonly IReadOnlyList<IProtocolDriverFactory> _protocolFactories;

    /// <summary>传输工厂集合；串口枚举需要在其中寻找 ISerialPortEnumerator 实现。</summary>
    private readonly IReadOnlyList<ITransportFactory> _transportFactories;

    /// <param name="pluginDirectory">插件目录，扫描其中的 *.dll。</param>
    /// <param name="defaultSerialMinIoIntervalMs">串口默认最小 I/O 间隔（毫秒）。</param>
    /// <param name="loggerFactory">可选日志工厂。</param>
    public PluginRouteAssemblyService(
        string pluginDirectory,
        int defaultSerialMinIoIntervalMs = 15,
        ILoggerFactory? loggerFactory = null) {

        // 未注入日志工厂时退化为空实现，避免插件加载失败时再因日志空引用崩溃
        ILogger logger = loggerFactory?.CreateLogger<PluginRouteAssemblyService>()
            ?? NullLogger<PluginRouteAssemblyService>.Instance;

        // 一次扫描 plugins 目录：发现、校验、实例化全部传输/协议工厂
        (IReadOnlyList<ITransportFactory> transports, IReadOnlyList<IProtocolDriverFactory> protocols)
            = LoadFactories(pluginDirectory, loggerFactory);

        _protocolFactories  = protocols;
        _transportFactories = transports;
        // 装配过程与 StaticRouteAssemblyService 共用 RouteAssembler，避免两套实现漂移
        _assembler = new RouteAssembler(transports, protocols, defaultSerialMinIoIntervalMs, logger);

        logger.LogInformation(
            "PluginRouteAssemblyService: 从 '{Directory}' 加载了 {TransportCount} 个传输工厂, {ProtocolCount} 个协议工厂。",
            pluginDirectory, transports.Count, protocols.Count);
    }

    /// <inheritdoc />
    public IReadOnlyList<ProtocolMetadata> GetAvailableProtocols()
        // 数据源是插件工厂本身，与是否已注册路由无关：空载宿主也返回完整清单。
        => _protocolFactories
            .Select(f => f.Metadata)
            .Where(m => !string.IsNullOrWhiteSpace(m.ProtocolId))
            .OrderBy(m => m.DisplayName, StringComparer.OrdinalIgnoreCase)
            .ToList();

    /// <inheritdoc />
    public IReadOnlyList<SerialPortInfo> GetAvailableSerialPorts()
        // 每次都重新枚举而非缓存：USB 转串口设备可以随时插拔，
        // 缓存会让操作员插上线后仍然看不到新串口。
        => SerialPortDiscovery.Enumerate(_transportFactories);

    /// <inheritdoc />
    public Task<OperationResult<RouteAssemblyResult>> AssembleAsync(
        RegisterRouteCommand command, CancellationToken cancellationToken)
        // 选工厂、建链、造驱动全部委托给共享装配器
        => _assembler.AssembleAsync(command, cancellationToken);

    // ============================================================================
    // 插件发现
    // ============================================================================

    /// <summary>
    /// 加载插件工厂：扫描插件目录，发现、校验并实例化所有合法的传输与协议工厂。
    /// </summary>
    /// <param name="pluginDirectory">插件目录绝对路径。</param>
    /// <param name="loggerFactory">可选日志工厂。</param>
    /// <returns>(传输工厂集合, 协议工厂集合)；目录不存在时两者均为空。</returns>
    private static (IReadOnlyList<ITransportFactory>, IReadOnlyList<IProtocolDriverFactory>) LoadFactories (
        string pluginDirectory, ILoggerFactory? loggerFactory) {

        // DiscoverAndLoad 一次加载即完成校验与实例化：
        // 旧的 DiscoverAndValidate + LoadValidPlugins 组合会把每个 DLL 加载两遍
        // （第一遍校验后 Unload，第二遍才真正使用），IO 开销翻倍，
        // 且校验结论与运行实例来自两次不同加载，留下验证绕过窗口。
        var catalog = new PluginCatalog(loggerFactory?.CreateLogger<PluginCatalog>());
        var loads   = catalog.DiscoverAndLoad(pluginDirectory);

        // 收集本轮成功实例化的工厂；单个插件失败不丢弃其余插件
        var transportFactories = new List<ITransportFactory>();
        var protocolFactories  = new List<IProtocolDriverFactory>();

        // 反射失败要记日志，但不得让单个坏 DLL 拖垮整个宿主启动
        ILogger logger = loggerFactory?.CreateLogger<PluginRouteAssemblyService>()
            ?? (ILogger)NullLogger<PluginRouteAssemblyService>.Instance;

        foreach (PluginLoadResult loaded in loads) {
            // 逐程序集隔离：单个插件的类型加载失败不应中断其余插件的发现。
            Type[] types;
            try {
                // 取出程序集全部类型；缺依赖时部分类型会抛 ReflectionTypeLoadException
                types = loaded.Assembly.GetTypes();
            } catch (ReflectionTypeLoadException ex) {
                // 部分类型可加载时 Types 中失败项为 null，取出可用的继续处理。
                types = ex.Types.Where(t => t is not null).ToArray()!;
                logger.LogError(ex,
                    "LoadFactories: assembly '{Assembly}' had type load failures; {Count} usable types recovered.",
                    loaded.Assembly.FullName, types.Length);
            } catch (Exception ex) {
                // 不可恢复的反射错误：跳过整个程序集，继续加载下一个插件
                logger.LogError(ex,
                    "LoadFactories: skipped assembly '{Assembly}' due to unrecoverable reflection error.",
                    loaded.Assembly.FullName);
                continue;
            }

            foreach (Type type in types) {
                // 抽象类和接口无法实例化，不是工厂入口
                if (type.IsAbstract || type.IsInterface) continue;

                // 只关心传输/协议工厂；其余类型（内部帮助类）一律跳过
                bool isTransport = typeof(ITransportFactory).IsAssignableFrom(type);
                bool isProtocol  = typeof(IProtocolDriverFactory).IsAssignableFrom(type);
                if (!isTransport && !isProtocol) continue;

                // 逐类型隔离：缺少无参构造函数、静态构造抛异常等均只跳过该工厂。
                object? instance;
                try {
                    instance = Activator.CreateInstance(type);
                } catch (Exception ex) {
                    logger.LogError(ex,
                        "LoadFactories: skipped factory type '{Type}' (instantiation failed).",
                        type.FullName);
                    continue;
                }

                // 传输工厂：TCP / 串口等介质入口，供后续按 Kind 选工厂
                if (isTransport && instance is ITransportFactory tf) {
                    transportFactories.Add(tf);
                    continue;
                }

                // 协议工厂：Modbus / S7 / Mewtocol 等驱动入口，供 UI 协议清单与装配使用
                if (isProtocol && instance is IProtocolDriverFactory pf) {
                    protocolFactories.Add(pf);
                }
            }
        }

        // 目录不存在或全部失败时返回空集合，由调用方在启动日志里告警
        return (transportFactories, protocolFactories);
    }
}
