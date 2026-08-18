namespace CommunicationKernel.Contracts.Models;

public sealed class UiDiagnosticsQueryDto {
    public bool IncludeQueues { get; init; } = true;
    public bool IncludeRoutes { get; init; } = true;
    public bool IncludeSubscriptions { get; init; } = true;
}
