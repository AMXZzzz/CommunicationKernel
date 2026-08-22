// -----------------------------------------------------------------------------
// 文件: RouteAssembler.cs
// 层级: Engine
// -----------------------------------------------------------------------------

using System.Globalization;
using CommunicationKernel.Communication.Protocol.Abstractions;
using CommunicationKernel.Communication.Transport.Abstractions;
using CommunicationKernel.Core.Abstractions.Errors;
using CommunicationKernel.Core.Abstractions.Results;
using CommunicationKernel.Engine.Runtime.Models;
using CommunicationKernel.Engine.Router.Abstractions;
using CommunicationKernel.Engine.Router.Models;
using Microsoft.Extensions.Logging;

namespace CommunicationKernel.Engine.Runtime;

/// <summary>
/// 从工厂集合装配一条路由：选工厂 → 建连接 → 造驱动 → 组装 RouteEntry。
/// </summary>
/// <remarks>
/// 工厂<b>从哪来</b>是两种装配服务的唯一差异：
/// <see cref="PluginRouteAssemblyService"/> 扫描目录动态加载，
/// <see cref="StaticRouteAssemblyService"/> 由调用方编译期提供。
/// 装配过程本身完全相同，因此收敛到本类，避免两份实现随时间漂移。
/// </remarks>
internal sealed class RouteAssembler {

    private readonly IReadOnlyList<ITransportFactory> _transportFactories;
    private readonly IReadOnlyList<IProtocolDriverFactory> _protocolFactories;
    private readonly int _defaultSerialMinIoIntervalMs;
    private readonly ILogger _logger;

    internal RouteAssembler(
        IReadOnlyList<ITransportFactory> transportFactories,
        IReadOnlyList<IProtocolDriverFactory> protocolFactories,
        int defaultSerialMinIoIntervalMs,
        ILogger logger) {

        _transportFactories           = transportFactories;
        _protocolFactories            = protocolFactories;
        _defaultSerialMinIoIntervalMs = Math.Max(0, defaultSerialMinIoIntervalMs);
        _logger                       = logger;
    }

