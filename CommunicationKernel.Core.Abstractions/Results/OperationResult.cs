using CommunicationKernel.Core.Abstractions.Errors;

// -----------------------------------------------------------------------------
// 文件: OperationResult.cs
// 层级: Core.Abstractions / Results
// 作用: 无返回值操作的统一结果类型，跨层传递成功/失败而不抛异常。
// -----------------------------------------------------------------------------

namespace CommunicationKernel.Core.Abstractions.Results;

/// <summary>
/// 无返回值的操作结果。传输连接、写 PLC、断开等"只关心成败"的调用使用本类型。
/// </summary>
public sealed class OperationResult {
    /// <summary>操作是否成功。</summary>
    public bool Success { get; }

    /// <summary>失败原因文案；成功时为空字符串。</summary>
    public string ErrorMessage { get; }

    /// <summary>失败时的内核错误码；成功时为 <see cref="KernelErrorCode.None"/>。</summary>
    public KernelErrorCode ErrorCode { get; }

    // ============================================================================
    // 构造
    // ============================================================================

    private OperationResult(bool success, string message, KernelErrorCode code) {
        // 成功标志、错误文案、错误码一次性冻结，结果对象此后只读
        Success = success;
        // message 为 null 时回落空串，避免上层对 ErrorMessage 再做空判断
        ErrorMessage = message ?? string.Empty;
        ErrorCode = code;
    }

    // ============================================================================
    // 预置结果
    // ============================================================================

    /// <summary>成功单例。无返回值场景直接复用，避免每次分配。</summary>
    public static readonly OperationResult Ok = new(true, string.Empty, KernelErrorCode.None);

    /// <summary>取消单例。调用方主动取消时使用，不得与超时/传输错误混淆。</summary>
    public static readonly OperationResult Cancelled = new(false, "Cancelled", KernelErrorCode.Cancelled);

    // ============================================================================
    // 工厂方法
    // ============================================================================

    /// <summary>构造失败结果；未指定错误码时归为 Unknown，便于发现漏映射。</summary>
    public static OperationResult Fail(string message, KernelErrorCode code = KernelErrorCode.Unknown)
        // 文案为空时给默认值，保证日志与 UI 总能拿到可读原因
        => new(false, message ?? "Unknown error", code);

    /// <summary>参数非法的快捷失败（端点、地址、长度等配置问题，重试无意义）。</summary>
    public static OperationResult InvalidArgument(string message)
        // 固定映射到 InvalidArgument，避免各调用点手填错误码不一致
        => Fail(message, KernelErrorCode.InvalidArgument);

    /// <summary>等待 PLC 响应超时的快捷失败。</summary>
    public static OperationResult Timeout(string message)
        // 固定映射到 Timeout，上层可据此触发重连
        => Fail(message, KernelErrorCode.Timeout);

    /// <summary>诊断字符串：成功为 "Ok"，失败带错误码与文案，便于日志检索。</summary>
    public override string ToString() => Success
        // 成功只输出 Ok，避免把空错误文案刷进日志
        ? "Ok"
        // 失败带错误码，便于按 TransportIoError / Timeout 等过滤
        : $"Fail({ErrorCode}): {ErrorMessage}";
}
