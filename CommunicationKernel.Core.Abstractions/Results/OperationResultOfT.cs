using System.Diagnostics.CodeAnalysis;
using CommunicationKernel.Core.Abstractions.Errors;

// -----------------------------------------------------------------------------
// 文件: OperationResultOfT.cs
// 层级: Core.Abstractions / Results
// 作用: 携带返回值的操作结果（读 PLC、读帧等），成功时 Value 对编译器保证非 null。
// -----------------------------------------------------------------------------

namespace CommunicationKernel.Core.Abstractions.Results;

/// <summary>
/// 携带返回值的操作结果。
/// <para>
/// <see cref="Success"/> 属性标注了 <see cref="MemberNotNullWhenAttribute"/>：
/// 当 <c>Success == true</c> 时，编译器静态分析可知 <see cref="Value"/> 保证非 null，
/// 无需调用方写 null 条件运算符或 null 包容运算符。
/// </para>
/// </summary>
public sealed class OperationResult<T>
{
    /// <summary>
    /// 操作是否成功。
    /// <list type="bullet">
    ///   <item><c>true</c>：<see cref="Value"/> 保证非 null（编译器静态验证）。</item>
    ///   <item><c>false</c>：<see cref="ErrorMessage"/> 和 <see cref="ErrorCode"/> 携带失败原因。</item>
    /// </list>
    /// </summary>
    [MemberNotNullWhen(true, nameof(Value))]
    public bool Success { get; }

    /// <summary>失败原因文案；成功时为空字符串。</summary>
    public string ErrorMessage { get; }

    /// <summary>失败时的内核错误码；成功时为 <see cref="KernelErrorCode.None"/>。</summary>
    public KernelErrorCode ErrorCode { get; }

    /// <summary>
    /// 操作返回值。当 <see cref="Success"/> 为 <c>true</c> 时非 null（编译器可验证）。
    /// </summary>
    public T? Value { get; }

    // ============================================================================
    // 构造
    // ============================================================================

    private OperationResult(bool success, T? value, string message, KernelErrorCode code)
    {
        // 成功时冻结 Value；失败时 Value 为 default，文案/错误码供上层展示
        Success      = success;
        Value        = value;
        // message 为 null 时回落空串，避免上层对 ErrorMessage 再做空判断
        ErrorMessage = message ?? string.Empty;
        ErrorCode    = code;
    }

    // ============================================================================
    // 工厂方法
    // ============================================================================

    /// <summary>创建成功结果。</summary>
    public static OperationResult<T> Ok(T value)
        // 错误码置 None、文案置空；调用方此后可直接使用 Value
        => new(true, value, string.Empty, KernelErrorCode.None);

    /// <summary>创建失败结果。</summary>
    public static OperationResult<T> Fail(string message, KernelErrorCode code = KernelErrorCode.Unknown)
        // Value 为 default；文案为空时给默认值，未指定错误码时归为 Unknown
        => new(false, default, message ?? "Unknown error", code);

    /// <summary>从无值结果转换（成功时附带可选值，失败时传播错误信息）。</summary>
    public static OperationResult<T> From(OperationResult result, T? value = default)
        => result.Success
            // 无值结果成功：附带调用方给出的可选值（例如读帧后的字节）
            ? new(true,  value,   string.Empty,       KernelErrorCode.None)
            // 无值结果失败：原样传播错误码与文案，不丢诊断信息
            : new(false, default, result.ErrorMessage, result.ErrorCode);

    /// <summary>诊断字符串：成功带 Value，失败带错误码与文案，便于日志检索。</summary>
    // 成功输出载荷便于对照 PLC 回读；失败带错误码便于按 Timeout / ProtocolError 过滤
    public override string ToString()
        => Success ? $"Ok({Value})" : $"Fail({ErrorCode}): {ErrorMessage}";
}
