namespace CommunicationKernel.Core.Abstractions.Errors;

public enum KernelErrorCode {
    None = 0,
    Unknown = 1,
    Timeout = 2,
    Cancelled = 3,
    InvalidArgument = 4,

    TransportUnavailable = 100,
    TransportIoError = 101,

    ProtocolNotFound = 200,
    ProtocolError = 201,

    RouteNotFound = 300,
    RouteBusy = 301,

    PluginNotFound = 400,
    PluginLoadFailed = 401,
    PluginApiVersionMismatch = 402,
    PluginIsolationError = 403
}
