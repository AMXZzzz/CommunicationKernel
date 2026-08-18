using System.Collections.Generic;

namespace CommunicationKernel.Contracts.Models;

public class QueryRoutesResponseDto : UiResponseDto<IReadOnlyList<RouteInfoDto>> {
    public IReadOnlyList<RouteInfoDto> Routes { get; init; } = new List<RouteInfoDto>();
}
