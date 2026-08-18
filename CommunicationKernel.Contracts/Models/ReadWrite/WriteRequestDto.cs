namespace CommunicationKernel.Contracts.Models;

public class WriteRequestDto : UiRequestDto<object> {
    public string ProtocolId { get; init; } = string.Empty;
    public string TransportKind { get; init; } = string.Empty;
    public string Address { get; init; } = string.Empty;
    public string? RouteId { get; init; }
    public object? Value { get; init; }
}
