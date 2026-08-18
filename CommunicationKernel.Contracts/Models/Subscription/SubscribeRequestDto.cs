namespace CommunicationKernel.Contracts.Models;

public class SubscribeRequestDto : UiRequestDto<SubscriptionMessageDto> {
    public string TopicCategory { get; init; } = string.Empty;
    public string TopicName { get; init; } = string.Empty;
    public string? RouteId { get; init; }
}
