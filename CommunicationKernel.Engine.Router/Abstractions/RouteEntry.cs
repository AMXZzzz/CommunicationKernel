using CommunicationKernel.Communication.Protocol.Abstractions;
using CommunicationKernel.Communication.Transport.Abstractions;
using CommunicationKernel.Engine.Router.Models;

namespace CommunicationKernel.Engine.Router.Abstractions;

public sealed class RouteEntry {
    public required RouteKey Key { get; init; }
    public required ITransportClient TransportClient { get; init; }
    public required IProtocolDriver ProtocolDriver { get; init; }
}
