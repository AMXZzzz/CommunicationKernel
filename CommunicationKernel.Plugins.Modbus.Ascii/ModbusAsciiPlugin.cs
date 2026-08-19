using CommunicationKernel.Communication.Protocol.Abstractions;
using CommunicationKernel.Communication.Transport.Abstractions;
using CommunicationKernel.Core.Abstractions.Results;
using CommunicationKernel.Plugin.Runtime.Abstractions;

namespace CommunicationKernel.Plugins.Modbus.Ascii;

public sealed class ModbusAsciiPluginManifest : IPluginManifest {
    public PluginDescriptor Descriptor { get; } = new() {
        PluginId = "modbus-ascii",
        DisplayName = "Modbus ASCII Plugin",
        Kind = PluginKind.Protocol,
        ApiVersion = 1,
        Version = "1.0.0",
        EntryType = typeof(ModbusAsciiProtocolDriverFactory).FullName
    };
}

public sealed class ModbusAsciiProtocolDriverFactory : IProtocolDriverFactory {
    public ProtocolMetadata Metadata { get; } = new() {
        ProtocolId = "modbus-ascii",
        DisplayName = "Modbus ASCII",
        PluginApiVersion = 1
    };

    public IProtocolDriver CreateDriver() => new ModbusAsciiProtocolDriver();
}

public sealed class ModbusAsciiProtocolDriver : IProtocolDriver {
    public ProtocolMetadata Metadata => new() {
        ProtocolId = "modbus-ascii",
        DisplayName = "Modbus ASCII",
        PluginApiVersion = 1
    };

    public Task<OperationResult<byte[]>> BuildReadFrameAsync(string address, int length, CancellationToken cancellationToken) {
        _ = address;
        _ = length;
        _ = cancellationToken;
        return Task.FromResult(OperationResult<byte[]>.Fail("Modbus ASCII driver skeleton only", CommunicationKernel.Core.Abstractions.Errors.KernelErrorCode.ProtocolError));
    }

    public Task<OperationResult<byte[]>> BuildWriteFrameAsync(string address, byte[] payload, CancellationToken cancellationToken) {
        _ = address;
        _ = payload;
        _ = cancellationToken;
        return Task.FromResult(OperationResult<byte[]>.Fail("Modbus ASCII driver skeleton only", CommunicationKernel.Core.Abstractions.Errors.KernelErrorCode.ProtocolError));
    }

    public Task<OperationResult<byte[]>> ReadAsync(ITransportClient client, string address, int length, CancellationToken cancellationToken) {
        _ = client;
        _ = address;
        _ = length;
        _ = cancellationToken;
        return Task.FromResult(OperationResult<byte[]>.Fail("Modbus ASCII driver skeleton only", CommunicationKernel.Core.Abstractions.Errors.KernelErrorCode.ProtocolError));
    }

    public Task<OperationResult> WriteAsync(ITransportClient client, string address, byte[] payload, CancellationToken cancellationToken) {
        _ = client;
        _ = address;
        _ = payload;
        _ = cancellationToken;
        return Task.FromResult(OperationResult.Fail("Modbus ASCII driver skeleton only", CommunicationKernel.Core.Abstractions.Errors.KernelErrorCode.ProtocolError));
    }
}
