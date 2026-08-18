namespace CommunicationKernel.Contracts.Models;

public class DiagnosticsResponseDto : UiResponseDto<DiagnosticsDto> {
    public DiagnosticsDto? Diagnostics { get; init; }
}
