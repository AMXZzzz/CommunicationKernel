#nullable disable

// -----------------------------------------------------------------------------
// 文件: Core/Enums/DeviceStatusType.cs
// 层级: UI 层 — WPF 核心枚举
// 作用: 定义设备连接状态，供 DeviceInfo 与状态指示灯绑定显示。
// -----------------------------------------------------------------------------

namespace CommunicationKernel.UI.Wpf.Core.Enums
{
    /// <summary>
    /// 设备/路由的连接状态枚举。
    /// 状态由 WatchRouteStatus 流实时更新，驱动 UI 上的状态指示灯和文字。
    /// </summary>
    public enum DeviceStatusType
    {
        /// <summary>离线：未建立连接，或连接已断开。</summary>
        Offline,

        /// <summary>成功：连接正常，通信无异常。</summary>
        Success,

        /// <summary>警告：连接存在但存在非致命错误（例如部分超时）。</summary>
        Warning,

        /// <summary>连接中：正在建立连接，等待握手结果。</summary>
        Connecting,

        /// <summary>错误：连接失败或通信出现致命错误。</summary>
        Error
    }
}
