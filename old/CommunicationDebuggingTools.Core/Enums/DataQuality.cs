namespace CommunicationDebuggingTools.Core.Enums {
    /// <summary>
    /// 读回数据质量。
    /// </summary>
    public enum DataQuality {
        /// <summary>有效。</summary>
        Good = 0,

        /// <summary>无效（通信失败、异常等）。</summary>
        Bad = 1,

        /// <summary>不确定（可选，暂未细分时也可不用）。</summary>
        Uncertain = 2
    }
}