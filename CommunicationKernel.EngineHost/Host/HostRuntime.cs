using System;
using CommunicationKernel.Engine.Router;
using CommunicationKernel.Engine.Router.Abstractions;

namespace CommunicationKernel.EngineHost.Host;

/// <summary>
/// -----------------------------------------------------------------------------
/// 文件: HostRuntime.cs
/// 层级: EngineHost / Host
/// 作用: Host 运行时最小聚合入口，持有统一门面实例。
/// 说明:
/// - 当前实现保持轻量，聚焦“构造并暴露 Facade”。
/// - 后续落地高性能 gRPC 服务时，可在此处承接生命周期（启动/停止/健康状态）。
/// -----------------------------------------------------------------------------
/// </summary>
public sealed class HostRuntime {
    /// <summary>
    /// 创建 Host 运行时。
    /// </summary>
    /// <param name="orchestrator">
    /// 可选外部编排器；未提供时在此组合根处创建默认 RouterOrchestrator。
    /// </param>
    public HostRuntime(IRouterOrchestrator? orchestrator = null) {
        // 组合根职责：具体实现创建只允许发生在最外层装配点。
        IRouterOrchestrator resolvedOrchestrator = orchestrator ?? new RouterOrchestrator();
        Facade = new EngineHostFacade(resolvedOrchestrator);
    }

    /// <summary>
    /// Host 对外统一入口门面。
    /// </summary>
    public EngineHostFacade Facade { get; }
}
