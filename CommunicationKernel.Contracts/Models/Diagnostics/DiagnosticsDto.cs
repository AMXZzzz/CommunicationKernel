namespace CommunicationKernel.Contracts.Models;

public sealed class DiagnosticsDto {
    public int RouteCount { get; init; }
    public int WriteQueueCount { get; init; }
    public int SubscriptionCount { get; init; }
    public string HostVersion { get; init; } = string.Empty;
}
