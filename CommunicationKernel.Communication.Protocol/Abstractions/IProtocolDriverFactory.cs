// -----------------------------------------------------------------------------
// 文件: IProtocolDriverFactory.cs
// 层级: Communication.Protocol / Abstractions
// 作用: 约束协议插件工厂的统一入口。
// 说明:
//   - 宿主通过工厂获取协议元信息并创建驱动实例。
//   - 工厂与驱动分离可支持不同生命周期策略（短生命周期/复用）。
// -----------------------------------------------------------------------------

namespace CommunicationKernel.Communication.Protocol.Abstractions;

/// <summary>
/// 协议插件工厂：宿主用它读取元信息，并按路由创建独立驱动实例。
/// </summary>
public interface IProtocolDriverFactory {
    /// <summary>
    /// 协议元信息。
    /// </summary>
    ProtocolMetadata Metadata { get; }

    /// <summary>
    /// 创建协议驱动实例（每路由一份）。
    /// </summary>
    /// <param name="context">
    /// 该路由的驱动配置快照（含设备级站号）。
    /// 传 null 时驱动使用自身内置默认值，便于单元测试直接构造无状态驱动。
    /// </param>
    IProtocolDriver CreateDriver(ProtocolDriverContext? context = null);
}
