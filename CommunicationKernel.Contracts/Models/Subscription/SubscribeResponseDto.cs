namespace CommunicationKernel.Contracts.Models;

public class SubscribeResponseDto : UiResponseDto<string> {
    public string SubscriptionId { get; init; } = string.Empty;
}
