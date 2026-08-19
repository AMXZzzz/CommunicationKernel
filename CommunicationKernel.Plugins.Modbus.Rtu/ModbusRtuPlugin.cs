using CommunicationKernel.Communication.Protocol.Abstractions;
using CommunicationKernel.Communication.Transport.Abstractions;
using CommunicationKernel.Core.Abstractions.Results;
using CommunicationKernel.Plugin.Runtime.Abstractions;

namespace CommunicationKernel.Plugins.Modbus.Rtu;

public sealed class ModbusRtuPluginManifest : IPluginManifest {
    public PluginDescriptor Descriptor { get; } = new() {
        PluginId = "modbus-rtu",
        DisplayName = "Modbus RTU Plugin",
        Kind = PluginKind.Protocol,
        ApiVersion = 1,
        Version = "1.0.0",
        EntryType = typeof(ModbusRtuProtocolDriverFactory).FullName
    };
}

public sealed class ModbusRtuProtocolDriverFactory : IProtocolDriverFactory {
    public ProtocolMetadata Metadata { get; } = new() {
        ProtocolId = "modbus-rtu",
        DisplayName = "Modbus RTU",
        PluginApiVersion = 1
    };

    public IProtocolDriver CreateDriver() => new ModbusRtuProtocolDriver();
}

public sealed class ModbusRtuProtocolDriver : IProtocolDriver {
    public ProtocolMetadata Metadata => new() {
        ProtocolId = "modbus-rtu",
        DisplayName = "Modbus RTU",
        PluginApiVersion = 1
    };

    public Task<OperationResult<byte[]>> BuildReadFrameAsync(string address, int length, CancellationToken cancellationToken) {
        _ = address;
        _ = length;
        _ = cancellationToken;
        return Task.FromResult(OperationResult<byte[]>.Fail("Modbus RTU driver skeleton only", CommunicationKernel.Core.Abstractions.Errors.KernelErrorCode.ProtocolError));
    }

    public Task<OperationResult<byte[]>> BuildWriteFrameAsync(string address, byte[] payload, CancellationToken cancellationToken) {
        _ = address;
        _ = payload;
        _ = cancellationToken;
        return Task.FromResult(OperationResult<byte[]>.Fail("Modbus RTU driver skeleton only", CommunicationKernel.Core.Abstractions.Errors.KernelErrorCode.ProtocolError));
    }

    public Task<OperationResult<byte[]>> ReadAsync(ITransportClient client, string address, int length, CancellationToken cancellationToken) {
        _ = client;
        _ = address;
        _ = length;
        _ = cancellationToken;
        return Task.FromResult(OperationResult<byte[]>.Fail("Modbus RTU driver skeleton only", CommunicationKernel.Core.Abstractions.Errors.KernelErrorCode.ProtocolError));
    }

    public Task<OperationResult> WriteAsync(ITransportClient client, string address, byte[] payload, CancellationToken cancellationToken) {
        _ = client;
        _ = address;
        _ = payload;
        _ = cancellationToken;
        return Task.FromResult(OperationResult.Fail("Modbus RTU driver skeleton only", CommunicationKernel.Core.Abstractions.Errors.KernelErrorCode.ProtocolError));
    }
}
