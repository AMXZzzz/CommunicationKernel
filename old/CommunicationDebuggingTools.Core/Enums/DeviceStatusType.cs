namespace CommunicationDebuggingTools.Core.Enums {
    /// <summary>
    /// 设备/连接状态类型。
    /// 该枚举同时承担两个职责：
    /// 1. 供业务层描述设备当前的运行状态；
    /// 2. 供 UI 层据此选择状态文字（如 RUN/ALARM）与配色（如绿色/红色指示灯）。
    /// </summary>
    public enum DeviceStatusType {
        /// <summary>未连接 / 离线，默认初始状态。</summary>
        Offline = 0,

        /// <summary>正在建立连接的过渡状态。</summary>
        Connecting = 1,

        /// <summary>已连接且运行正常（UI 显示为 RUN）。</summary>
        Success = 2,

        /// <summary>已连接但存在告警（如数据异常、参数越限等）。</summary>
        Warning = 3,

        /// <summary>发生错误（如通信超时、协议异常，UI 显示为 ALARM）。</summary>
        Error = 4
    }
}