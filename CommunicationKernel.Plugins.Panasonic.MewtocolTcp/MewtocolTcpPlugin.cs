using CommunicationKernel.Communication.Protocol.Abstractions;
using CommunicationKernel.Communication.Transport.Abstractions;
using CommunicationKernel.Core.Abstractions.Results;
using CommunicationKernel.Plugin.Runtime.Abstractions;

namespace CommunicationKernel.Plugins.Panasonic.MewtocolTcp;

public sealed class PanasonicMewtocolTcpPluginManifest : IPluginManifest {
    public PluginDescriptor Descriptor { get; } = new() {
        PluginId = "panasonic-mewtocol-tcp",
        DisplayName = "Panasonic Mewtocol TCP Plugin",
        Kind = PluginKind.Protocol,
        ApiVersion = 1,
        Version = "1.0.0",
        EntryType = typeof(PanasonicMewtocolTcpProtocolDriverFactory).FullName
    };
}

public sealed class PanasonicMewtocolTcpProtocolDriverFactory : IProtocolDriverFactory {
    public ProtocolMetadata Metadata { get; } = new() {
        ProtocolId = "panasonic-mewtocol-tcp",
        DisplayName = "Panasonic Mewtocol TCP",
        PluginApiVersion = 1
    };

    public IProtocolDriver CreateDriver() => new PanasonicMewtocolTcpProtocolDriver();
}

public sealed class PanasonicMewtocolTcpProtocolDriver : IProtocolDriver {
    public ProtocolMetadata Metadata => new() {
        ProtocolId = "panasonic-mewtocol-tcp",
        DisplayName = "Panasonic Mewtocol TCP",
        PluginApiVersion = 1
    };

    public Task<OperationResult<byte[]>> BuildReadFrameAsync(string address, int length, CancellationToken cancellationToken) {
        _ = address;
        _ = length;
        _ = cancellationToken;
        return Task.FromResult(OperationResult<byte[]>.Fail("Panasonic Mewtocol TCP driver skeleton only", CommunicationKernel.Core.Abstractions.Errors.KernelErrorCode.ProtocolError));
    }

    public Task<OperationResult<byte[]>> BuildWriteFrameAsync(string address, byte[] payload, CancellationToken cancellationToken) {
        _ = address;
        _ = payload;
        _ = cancellationToken;
        return Task.FromResult(OperationResult<byte[]>.Fail("Panasonic Mewtocol TCP driver skeleton only", CommunicationKernel.Core.Abstractions.Errors.KernelErrorCode.ProtocolError));
    }

    public Task<OperationResult<byte[]>> ReadAsync(ITransportClient client, string address, int length, CancellationToken cancellationToken) {
        _ = client;
        _ = address;
        _ = length;
        _ = cancellationToken;
        return Task.FromResult(OperationResult<byte[]>.Fail("Panasonic Mewtocol TCP driver skeleton only", CommunicationKernel.Core.Abstractions.Errors.KernelErrorCode.ProtocolError));
    }

    public Task<OperationResult> WriteAsync(ITransportClient client, string address, byte[] payload, CancellationToken cancellationToken) {
        _ = client;
        _ = address;
        _ = payload;
        _ = cancellationToken;
        return Task.FromResult(OperationResult.Fail("Panasonic Mewtocol TCP driver skeleton only", CommunicationKernel.Core.Abstractions.Errors.KernelErrorCode.ProtocolError));
    }
}
