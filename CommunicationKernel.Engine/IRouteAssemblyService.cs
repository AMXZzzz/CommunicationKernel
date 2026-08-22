using CommunicationKernel.Engine.Models;
using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using CommunicationKernel.Communication.Protocol.Abstractions;
using CommunicationKernel.Communication.Transport.Abstractions;
using CommunicationKernel.Core.Abstractions.Results;
using CommunicationKernel.Engine.Router.Abstractions;
using CommunicationKernel.Engine.Router.Models;

namespace CommunicationKernel.Engine;

/// <summary>
/// -----------------------------------------------------------------------------
/// 文件: IRouteAssemblyService.cs
/// 层级: EngineHost / Host
/// 作用: 抽象路由装配服务，屏蔽协议/传输工厂与插件发现细节。
/// 说明:
/// 1) HostRuntime 仅依赖该抽象，避免直接持有具体协议装配逻辑。
/// 2) 实现层可基于插件、配置中心或远程注册中心提供装配能力。
/// -----------------------------------------------------------------------------
/// </summary>
public interface IRouteAssemblyService {
    /// <summary>
    /// 按注册命令构建并连接路由运行时对象。
    /// </summary>
    Task<OperationResult<RouteAssemblyResult>> AssembleAsync(
        RegisterRouteCommand command,
        CancellationToken cancellationToken);

    /// <summary>
    /// 列出当前已加载的全部协议插件元信息。
    /// 用于向 UI 提供可选协议清单（含 ProtocolId、展示名、所需传输介质、是否需要站号），
    /// 使 UI 无需硬编码任何协议知识即可渲染设备表单。
    /// </summary>
    /// <remarks>
    /// 数据源是插件工厂本身，与「当前是否已注册路由」无关：
    /// 空载 Host 也必须返回完整的可用协议清单。
    /// </remarks>
    IReadOnlyList<ProtocolMetadata> GetAvailableProtocols();
}

/// <summary>
/// 路由装配结果模型。
/// </summary>
public sealed class RouteAssemblyResult {
    /// <summary>
    /// 解析后的路由键。
    /// </summary>
    public required RouteKey RouteKey { get; init; }

    /// <summary>
    /// 连接参数快照。
    /// </summary>
    public required TransportEndpoint Endpoint { get; init; }

    /// <summary>
    /// 绑定的工厂标识。
    /// </summary>
    public required string TransportId { get; init; }

    /// <summary>
    /// 是否串口路由。
    /// </summary>
    public bool IsSerialRoute { get; init; }

    /// <summary>
    /// 最小 I/O 间隔（毫秒）。
    /// </summary>
    public int MinIoIntervalMs { get; init; }

    /// <summary>
    /// 最终可注册的路由实体。
    /// </summary>
    public required RouteEntry RouteEntry { get; init; }

    /// <summary>
    /// 异常回滚（释放资源）回调。
    /// </summary>
    public required Func<CancellationToken, Task> RollbackAsync { get; init; }
}
