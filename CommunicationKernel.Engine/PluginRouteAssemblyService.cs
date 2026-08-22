// -----------------------------------------------------------------------------
// 文件: PluginRouteAssemblyService.cs
// 层级: Engine
// -----------------------------------------------------------------------------

using System.Reflection;
using CommunicationKernel.Communication.Protocol.Abstractions;
using CommunicationKernel.Communication.Transport.Abstractions;
using CommunicationKernel.Core.Abstractions.Results;
using CommunicationKernel.Engine.Models;
using CommunicationKernel.Engine.Router.Abstractions;
using CommunicationKernel.Plugin.Runtime.Loader;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace CommunicationKernel.Engine;

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

        ILogger logger = loggerFactory?.CreateLogger<PluginRouteAssemblyService>()
            ?? NullLogger<PluginRouteAssemblyService>.Instance;

        (IReadOnlyList<ITransportFactory> transports, IReadOnlyList<IProtocolDriverFactory> protocols)
            = LoadFactories(pluginDirectory, loggerFactory);

        _protocolFactories  = protocols;
        _transportFactories = transports;
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
    public IReadOnlyList<SerialPortDescriptor> GetAvailableSerialPorts()
        // 每次都重新枚举而非缓存：USB 转串口设备可以随时插拔，
        // 缓存会让操作员插上线后仍然看不到新串口。
        => SerialPortDiscovery.Enumerate(_transportFactories);

    /// <inheritdoc />
    public Task<OperationResult<RouteAssemblyResult>> AssembleAsync(
        RegisterRouteCommand command, CancellationToken cancellationToken)
        => _assembler.AssembleAsync(command, cancellationToken);

    /// <summary>
    /// 加载插件工厂：扫描插件目录，发现、校验并实例化所有合法的传输与协议工厂。
    /// </summary>
    /// <param name="pluginDirectory">插件目录绝对路径。</param>
    /// <param name="loggerFactory">可选日志工厂。</param>
    /// <returns>(传输工厂集合, 协议工厂集合)；目录不存在时两者均为空。</returns>
    private static (IReadOnlyList<ITransportFactory>, IReadOnlyList<IProtocolDriverFactory>) LoadFactories (
        string pluginDirectory, ILoggerFactory? loggerFactory) {

        //! 插件目录不存在时记录错误并返回空集合。
        // DiscoverAndLoad 一次加载即完成校验与实例化：
        // 旧的 DiscoverAndValidate + LoadValidPlugins 组合会把每个 DLL 加载两遍
        // （第一遍校验后 Unload，第二遍才真正使用），IO 开销翻倍，
        // 且校验结论与运行实例来自两次不同加载，留下验证绕过窗口。
        var catalog = new PluginCatalog(loggerFactory?.CreateLogger<PluginCatalog>());
        var loads   = catalog.DiscoverAndLoad(pluginDirectory);

        //! 初始化传输与协议工厂集合：用于存储已加载的插件实例。
        var transportFactories = new List<ITransportFactory>();
        var protocolFactories  = new List<IProtocolDriverFactory>();

        //! 创建日志记录器：用于记录插件加载过程中的错误和信息。
        ILogger logger = loggerFactory?.CreateLogger<PluginRouteAssemblyService>()
            ?? (ILogger)NullLogger<PluginRouteAssemblyService>.Instance;

        //! 遍历已加载的插件程序集，尝试获取类型并实例化工厂。
        foreach (PluginLoadResult loaded in loads) {
            // 逐程序集隔离：单个插件的类型加载失败不应中断其余插件的发现。
            Type[] types;
            try {
                //! 获取程序集中的所有类型：若部分类型加载失败，则捕获 ReflectionTypeLoadException 并继续处理可用类型。
                types = loaded.Assembly.GetTypes();
            } catch (ReflectionTypeLoadException ex) {
                // 部分类型可加载时 Types 中失败项为 null，取出可用的继续处理。
                types = ex.Types.Where(t => t is not null).ToArray()!;
                logger.LogError(ex,
                    "LoadFactories: assembly '{Assembly}' had type load failures; {Count} usable types recovered.",
                    loaded.Assembly.FullName, types.Length);
            } catch (Exception ex) {
                logger.LogError(ex,
                    "LoadFactories: skipped assembly '{Assembly}' due to unrecoverable reflection error.",
                    loaded.Assembly.FullName);
                continue;
            }

            //! 遍历程序集中的类型，尝试实例化传输与协议工厂。
            foreach (Type type in types) {
                //! 逐类型隔离：跳过抽象类和接口，避免尝试实例化无法实例化的类型。
                if (type.IsAbstract || type.IsInterface) continue;

                //! 逐类型隔离：检查类型是否实现 ITransportFactory 或 IProtocolDriverFactory 接口。
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

                //! 根据类型实现接口的情况，将实例添加到相应的工厂集合中。
                if (isTransport && instance is ITransportFactory tf) {
                    transportFactories.Add(tf);
                    continue;
                }

                //! 根据类型实现接口的情况，将实例添加到相应的工厂集合中。
                if (isProtocol && instance is IProtocolDriverFactory pf) {
                    protocolFactories.Add(pf);
                }
            }
        }

        //! 返回已加载的传输与协议工厂集合。
        return (transportFactories, protocolFactories);
    }
}
