using System;
using System.Collections.Generic;
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
    private readonly IReadOnlyList<ITransportFactory> _transportFactories;
    private readonly IReadOnlyList<IProtocolDriverFactory> _protocolFactories;
    private readonly int _defaultSerialMinIoIntervalMs;
    private readonly ILogger<PluginRouteAssemblyService> _logger;

    public PluginRouteAssemblyService(
        string pluginDirectory,
        int defaultSerialMinIoIntervalMs = 15,
        ILoggerFactory? loggerFactory = null) {

        _defaultSerialMinIoIntervalMs = Math.Max(0, defaultSerialMinIoIntervalMs);
        _logger = loggerFactory?.CreateLogger<PluginRouteAssemblyService>()
            ?? NullLogger<PluginRouteAssemblyService>.Instance;

        (IReadOnlyList<ITransportFactory> transportFactories, IReadOnlyList<IProtocolDriverFactory> protocolFactories)
            = LoadFactories(pluginDirectory, loggerFactory);

        _transportFactories = transportFactories;
        _protocolFactories  = protocolFactories;

        _logger.LogInformation(
            "PluginRouteAssemblyService: loaded {TransportCount} transport factories, {ProtocolCount} protocol factories.",
            _transportFactories.Count, _protocolFactories.Count);
    }

    /// <inheritdoc />
    public IReadOnlyList<ProtocolMetadata> GetAvailableProtocols() {
        // 数据源是插件工厂本身，与是否已注册路由无关：空载 Host 也返回完整清单。
        return _protocolFactories
            .Select(f => f.Metadata)
            .Where(m => !string.IsNullOrWhiteSpace(m.ProtocolId))
            .OrderBy(m => m.DisplayName, StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    public async Task<OperationResult<RouteAssemblyResult>> AssembleAsync(
        HostRuntime.RegisterRouteCommand command, CancellationToken cancellationToken) {

        ArgumentNullException.ThrowIfNull(command);

        if (string.IsNullOrWhiteSpace(command.ProtocolId))
            return OperationResult<RouteAssemblyResult>.Fail("protocol_id is required", KernelErrorCode.InvalidArgument);

        IProtocolDriverFactory? protocolFactory = _protocolFactories.FirstOrDefault(f =>
            string.Equals(f.Metadata.ProtocolId, command.ProtocolId, StringComparison.OrdinalIgnoreCase));

        if (protocolFactory is null) {
            _logger.LogError("AssembleRoute: protocol factory not found: '{ProtocolId}'.", command.ProtocolId);
            return OperationResult<RouteAssemblyResult>.Fail(
                $"protocol factory not found: {command.ProtocolId}", KernelErrorCode.ProtocolNotFound);
        }

        if (!Enum.TryParse(command.TransportKind, ignoreCase: true, out TransportKind transportKind))
            return OperationResult<RouteAssemblyResult>.Fail("transport_kind is invalid", KernelErrorCode.InvalidArgument);

        ITransportFactory? transportFactory = _transportFactories.FirstOrDefault(f =>
            (!string.IsNullOrWhiteSpace(command.TransportId)
                ? string.Equals(f.TransportId, command.TransportId, StringComparison.OrdinalIgnoreCase)
                : f.Kind == transportKind)
            && f.Kind == transportKind);

        if (transportFactory is null) {
            _logger.LogError("AssembleRoute: transport factory not found: kind={Kind}, id={Id}.", transportKind, command.TransportId);
            return OperationResult<RouteAssemblyResult>.Fail(
                $"transport factory not found: kind={transportKind}, transport_id={command.TransportId}",
                KernelErrorCode.TransportUnavailable);
        }

        var routeKey = new RouteKey(
            command.ProtocolId.Trim(),
            transportKind,
            command.Address?.Trim() ?? string.Empty,
            command.Port,
            string.IsNullOrWhiteSpace(command.Station) ? null : command.Station.Trim());

        TransportEndpoint endpoint = BuildEndpoint(transportKind, command);

        ITransportClient transportClient = transportFactory.CreateClient();
        OperationResult connectResult = await transportClient.ConnectAsync(endpoint, cancellationToken).ConfigureAwait(false);
        if (!connectResult.Success) {
            _logger.LogError("AssembleRoute: ConnectAsync failed for {RouteKey}: {Error}.", routeKey, connectResult.ErrorMessage);
            await transportClient.DisposeAsync().ConfigureAwait(false);
            return OperationResult<RouteAssemblyResult>.Fail(connectResult.ErrorMessage, connectResult.ErrorCode);
        }

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

        async Task RollbackAsync(CancellationToken ct) {
            await transportClient.DisconnectAsync(ct).ConfigureAwait(false);
            await transportClient.DisposeAsync().ConfigureAwait(false);
            _logger.LogWarning("AssembleRoute: rolled back connection for {RouteKey}.", routeKey);
        }

        return OperationResult<RouteAssemblyResult>.Ok(new RouteAssemblyResult {
            RouteKey        = routeKey,
            Endpoint        = endpoint,
            TransportId     = transportFactory.TransportId,
            IsSerialRoute   = transportKind == TransportKind.Serial,
            MinIoIntervalMs = command.MinIoIntervalMs > 0 ? command.MinIoIntervalMs : _defaultSerialMinIoIntervalMs,
            RouteEntry      = routeEntry,
            RollbackAsync   = RollbackAsync
        });
    }

    private static TransportEndpoint BuildEndpoint(TransportKind transportKind, HostRuntime.RegisterRouteCommand command) =>
        new TransportEndpoint {
            Kind       = transportKind,
            Address    = command.Address?.Trim() ?? string.Empty,
            Port       = command.Port,
            SerialPort = string.IsNullOrWhiteSpace(command.SerialPort) ? null : command.SerialPort.Trim(),
            BaudRate   = command.BaudRate > 0 ? command.BaudRate : null
        };

    private static (IReadOnlyList<ITransportFactory>, IReadOnlyList<IProtocolDriverFactory>) LoadFactories(
        string pluginDirectory, ILoggerFactory? loggerFactory) {

        var catalog     = new PluginCatalog(loggerFactory?.CreateLogger<PluginCatalog>());
        var validations = catalog.DiscoverAndValidate(pluginDirectory);
        var loads       = catalog.LoadValidPlugins(validations);

        var transportFactories = new List<ITransportFactory>();
        var protocolFactories  = new List<IProtocolDriverFactory>();

        ILogger logger = loggerFactory?.CreateLogger<PluginRouteAssemblyService>()
            ?? (ILogger)NullLogger<PluginRouteAssemblyService>.Instance;

        foreach (PluginLoadResult loaded in loads) {
            // 逐程序集隔离：单个插件的类型加载失败不应中断其余插件的发现。
            Type[] types;
            try {
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

            foreach (Type type in types) {
                if (type.IsAbstract || type.IsInterface) continue;

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

                if (isTransport && instance is ITransportFactory tf) {
                    transportFactories.Add(tf);
                    continue;
                }

                if (isProtocol && instance is IProtocolDriverFactory pf) {
                    protocolFactories.Add(pf);
                }
            }
        }

        return (transportFactories, protocolFactories);
    }
}
