// -----------------------------------------------------------------------------
// 文件: TransportKind.cs
// 层级: Communication.Transport / Abstractions
// 作用: 定义传输介质分类。
// 说明:
//   - 该枚举支撑“协议层与介质层分离”设计。
//   - 可在不修改上层流程的前提下扩展新的通讯介质。
// -----------------------------------------------------------------------------

namespace CommunicationKernel.Communication.Transport.Abstractions;

/// <summary>
/// 传输介质分类。协议层与介质层分离：同一协议可跑在多种介质上。
/// </summary>
public enum TransportKind {
    /// <summary>
    /// TCP 传输（以太网直连 PLC，或经 TCP 转串口网关透传）。
    /// </summary>
    Tcp = 0,

    /// <summary>
    /// 串口传输（RS-232 / RS-485，含 USB 转串口）。
    /// </summary>
    Serial = 1,

    /// <summary>
    /// WiFi 传输（无线以太网，连接参数与 TCP 相同）。
    /// </summary>
    Wifi = 2,

    /// <summary>
    /// 蓝牙传输。
    /// </summary>
    Bluetooth = 3,

    /// <summary>
    /// 自定义扩展传输（厂商私有介质，由对应传输插件解释）。
    /// </summary>
    Custom = 99
}
