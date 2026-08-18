namespace CommunicationKernel.Contracts.Models;

public class UnsubscribeRequestDto : UiRequestDto<string> {
    public string SubscriptionId { get; init; } = string.Empty;
}
