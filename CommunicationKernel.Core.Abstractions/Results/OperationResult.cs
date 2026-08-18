using CommunicationKernel.Core.Abstractions.Errors;

namespace CommunicationKernel.Core.Abstractions.Results;

public sealed class OperationResult {
    public bool Success { get; }
    public string ErrorMessage { get; }
    public KernelErrorCode ErrorCode { get; }

    private OperationResult(bool success, string message, KernelErrorCode code) {
        Success = success;
        ErrorMessage = message ?? string.Empty;
        ErrorCode = code;
    }

    public static readonly OperationResult Ok = new(true, string.Empty, KernelErrorCode.None);
    public static readonly OperationResult Cancelled = new(false, "Cancelled", KernelErrorCode.Cancelled);

    public static OperationResult Fail(string message, KernelErrorCode code = KernelErrorCode.Unknown)
        => new(false, message ?? "Unknown error", code);

    public static OperationResult InvalidArgument(string message)
        => Fail(message, KernelErrorCode.InvalidArgument);

    public static OperationResult Timeout(string message)
        => Fail(message, KernelErrorCode.Timeout);

    public override string ToString() => Success
        ? "Ok"
        : $"Fail({ErrorCode}): {ErrorMessage}";
}
