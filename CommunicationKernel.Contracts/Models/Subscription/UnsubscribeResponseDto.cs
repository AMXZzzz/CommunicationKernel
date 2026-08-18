namespace CommunicationKernel.Contracts.Models;

public class UnsubscribeResponseDto : UiResponseDto<object> {
    public bool Removed { get; init; }
}
