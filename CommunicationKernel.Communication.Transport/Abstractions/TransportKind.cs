namespace CommunicationKernel.Communication.Transport.Abstractions;

/// <summary>
/// -----------------------------------------------------------------------------
/// 文件: TransportKind.cs
/// 层级: Communication.Transport / Abstractions
/// 作用: 定义传输介质分类。
/// 说明:
/// - 该枚举支撑“协议层与介质层分离”设计。
/// - 可在不修改上层流程的前提下扩展新的通讯介质。
/// -----------------------------------------------------------------------------
/// </summary>
public enum TransportKind {
    /// <summary>
    /// TCP 传输。
    /// </summary>
    Tcp = 0,

    /// <summary>
    /// 串口传输。
    /// </summary>
    Serial = 1,

    /// <summary>
    /// WiFi 传输。
    /// </summary>
    Wifi = 2,

    /// <summary>
    /// 蓝牙传输。
    /// </summary>
    Bluetooth = 3,

    /// <summary>
    /// 自定义扩展传输。
    /// </summary>
    Custom = 99
}
