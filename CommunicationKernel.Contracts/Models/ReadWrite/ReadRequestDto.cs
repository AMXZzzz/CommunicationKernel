namespace CommunicationKernel.Contracts.Models;

public class ReadRequestDto : UiRequestDto<UiRouteQueryDto> {
    public string ProtocolId { get; init; } = string.Empty;
    public string TransportKind { get; init; } = string.Empty;
    public string Address { get; init; } = string.Empty;
    public int Length { get; init; }
    public string? RouteId { get; init; }
}
