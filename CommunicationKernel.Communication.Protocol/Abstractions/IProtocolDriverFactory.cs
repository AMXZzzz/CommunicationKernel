namespace CommunicationKernel.Communication.Protocol.Abstractions;

/// <summary>
/// -----------------------------------------------------------------------------
/// 文件: IProtocolDriverFactory.cs
/// 层级: Communication.Protocol / Abstractions
/// 作用: 约束协议插件工厂的统一入口。
/// 说明:
/// - 宿主通过工厂获取协议元信息并创建驱动实例。
/// - 工厂与驱动分离可支持不同生命周期策略（短生命周期/复用）。
/// -----------------------------------------------------------------------------
/// </summary>
public interface IProtocolDriverFactory {
    /// <summary>
    /// 协议元信息。
    /// </summary>
    ProtocolMetadata Metadata { get; }

    /// <summary>
    /// 创建协议驱动实例。
    /// </summary>
    IProtocolDriver CreateDriver();
}
