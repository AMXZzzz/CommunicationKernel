namespace CommunicationKernel.Contracts.Models;

public sealed class RouteInfoDto {
    public string RouteId { get; init; } = string.Empty;
    public string ProtocolId { get; init; } = string.Empty;
    public string TransportKind { get; init; } = string.Empty;
    public string Address { get; init; } = string.Empty;
    public int Port { get; init; }
    public string? Station { get; init; }
}
