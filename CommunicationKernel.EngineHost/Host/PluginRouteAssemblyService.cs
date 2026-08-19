using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using CommunicationKernel.Communication.Protocol.Abstractions;
using CommunicationKernel.Communication.Transport.Abstractions;
using CommunicationKernel.Core.Abstractions.Errors;
using CommunicationKernel.Core.Abstractions.Results;
using CommunicationKernel.Engine.Router.Abstractions;
using CommunicationKernel.Engine.Router.Models;
using CommunicationKernel.Plugin.Runtime.Loader;

namespace CommunicationKernel.EngineHost.Host;

/// <summary>
/// -----------------------------------------------------------------------------
/// 文件: PluginRouteAssemblyService.cs
/// 层级: EngineHost / Host
/// 作用: 基于插件工厂完成路由装配（传输连接 + 协议驱动创建 + RouteEntry组装）。
/// 说明:
/// 1) 装配职责集中在此服务，HostRuntime 仅做运行时策略编排。
/// 2) 插件工厂在启动期加载，运行中按命令选择匹配实现。
/// -----------------------------------------------------------------------------
/// </summary>
public sealed class PluginRouteAssemblyService : IRouteAssemblyService {
    private readonly IReadOnlyList<ITransportFactory> _transportFactories;
    private readonly IReadOnlyList<IProtocolDriverFactory> _protocolFactories;
    private readonly int _defaultSerialMinIoIntervalMs;

    public PluginRouteAssemblyService(string pluginDirectory, int defaultSerialMinIoIntervalMs = 15) {
        _defaultSerialMinIoIntervalMs = Math.Max(0, defaultSerialMinIoIntervalMs);

        (IReadOnlyList<ITransportFactory> transportFactories, IReadOnlyList<IProtocolDriverFactory> protocolFactories)
            = LoadFactories(pluginDirectory);

        _transportFactories = transportFactories;
        _protocolFactories = protocolFactories;
    }

    public async Task<OperationResult<RouteAssemblyResult>> AssembleAsync(
        HostRuntime.RegisterRouteCommand command,
        CancellationToken cancellationToken) {

        ArgumentNullException.ThrowIfNull(command);

        if (string.IsNullOrWhiteSpace(command.ProtocolId)) {
            return OperationResult<RouteAssemblyResult>.Fail("protocol_id is required", KernelErrorCode.InvalidArgument);
        }

        IProtocolDriverFactory? protocolFactory = _protocolFactories.FirstOrDefault(factory =>
            string.Equals(factory.Metadata.ProtocolId, command.ProtocolId, StringComparison.OrdinalIgnoreCase));

        if (protocolFactory is null) {
            return OperationResult<RouteAssemblyResult>.Fail($"protocol factory not found: {command.ProtocolId}", KernelErrorCode.ProtocolNotFound);
        }

        if (!Enum.TryParse(command.TransportKind, ignoreCase: true, out TransportKind transportKind)) {
            return OperationResult<RouteAssemblyResult>.Fail("transport_kind is invalid", KernelErrorCode.InvalidArgument);
        }

        ITransportFactory? transportFactory = _transportFactories.FirstOrDefault(factory =>
            (!string.IsNullOrWhiteSpace(command.TransportId)
                ? string.Equals(factory.TransportId, command.TransportId, StringComparison.OrdinalIgnoreCase)
                : factory.Kind == transportKind)
            && factory.Kind == transportKind);

        if (transportFactory is null) {
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
            await transportClient.DisposeAsync().ConfigureAwait(false);
            return OperationResult<RouteAssemblyResult>.Fail(connectResult.ErrorMessage, connectResult.ErrorCode);
        }

        IProtocolDriver protocolDriver = protocolFactory.CreateDriver();
        var routeEntry = new RouteEntry {
            Key = routeKey,
            TransportClient = transportClient,
            ProtocolDriver = protocolDriver
        };

        async Task RollbackAsync(CancellationToken ct) {
            await transportClient.DisconnectAsync(ct).ConfigureAwait(false);
            await transportClient.DisposeAsync().ConfigureAwait(false);
        }

        var result = new RouteAssemblyResult {
            RouteKey = routeKey,
            Endpoint = endpoint,
            TransportId = transportFactory.TransportId,
            IsSerialRoute = transportKind == TransportKind.Serial,
            MinIoIntervalMs = command.MinIoIntervalMs > 0 ? command.MinIoIntervalMs : _defaultSerialMinIoIntervalMs,
            RouteEntry = routeEntry,
            RollbackAsync = RollbackAsync
        };

        return OperationResult<RouteAssemblyResult>.Ok(result);
    }

    private static TransportEndpoint BuildEndpoint(TransportKind transportKind, HostRuntime.RegisterRouteCommand command) {
        return new TransportEndpoint {
            Kind = transportKind,
            Address = command.Address?.Trim() ?? string.Empty,
            Port = command.Port,
            SerialPort = string.IsNullOrWhiteSpace(command.SerialPort) ? null : command.SerialPort.Trim(),
            BaudRate = command.BaudRate > 0 ? command.BaudRate : null
        };
    }

    private static (IReadOnlyList<ITransportFactory>, IReadOnlyList<IProtocolDriverFactory>) LoadFactories(string pluginDirectory) {
        var catalog = new PluginCatalog();
        IReadOnlyList<PluginValidationResult> validations = catalog.DiscoverAndValidate(pluginDirectory);
        IReadOnlyList<PluginLoadResult> loads = catalog.LoadValidPlugins(validations);

        var transportFactories = new List<ITransportFactory>();
        var protocolFactories = new List<IProtocolDriverFactory>();

        foreach (PluginLoadResult loaded in loads) {
            foreach (Type type in loaded.Assembly.GetTypes()) {
                if (type.IsAbstract || type.IsInterface) {
                    continue;
                }

                if (typeof(ITransportFactory).IsAssignableFrom(type)
                    && Activator.CreateInstance(type) is ITransportFactory transportFactory) {
                    transportFactories.Add(transportFactory);
                    continue;
                }

                if (typeof(IProtocolDriverFactory).IsAssignableFrom(type)
                    && Activator.CreateInstance(type) is IProtocolDriverFactory protocolFactory) {
                    protocolFactories.Add(protocolFactory);
                }
            }
        }

        return (transportFactories, protocolFactories);
    }
}
