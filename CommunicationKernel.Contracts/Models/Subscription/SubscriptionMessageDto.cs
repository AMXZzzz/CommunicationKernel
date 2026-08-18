namespace CommunicationKernel.Contracts.Models;

public sealed class SubscriptionMessageDto {
    public string TopicCategory { get; init; } = string.Empty;
    public string TopicName { get; init; } = string.Empty;
    public string? RouteId { get; init; }
    public object? Payload { get; init; }
}
