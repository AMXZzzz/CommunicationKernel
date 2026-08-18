namespace CommunicationKernel.Contracts.Models;

public enum UiOperationType {
    Read = 0,
    Write = 1,
    Subscribe = 2,
    Unsubscribe = 3,
    QueryRoutes = 4,
    Diagnostics = 5
}
