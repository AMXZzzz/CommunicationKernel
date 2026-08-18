namespace CommunicationKernel.Communication.Transport.Abstractions;

/// <summary>
/// -----------------------------------------------------------------------------
/// 文件: ITransportFactory.cs
/// 层级: Communication.Transport / Abstractions
/// 作用: 约束传输插件工厂，以统一方式创建传输客户端。
/// 说明:
/// - 工厂本身是插件暴露给宿主的入口。
/// - 宿主可按 Kind/TransportId 选择对应工厂并创建客户端实例。
/// -----------------------------------------------------------------------------
/// </summary>
public interface ITransportFactory {
    /// <summary>
    /// 工厂逻辑标识。
    /// </summary>
    string TransportId { get; }

    /// <summary>
    /// 工厂所生产客户端的介质类型。
    /// </summary>
    TransportKind Kind { get; }

    /// <summary>
    /// 插件声明的 API 版本。
    /// </summary>
    int PluginApiVersion { get; }

    /// <summary>
    /// 创建一个新的传输客户端实例。
    /// </summary>
    ITransportClient CreateClient();
}
