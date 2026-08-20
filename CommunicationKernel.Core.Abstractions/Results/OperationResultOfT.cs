using System.Diagnostics.CodeAnalysis;
using CommunicationKernel.Core.Abstractions.Errors;

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

    public string ErrorMessage { get; }
    public KernelErrorCode ErrorCode { get; }

    /// <summary>
    /// 操作返回值。当 <see cref="Success"/> 为 <c>true</c> 时非 null（编译器可验证）。
    /// </summary>
    public T? Value { get; }

    private OperationResult(bool success, T? value, string message, KernelErrorCode code)
    {
        Success      = success;
        Value        = value;
        ErrorMessage = message ?? string.Empty;
        ErrorCode    = code;
    }

    /// <summary>创建成功结果。</summary>
    public static OperationResult<T> Ok(T value)
        => new(true, value, string.Empty, KernelErrorCode.None);

    /// <summary>创建失败结果。</summary>
    public static OperationResult<T> Fail(string message, KernelErrorCode code = KernelErrorCode.Unknown)
        => new(false, default, message ?? "Unknown error", code);

    /// <summary>从无值结果转换（成功时附带可选值，失败时传播错误信息）。</summary>
    public static OperationResult<T> From(OperationResult result, T? value = default)
        => result.Success
            ? new(true,  value,   string.Empty,       KernelErrorCode.None)
            : new(false, default, result.ErrorMessage, result.ErrorCode);

    public override string ToString()
        => Success ? $"Ok({Value})" : $"Fail({ErrorCode}): {ErrorMessage}";
}
