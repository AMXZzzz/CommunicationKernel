namespace CommunicationDebuggingTools.Core.Enums {

    /// <summary>
    /// 操作失败的语义分类。
    /// 调用方可按类型分支处理（如 DeviceNotConnected 触发重连，Timeout 重试）。
    /// </summary>
    public enum OperationErrorCode {
        /// <summary>无错误（Success = true 时使用）。</summary>
        None = 0,

        /// <summary>设备 Id 不存在。</summary>
        DeviceNotFound = 1,

        /// <summary>设备未连接或连接已断开。</summary>
        DeviceNotConnected = 2,

        /// <summary>变量 Id 不存在。</summary>
        VariableNotFound = 3,

        /// <summary>访问权限不符（只写变量读 / 只读变量写）。</summary>
        AccessDenied = 4,

        /// <summary>地址无效或协议无法解析。</summary>
        AddressInvalid = 5,

        /// <summary>协议层返回错误（PLC 拒绝、帧格式错误等）。</summary>
        ProtocolError = 6,

        /// <summary>I/O 超时。</summary>
        Timeout = 7,

        /// <summary>操作被取消。</summary>
        Cancelled = 8,

        /// <summary>未分类错误。</summary>
        Unknown = 99,
    }
}