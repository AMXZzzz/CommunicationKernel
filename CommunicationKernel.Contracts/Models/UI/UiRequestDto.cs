namespace CommunicationKernel.Contracts.Models;

public class UiRequestDto<T> {
    public string RequestId { get; init; } = string.Empty;
    public string ClientId { get; init; } = string.Empty;
    public string UiType { get; init; } = string.Empty;
    public UiOperationType Operation { get; init; }
    public T? Payload { get; init; }
}