    /// <summary>装配一条路由，失败时保证已建立的连接被释放。</summary>
    internal async Task<OperationResult<RouteAssemblyResult>> AssembleAsync(
        RegisterRouteCommand command, CancellationToken cancellationToken) {

        ArgumentNullException.ThrowIfNull(command);

        if (string.IsNullOrWhiteSpace(command.ProtocolId))
            return Fail("必须指定 protocol_id", KernelErrorCode.InvalidArgument);

        // ── 选协议工厂 ──
        IProtocolDriverFactory? protocolFactory = _protocolFactories.FirstOrDefault(f =>
            string.Equals(f.Metadata.ProtocolId, command.ProtocolId, StringComparison.OrdinalIgnoreCase));

        if (protocolFactory is null) {
            _logger.LogError("装配路由失败：找不到协议插件 '{ProtocolId}'。", command.ProtocolId);
            return Fail($"找不到协议插件：{command.ProtocolId}", KernelErrorCode.ProtocolNotFound);
        }

        // ── 解析传输介质 ──
        if (!Enum.TryParse(command.TransportKind, ignoreCase: true, out TransportKind transportKind))
            return Fail($"无效的传输介质：'{command.TransportKind}'（应为 Tcp 或 Serial）",
                KernelErrorCode.InvalidArgument);

        // 协议须声明支持该介质。缺少此校验时，把 Modbus TCP 配到串口上
        // 只会在建链后的首次读写才暴露，且错误信息与真实原因无关。
        if (protocolFactory.Metadata.SupportedTransports is { Count: > 0 } supported
            && !supported.Contains(transportKind)) {

            string list = string.Join(" / ", supported);
            return Fail(
                $"协议 {command.ProtocolId} 不支持 {transportKind} 介质，仅支持：{list}",
                KernelErrorCode.InvalidArgument);
        }

        // ── 选传输工厂 ──
        ITransportFactory? transportFactory = _transportFactories.FirstOrDefault(f =>
            f.Kind == transportKind
            && (string.IsNullOrWhiteSpace(command.TransportId)
                || string.Equals(f.TransportId, command.TransportId, StringComparison.OrdinalIgnoreCase)));

        if (transportFactory is null) {
            _logger.LogError("装配路由失败：找不到传输插件 kind={Kind}, id={Id}。", transportKind, command.TransportId);
            return Fail(
                $"找不到传输插件：介质={transportKind}，transport_id={command.TransportId}",
                KernelErrorCode.TransportUnavailable);
        }

        var routeKey = new RouteKey(
            command.ProtocolId.Trim(),
            transportKind,
            command.Address?.Trim() ?? string.Empty,
            command.Port,
            string.IsNullOrWhiteSpace(command.Station) ? null : command.Station.Trim());

        TransportEndpoint endpoint = BuildEndpoint(transportKind, command);

        // ── 建立连接 ──
        ITransportClient transportClient = transportFactory.CreateClient();
        OperationResult connect = await transportClient.ConnectAsync(endpoint, cancellationToken)
            .ConfigureAwait(false);

        if (!connect.Success) {
            _logger.LogError("装配路由失败：连接 {RouteKey} 失败：{Error}。", routeKey, connect.ErrorMessage);
            await transportClient.DisposeAsync().ConfigureAwait(false);
            return Fail(connect.ErrorMessage, connect.ErrorCode);
        }

        _logger.LogInformation("装配路由：{RouteKey} 连接已建立。", routeKey);

        // 设备级站号作为该路由驱动实例的默认站号传入。
        // 引擎不解析站号语义，只原样透传；如何理解由协议插件自行决定。
        IProtocolDriver protocolDriver = protocolFactory.CreateDriver(
            new ProtocolDriverContext { Station = command.Station ?? string.Empty });

        int minIoInterval = command.MinIoIntervalMs > 0
            ? command.MinIoIntervalMs
            : (transportKind == TransportKind.Serial ? _defaultSerialMinIoIntervalMs : 0);

        var routeEntry = new RouteEntry {
            Key             = routeKey,
            TransportClient = transportClient,
            ProtocolDriver  = protocolDriver,
            MinIoIntervalMs = minIoInterval
        };

        async Task RollbackAsync(CancellationToken ct) {
            await transportClient.DisconnectAsync(ct).ConfigureAwait(false);
            await transportClient.DisposeAsync().ConfigureAwait(false);
            _logger.LogWarning("装配路由：已回滚 {RouteKey} 的连接。", routeKey);
        }

        return OperationResult<RouteAssemblyResult>.Ok(new RouteAssemblyResult {
            RouteKey        = routeKey,
            Endpoint        = endpoint,
            TransportId     = transportFactory.TransportId,
            IsSerialRoute   = transportKind == TransportKind.Serial,
            MinIoIntervalMs = minIoInterval,
            RouteEntry      = routeEntry,
            RollbackAsync   = RollbackAsync
        });
    }

    /// <summary>由注册命令构建传输端点。</summary>
    private static TransportEndpoint BuildEndpoint(TransportKind transportKind, RegisterRouteCommand command) {
        var endpoint = new TransportEndpoint {
            Kind       = transportKind,
            Address    = command.Address?.Trim() ?? string.Empty,
            Port       = command.Port,
            SerialPort = string.IsNullOrWhiteSpace(command.SerialPort) ? null : command.SerialPort.Trim(),
            BaudRate   = command.BaudRate > 0 ? command.BaudRate : null
        };

        // 串口线路参数经 Properties 透传给传输插件。
        // 引擎不解释其含义，只做搬运——具体取值由串口插件校验。
        // 缺省留空即为插件的默认 8N1。
        if (!string.IsNullOrWhiteSpace(command.Parity))
            endpoint.Properties["Parity"] = command.Parity.Trim();
        if (command.DataBits > 0)
            endpoint.Properties["DataBits"] = command.DataBits.ToString(CultureInfo.InvariantCulture);
        if (!string.IsNullOrWhiteSpace(command.StopBits))
            endpoint.Properties["StopBits"] = command.StopBits.Trim();

        return endpoint;
    }

    private static OperationResult<RouteAssemblyResult> Fail(string message, KernelErrorCode code)
        => OperationResult<RouteAssemblyResult>.Fail(message, code);
}
