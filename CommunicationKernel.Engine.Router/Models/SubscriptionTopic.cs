namespace CommunicationKernel.Engine.Router.Models;

public readonly record struct SubscriptionTopic(
    string Category,
    string Name,
    string? RouteId = null);
