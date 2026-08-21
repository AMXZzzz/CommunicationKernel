using CommunicationDebuggingTools.Core.Enums;

namespace CommunicationDebuggingTools.Core.Models {

    /// <summary>
    /// 统一操作结果：替代 <c>Task&lt;bool&gt;</c> + 旁路写 <c>LastError</c> 的双重模式。
    ///
    /// 设计约束：
    ///   - 不可变值对象（构造后字段不变），线程安全。
    ///   - 通过工厂方法构造，禁止直接 new。
    ///   - Success = true 时 ErrorMessage / ErrorCode 均为默认值，调用方无需检查。
    ///   - Success = false 时 ErrorMessage 必须非空，ErrorCode 必须不为 None。
    /// </summary>
    public sealed class OperationResult {

        public bool Success { get; }
        public string ErrorMessage { get; }
        public OperationErrorCode ErrorCode { get; }

        private OperationResult (
            bool success,
            string message,
            OperationErrorCode code) {
            Success = success;
            ErrorMessage = message ?? string.Empty;
            ErrorCode = code;
        }

        // ── 工厂方法 ──────────────────────────────────

        /// <summary>操作成功。</summary>
        public static readonly OperationResult Ok =
            new OperationResult(true, string.Empty, OperationErrorCode.None);

        /// <summary>操作失败，附带原因和分类。</summary>
        public static OperationResult Fail (
            string message,
            OperationErrorCode code = OperationErrorCode.Unknown) =>
            new OperationResult(false, message ?? "未知错误", code);

        /// <summary>设备不存在。</summary>
        public static OperationResult DeviceNotFound (string deviceId) =>
            Fail("设备不存在: " + deviceId, OperationErrorCode.DeviceNotFound);

        /// <summary>设备未连接。</summary>
        public static OperationResult DeviceNotConnected (string deviceName) =>
            Fail("设备未连接: " + deviceName, OperationErrorCode.DeviceNotConnected);

        /// <summary>变量不存在。</summary>
        public static OperationResult VariableNotFound (string variableId) =>
            Fail("变量不存在: " + variableId, OperationErrorCode.VariableNotFound);

        /// <summary>访问权限不符。</summary>
        public static OperationResult AccessDenied (string reason) =>
            Fail(reason, OperationErrorCode.AccessDenied);

        /// <summary>协议层失败，附带原始错误消息。</summary>
        public static OperationResult ProtocolError (string message) =>
            Fail(message, OperationErrorCode.ProtocolError);

        /// <summary>操作被取消。</summary>
        public static readonly OperationResult Cancelled =
            new OperationResult(false, "已取消", OperationErrorCode.Cancelled);

        // ── 辅助 ──────────────────────────────────────

        public override string ToString () =>
            Success ? "Ok" : $"Fail({ErrorCode}): {ErrorMessage}";
    }
}