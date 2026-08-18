namespace CommunicationKernel.Engine.Router.Models;

public readonly record struct ReadRequestKey(
    RouteKey RouteKey,
    string Address,
    int Length);
