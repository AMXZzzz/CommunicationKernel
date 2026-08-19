using CommunicationKernel.Communication.Protocol.Abstractions;
using CommunicationKernel.Communication.Transport.Abstractions;
using CommunicationKernel.Core.Abstractions.Results;
using CommunicationKernel.Plugin.Runtime.Abstractions;

namespace CommunicationKernel.Plugins.Siemens.S7_1200;

public sealed class SiemensS7_1200PluginManifest : IPluginManifest {
    public PluginDescriptor Descriptor { get; } = new() {
        PluginId = "siemens-s7-1200",
        DisplayName = "Siemens S7-1200 Plugin",
        Kind = PluginKind.Protocol,
        ApiVersion = 1,
        Version = "1.0.0",
        EntryType = typeof(SiemensS7_1200ProtocolDriverFactory).FullName
    };
}

public sealed class SiemensS7_1200ProtocolDriverFactory : IProtocolDriverFactory {
    public ProtocolMetadata Metadata { get; } = new() {
        ProtocolId = "siemens-s7-1200",
        DisplayName = "Siemens S7-1200",
        PluginApiVersion = 1
    };

    public IProtocolDriver CreateDriver() => new SiemensS7_1200ProtocolDriver();
}

public sealed class SiemensS7_1200ProtocolDriver : IProtocolDriver {
    public ProtocolMetadata Metadata => new() {
        ProtocolId = "siemens-s7-1200",
        DisplayName = "Siemens S7-1200",
        PluginApiVersion = 1
    };

    public Task<OperationResult<byte[]>> BuildReadFrameAsync(string address, int length, CancellationToken cancellationToken) {
        _ = address;
        _ = length;
        _ = cancellationToken;
        return Task.FromResult(OperationResult<byte[]>.Fail("Siemens S7-1200 driver skeleton only", CommunicationKernel.Core.Abstractions.Errors.KernelErrorCode.ProtocolError));
    }

    public Task<OperationResult<byte[]>> BuildWriteFrameAsync(string address, byte[] payload, CancellationToken cancellationToken) {
        _ = address;
        _ = payload;
        _ = cancellationToken;
        return Task.FromResult(OperationResult<byte[]>.Fail("Siemens S7-1200 driver skeleton only", CommunicationKernel.Core.Abstractions.Errors.KernelErrorCode.ProtocolError));
    }

    public Task<OperationResult<byte[]>> ReadAsync(ITransportClient client, string address, int length, CancellationToken cancellationToken) {
        _ = client;
        _ = address;
        _ = length;
        _ = cancellationToken;
        return Task.FromResult(OperationResult<byte[]>.Fail("Siemens S7-1200 driver skeleton only", CommunicationKernel.Core.Abstractions.Errors.KernelErrorCode.ProtocolError));
    }

    public Task<OperationResult> WriteAsync(ITransportClient client, string address, byte[] payload, CancellationToken cancellationToken) {
        _ = client;
        _ = address;
        _ = payload;
        _ = cancellationToken;
        return Task.FromResult(OperationResult.Fail("Siemens S7-1200 driver skeleton only", CommunicationKernel.Core.Abstractions.Errors.KernelErrorCode.ProtocolError));
    }
}
