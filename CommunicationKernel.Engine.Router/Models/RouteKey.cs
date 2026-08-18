using CommunicationKernel.Communication.Transport.Abstractions;

namespace CommunicationKernel.Engine.Router.Models;

public readonly record struct RouteKey(
    string ProtocolId,
    TransportKind TransportKind,
    string Address,
    int Port,
    string? Station) {

    public override string ToString() =>
        $"{ProtocolId}|{TransportKind}|{Address}:{Port}|{Station}";
}
