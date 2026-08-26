// -----------------------------------------------------------------------------
// 文件: StaticRouteAssemblyService.cs
// 层级: Core.EngineRuntime
// 作用: 由调用方在编译期直接提供工厂实例的装配服务，不扫描文件系统。
// -----------------------------------------------------------------------------

using CommunicationKernel.Core.Protocol.Abstractions;
using CommunicationKernel.Core.Transport.Abstractions;
using CommunicationKernel.Core.Abstractions.Errors;
using CommunicationKernel.Core.Abstractions.Results;
using CommunicationKernel.Core.EngineRuntime.Models;
using CommunicationKernel.Core.EngineRouter.Abstractions;
using CommunicationKernel.Core.EngineRouter.Models;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace CommunicationKernel.Core.EngineRuntime;

/// <summary>
/// 由调用方在编译期直接提供工厂实例的装配服务。
/// </summary>
/// <remarks>
/// <para>
/// 与 <see cref="PluginRouteAssemblyService"/> <b>共存而非取代</b>：
/// </para>
/// <list type="bullet">
///   <item>
///     <b>本类</b>——工厂在编译期确定。适合 SDK 消费者、容器化部署、
///     单元测试，以及树莓派这类希望单文件发布、不带插件目录的场景。
///     不触碰文件系统，因而也不受目录权限、工作目录漂移的影响。
///   </item>
///   <item>
///     <b>PluginRouteAssemblyService</b>——运行时扫描目录动态加载。
///     适合需要热插拔第三方协议、不重新编译即可扩展的宿主。
///   </item>
/// </list>
/// <para>
/// 典型用法：
/// </para>
/// <code>
/// var assembly = new StaticRouteAssemblyService(
///     transportFactories: new ITransportFactory[] { new TcpTransportFactory() },
///     protocolFactories:  new IProtocolDriverFactory[] { new ModbusTcpProtocolDriverFactory() });
///
/// var engine = new EngineRuntime(assembly, orchestrator);
/// await engine.RegisterRouteAsync(new RegisterRouteCommand { ... }, ct);
/// </code>
/// </remarks>
public sealed class StaticRouteAssemblyService : IRouteAssemblyService {

    private readonly RouteAssembler _assembler;
    private readonly IReadOnlyList<IProtocolDriverFactory> _protocolFactories;

    /// <summary>传输工厂集合；串口枚举需要在其中寻找 ISerialPortEnumerator 实现。</summary>
    private readonly IReadOnlyList<ITransportFactory> _transportFactories;

    /// <summary>
    /// 用显式提供的工厂集合构造装配服务。
    /// </summary>
    /// <param name="transportFactories">传输工厂集合，至少一个。</param>
    /// <param name="protocolFactories">协议工厂集合，至少一个。</param>
    /// <param name="defaultSerialMinIoIntervalMs">串口默认最小 I/O 间隔（毫秒）。</param>
    /// <param name="logger">可选日志记录器。</param>
    public StaticRouteAssemblyService(
        IEnumerable<ITransportFactory> transportFactories,
        IEnumerable<IProtocolDriverFactory> protocolFactories,
        int defaultSerialMinIoIntervalMs = 15,
        ILogger<StaticRouteAssemblyService>? logger = null) {

        // 工厂集合为必填：没有传输工厂无法建链，没有协议工厂无法造驱动
        ArgumentNullException.ThrowIfNull(transportFactories);
        ArgumentNullException.ThrowIfNull(protocolFactories);

        // 物化为列表：后续装配/枚举会多次遍历，且需立即校验非空
        IReadOnlyList<ITransportFactory> transports = transportFactories.ToList();
        _transportFactories = transports;
        _protocolFactories = protocolFactories.ToList();

        // 空集合在首次 RegisterRoute 才暴露为「找不到插件」，构造期拒绝更早、更清楚
        if (transports.Count == 0)
            throw new ArgumentException("至少需要提供一个传输工厂", nameof(transportFactories));
        if (_protocolFactories.Count == 0)
            throw new ArgumentException("至少需要提供一个协议工厂", nameof(protocolFactories));

        ILogger log = logger ?? NullLogger<StaticRouteAssemblyService>.Instance;
        // 装配过程与插件扫描路径共用 RouteAssembler，避免两套实现漂移
        _assembler = new RouteAssembler(transports, _protocolFactories, defaultSerialMinIoIntervalMs, log);

        log.LogInformation(
            "StaticRouteAssemblyService: {TransportCount} 个传输工厂, {ProtocolCount} 个协议工厂（编译期提供，不扫描文件系统）。",
            transports.Count, _protocolFactories.Count);
    }

    /// <inheritdoc />
    public IReadOnlyList<ProtocolMetadata> GetAvailableProtocols()
        // 数据源是调用方注入的工厂，与是否已注册路由无关
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
}
