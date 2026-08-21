using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Reflection;
using System.Threading;
using System.Threading.Tasks;
using CommunicationKernel.Communication.Protocol.Abstractions;
using CommunicationKernel.Communication.Transport.Abstractions;
using CommunicationKernel.Core.Abstractions.Errors;
using CommunicationKernel.Core.Abstractions.Results;
using CommunicationKernel.Engine.Router.Abstractions;
using CommunicationKernel.Engine.Router.Models;
using CommunicationKernel.Plugin.Runtime.Loader;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace CommunicationKernel.EngineHost.Host;

/// <summary>
/// -----------------------------------------------------------------------------
/// 文件: PluginRouteAssemblyService.cs
/// 层级: EngineHost / Host
/// 作用: 基于插件工厂完成路由装配（传输连接 + 协议驱动创建 + RouteEntry 组装）。
/// 说明:
/// 1) 装配职责集中在此服务，HostRuntime 仅做运行时策略编排。
/// 2) 插件工厂在启动期加载，运行中按命令选择匹配实现。
/// 3) 若 TransportClient.ConnectAsync 失败，立即 Dispose 客户端；
///    组装成功后 RollbackAsync 供注册失败时回滚已建立的连接。
/// -----------------------------------------------------------------------------
/// </summary>
public sealed class PluginRouteAssemblyService : IRouteAssemblyService {
    //! 通讯插件工厂集合：在服务构造时加载，运行中按命令选择匹配实现。
    private readonly IReadOnlyList<ITransportFactory> _transportFactories;
    //! 协议插件工厂集合：在服务构造时加载，运行中按命令选择匹配实现。
    private readonly IReadOnlyList<IProtocolDriverFactory> _protocolFactories;
    //! 默认串口最小 IO 间隔（毫秒）：用于未指定 MinIoIntervalMs 的串口路由。
    private readonly int _defaultSerialMinIoIntervalMs;
    //! 日志记录器：用于记录插件加载、路由装配等关键事件。
    private readonly ILogger<PluginRouteAssemblyService> _logger;

    //! 构造函数：加载插件工厂并初始化服务。
    public PluginRouteAssemblyService (
        //! 插件目录：用于扫描和加载传输与协议插件。
        string pluginDirectory,
        //! 默认串口最小 IO 间隔（毫秒）：用于未指定 MinIoIntervalMs 的串口路由。
        int defaultSerialMinIoIntervalMs = 15,
        //! 可选日志工厂：用于创建日志记录器，若为 null 则使用 NullLogger。
        ILoggerFactory? loggerFactory = null) {

        //! 确保插件目录存在，否则抛出异常。
        _defaultSerialMinIoIntervalMs = Math.Max(0, defaultSerialMinIoIntervalMs);
        _logger = loggerFactory?.CreateLogger<PluginRouteAssemblyService>()
            ?? NullLogger<PluginRouteAssemblyService>.Instance;

        //! 加载插件工厂：发现、校验并实例化所有合法的传输与协议工厂。
        (IReadOnlyList<ITransportFactory> transportFactories, IReadOnlyList<IProtocolDriverFactory> protocolFactories)
            = LoadFactories(pluginDirectory, loggerFactory);

        //! 记录加载结果：输出已加载的传输与协议工厂数量。
        _transportFactories = transportFactories;
        _protocolFactories = protocolFactories;

        //! 日志记录：输出已加载的传输与协议工厂数量。
        _logger.LogInformation(
            "PluginRouteAssemblyService: loaded {TransportCount} transport factories, {ProtocolCount} protocol factories.",
            _transportFactories.Count, _protocolFactories.Count);
    }

