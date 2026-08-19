namespace CommunicationDebuggingTools.Core.Enums {
    /// <summary>
    /// 设备所属产线的轨道类型（用于区分单轨/双轨产线的设备归属与展示）。
    /// </summary>
    public enum LaneType {
        /// <summary>单轨产线。</summary>
        Single = 0,

        /// <summary>双轨产线。</summary>
        Dual = 1
    }
}