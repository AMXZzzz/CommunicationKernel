// -----------------------------------------------------------------------------
// 文件: ProtocolDriverContext.cs
// 层级: Core.Protocol / Abstractions
// 作用: 承载「每路由一份」的协议驱动创建参数。
// 说明:
//   1) 每条路由在装配时创建独立驱动实例，本上下文即该实例的不可变配置快照。
//   2) Station 来自设备级配置（RegisterRoute.station），是该路由的默认站号。
//      地址字符串中的 "站号:" 前缀仍可覆盖它，用于 RS-485 一主多从场景：
//      同一串口路由下不同变量指向不同从站。
//   3) 本类型不承载任何协议语义解析，仅传递原始配置值；
//      如何理解 Station（Modbus Unit ID / MEWTOCOL 站号）由各协议插件自行决定。
// -----------------------------------------------------------------------------

namespace CommunicationKernel.Core.Protocol.Abstractions;

/// <summary>
/// 每路由一份的协议驱动创建参数（不可变配置快照）。
/// </summary>
public sealed class ProtocolDriverContext {
    /// <summary>
    /// 设备级站号原文（未解析）。空字符串表示未配置，驱动应使用自身默认值。
    /// </summary>
    public string Station { get; init; } = string.Empty;
}