    /// <summary>
    ///  获取可用协议清单：返回所有已加载的协议插件的元数据，按 DisplayName 排序。
    /// </summary>
    /// <returns></returns>
    public IReadOnlyList<ProtocolMetadata> GetAvailableProtocols () {
        // 数据源是插件工厂本身，与是否已注册路由无关：空载 Host 也返回完整清单。
        return _protocolFactories
            .Select(f => f.Metadata)
            .Where(m => !string.IsNullOrWhiteSpace(m.ProtocolId))
            .OrderBy(m => m.DisplayName, StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    /// <summary>
    /// 组装路由：根据命令参数选择匹配的传输与协议工厂，创建 TransportClient 与 ProtocolDriver，并返回 RouteAssemblyResult。
    /// </summary>
    /// <param name="command">注册路由命令，包含协议 ID、传输类型、地址、端口等信息。</param>
    /// <param name="cancellationToken">取消令牌，用于取消异步操作。</param>
    /// <returns>返回操作结果，包含 RouteAssemblyResult 或错误信息。</returns>
    public async Task<OperationResult<RouteAssemblyResult>> AssembleAsync (
        HostRuntime.RegisterRouteCommand command, CancellationToken cancellationToken) {
        //! 确保命令参数不为 null，否则抛出异常。
        ArgumentNullException.ThrowIfNull(command);

        //! 验证协议 ID 是否为空或空白，否则返回错误结果。
        if (string.IsNullOrWhiteSpace(command.ProtocolId))
            return OperationResult<RouteAssemblyResult>.Fail("protocol_id is required", KernelErrorCode.InvalidArgument);

        //! 验证传输类型是否为空或空白，否则返回错误结果。
        IProtocolDriverFactory? protocolFactory = _protocolFactories.FirstOrDefault(f =>
            string.Equals(f.Metadata.ProtocolId, command.ProtocolId, StringComparison.OrdinalIgnoreCase));

        //! 验证传输工厂是否存在，否则返回错误结果。
        if (protocolFactory is null) {
            _logger.LogError("AssembleRoute: protocol factory not found: '{ProtocolId}'.", command.ProtocolId);
            return OperationResult<RouteAssemblyResult>.Fail(
                $"protocol factory not found: {command.ProtocolId}", KernelErrorCode.ProtocolNotFound);
        }

        //! 验证传输类型是否有效，否则返回错误结果。
        if (!Enum.TryParse(command.TransportKind, ignoreCase: true, out TransportKind transportKind))
            return OperationResult<RouteAssemblyResult>.Fail("transport_kind is invalid", KernelErrorCode.InvalidArgument);

        //! 查找匹配的传输工厂：根据传输类型和可选的传输 ID 查找工厂。
        ITransportFactory? transportFactory = _transportFactories.FirstOrDefault(f =>
            (!string.IsNullOrWhiteSpace(command.TransportId)
                ? string.Equals(f.TransportId, command.TransportId, StringComparison.OrdinalIgnoreCase)
                : f.Kind == transportKind)
            && f.Kind == transportKind);

        //! 验证传输工厂是否存在，否则返回错误结果。
        if (transportFactory is null) {
            _logger.LogError("AssembleRoute: transport factory not found: kind={Kind}, id={Id}.", transportKind, command.TransportId);
            return OperationResult<RouteAssemblyResult>.Fail(
                $"transport factory not found: kind={transportKind}, transport_id={command.TransportId}",
                KernelErrorCode.TransportUnavailable);
        }

        //! 构建路由键：用于唯一标识路由实例，包括协议 ID、传输类型、地址、端口和站号。
        var routeKey = new RouteKey(
            command.ProtocolId.Trim(),
            transportKind,
            command.Address?.Trim() ?? string.Empty,
            command.Port,
            string.IsNullOrWhiteSpace(command.Station) ? null : command.Station.Trim());

        //! 构建传输端点：根据传输类型和命令参数创建 TransportEndpoint。
        TransportEndpoint endpoint = BuildEndpoint(transportKind, command);

        //! 创建传输客户端：使用传输工厂创建 TransportClient，并尝试连接到指定端点。
        ITransportClient transportClient = transportFactory.CreateClient();
        OperationResult connectResult = await transportClient.ConnectAsync(endpoint, cancellationToken).ConfigureAwait(false);
        if (!connectResult.Success) {
            _logger.LogError("AssembleRoute: ConnectAsync failed for {RouteKey}: {Error}.", routeKey, connectResult.ErrorMessage);
            await transportClient.DisposeAsync().ConfigureAwait(false);
            return OperationResult<RouteAssemblyResult>.Fail(connectResult.ErrorMessage, connectResult.ErrorCode);
        }

        //! 记录连接成功：输出路由键和连接状态。
        _logger.LogInformation("AssembleRoute: transport connected for {RouteKey}.", routeKey);

        // 将设备级站号作为该路由驱动实例的默认站号传入。
        // Host 层不解析站号语义，只原样透传；如何理解由协议插件自行决定。
        IProtocolDriver protocolDriver = protocolFactory.CreateDriver(
            new ProtocolDriverContext { Station = command.Station ?? string.Empty });
        var routeEntry = new RouteEntry {
            Key            = routeKey,
            TransportClient = transportClient,
            ProtocolDriver  = protocolDriver
        };

        //! 匿名内联函数 定义回滚操作：在注册失败时断开连接并释放资源。
        async Task RollbackAsync (CancellationToken ct) {
            await transportClient.DisconnectAsync(ct).ConfigureAwait(false);
            await transportClient.DisposeAsync().ConfigureAwait(false);
            _logger.LogWarning("AssembleRoute: rolled back connection for {RouteKey}.", routeKey);
        }

        //! 返回组装结果：包含路由键、端点、传输 ID、是否串口路由、最小 IO 间隔和回滚操作。
        return OperationResult<RouteAssemblyResult>.Ok(new RouteAssemblyResult {
            RouteKey = routeKey,
            Endpoint = endpoint,
            TransportId = transportFactory.TransportId,
            IsSerialRoute = transportKind == TransportKind.Serial,
            MinIoIntervalMs = command.MinIoIntervalMs > 0 ? command.MinIoIntervalMs : _defaultSerialMinIoIntervalMs,
            RouteEntry = routeEntry,
            RollbackAsync = RollbackAsync
        });
    }

    /// <summary>
    /// 构建传输端点：根据传输类型和注册命令参数创建 TransportEndpoint 实例。
    /// </summary>
    /// <param name="transportKind"></param>
    /// <param name="command"></param>
    /// <returns></returns>
    private static TransportEndpoint BuildEndpoint (TransportKind transportKind, HostRuntime.RegisterRouteCommand command) {
        var endpoint = new TransportEndpoint {
            Kind = transportKind,
            Address = command.Address?.Trim() ?? string.Empty,
            Port = command.Port,
            SerialPort = string.IsNullOrWhiteSpace(command.SerialPort) ? null : command.SerialPort.Trim(),
            BaudRate = command.BaudRate > 0 ? command.BaudRate : null
        };

        // 串口线路参数经 Properties 透传给传输插件。
        // Host 不解释其含义，只做搬运——具体取值由串口插件校验。
        // 缺省留空即为插件的默认 8N1。
        if (!string.IsNullOrWhiteSpace(command.Parity))
            endpoint.Properties["Parity"] = command.Parity.Trim();
        if (command.DataBits > 0)
            endpoint.Properties["DataBits"] = command.DataBits.ToString(CultureInfo.InvariantCulture);
        if (!string.IsNullOrWhiteSpace(command.StopBits))
            endpoint.Properties["StopBits"] = command.StopBits.Trim();

        return endpoint;
    }

    /// <summary>
    /// 加载插件工厂：扫描插件目录，发现、校验并实例化所有合法的传输与协议工厂。
    /// </summary>
    /// <param name="pluginDirectory"></param>
    /// <param name="loggerFactory"></param>
    /// <returns></returns>
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
