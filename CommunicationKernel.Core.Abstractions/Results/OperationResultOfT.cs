using CommunicationKernel.Core.Abstractions.Errors;

namespace CommunicationKernel.Core.Abstractions.Results;

public sealed class OperationResult<T> {
    public bool Success { get; }
    public string ErrorMessage { get; }
    public KernelErrorCode ErrorCode { get; }
    public T? Value { get; }

    private OperationResult(bool success, T? value, string message, KernelErrorCode code) {
        Success = success;
        Value = value;
        ErrorMessage = message ?? string.Empty;
        ErrorCode = code;
    }

    public static OperationResult<T> Ok(T value)
        => new(true, value, string.Empty, KernelErrorCode.None);

    public static OperationResult<T> Fail(string message, KernelErrorCode code = KernelErrorCode.Unknown)
        => new(false, default, message ?? "Unknown error", code);

    public static OperationResult<T> From(OperationResult result, T? value = default)
        => result.Success
            ? new(true, value, string.Empty, KernelErrorCode.None)
            : new(false, default, result.ErrorMessage, result.ErrorCode);
}
