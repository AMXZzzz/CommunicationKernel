namespace CommunicationKernel.Contracts.Models;

public sealed class UiRouteQueryDto {
    public string? RouteId { get; init; }
    public string? ProtocolId { get; init; }
    public string? TransportKind { get; init; }
}
