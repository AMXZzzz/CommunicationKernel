// -----------------------------------------------------------------------------
// 文件: PluginKind.cs
// 层级: Plugin.Loader / Abstractions
// 作用: 声明插件能力分类。
// 说明:
// - 分类用于路由到不同加载与组装流程。
// - Transport 与 Protocol 解耦，支持“同协议+多介质”的组合扩展。
// -----------------------------------------------------------------------------

namespace CommunicationKernel.Plugin.Loader.Abstractions;

/// <summary>
/// -----------------------------------------------------------------------------
/// 文件: PluginKind.cs
/// 层级: Plugin.Loader / Abstractions
/// 作用: 声明插件能力分类。
/// 说明:
/// - 分类用于路由到不同加载与组装流程。
/// - Transport 与 Protocol 解耦，支持“同协议+多介质”的组合扩展。
/// -----------------------------------------------------------------------------
/// </summary>
public enum PluginKind {
    /// <summary>
    /// 传输介质插件（如 TCP、串口、蓝牙等）。
    /// </summary>
    Transport = 0,

    /// <summary>
    /// 协议插件（如 Modbus、S7、Mewtocol 等）。
    /// </summary>
    Protocol = 1
}
