using CommunicationKernel.Core.Abstractions.Errors;

namespace CommunicationKernel.Contracts.Models;

public class UiResponseDto<T> {
    public string RequestId { get; init; } = string.Empty;
    public KernelErrorCode ErrorCode { get; init; }
    public string ErrorMessage { get; init; } = string.Empty;
    public T? Data { get; init; }
    public bool Success => ErrorCode == KernelErrorCode.None;
}
