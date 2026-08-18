using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using CommunicationKernel.Communication.Transport.Abstractions;
using CommunicationKernel.Core.Abstractions.Results;
using CommunicationKernel.Engine.Router;
using CommunicationKernel.Engine.Router.Abstractions;
using CommunicationKernel.Engine.Router.Models;
using CommunicationKernel.EngineHost.Host;
using CommunicationKernel.Contracts.Models;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace CommunicationKernel.Tests;

[TestClass]
public sealed class EngineHostFacadeTests {

    [TestMethod]
    public async Task Facade_ShouldUseUnderlyingOrchestrator() {
        var host = new EngineHostFacade(new RouterOrchestrator());
        var key = new RouteKey("modbus", TransportKind.Tcp, "127.0.0.1", 502, "1");

        Assert.IsTrue(host.TryRegister(new RouteEntry {
            Key = key,
            TransportClient = new FakeTransportClient(),
            ProtocolDriver = new FakeProtocolDriver("modbus")
        }));

        OperationResult write = await host.ExecuteWriteAsync(key, _ => Task.FromResult(OperationResult.Ok), CancellationToken.None);
        OperationResult<byte[]> read = await host.ExecuteReadAsync(new ReadRequestKey(key, "D100", 2), _ => Task.FromResult(OperationResult<byte[]>.Ok(new byte[] { 1, 2 })), CancellationToken.None);

        Assert.IsTrue(write.Success);
        Assert.IsTrue(read.Success);
    }

    [TestMethod]
    public void MappingMethods_ShouldProduceContractsDtos() {
        var host = new EngineHostFacade(new RouterOrchestrator());
        var entry = new RouteEntry {
            Key = new RouteKey("modbus", TransportKind.Tcp, "127.0.0.1", 502, "1"),
            TransportClient = new FakeTransportClient(),
            ProtocolDriver = new FakeProtocolDriver("modbus")
        };

        RouteInfoDto route = host.ToRouteInfo(entry);
        QueryRoutesResponseDto routes = host.ToQueryRoutesResponse("req-1", new[] { entry });
        ReadResponseDto read = host.ToReadResponse("req-2", OperationResult<byte[]>.Ok(new byte[] { 0x11 }));
        WriteResponseDto write = host.ToWriteResponse("req-3", OperationResult.Ok);
        SubscribeResponseDto subscribe = host.ToSubscribeResponse("req-4", Guid.Parse("11111111-1111-1111-1111-111111111111"), OperationResult.Ok);
        UnsubscribeResponseDto unsubscribe = host.ToUnsubscribeResponse("req-5", true, OperationResult.Ok);
        DiagnosticsResponseDto diagnostics = host.ToDiagnosticsResponse("req-6", new DiagnosticsDto { RouteCount = 1, WriteQueueCount = 2, SubscriptionCount = 3, HostVersion = "1.0.0" });

        Assert.AreEqual("modbus", route.ProtocolId);
        Assert.HasCount(1, routes.Routes);
        Assert.IsNotNull(read.Data);
        Assert.AreEqual(0x11, read.Data[0]);
        Assert.AreEqual(CommunicationKernel.Core.Abstractions.Errors.KernelErrorCode.None, write.ErrorCode);
        Assert.AreEqual(CommunicationKernel.Core.Abstractions.Errors.KernelErrorCode.None, subscribe.ErrorCode);
        Assert.IsTrue(unsubscribe.Removed);
        Assert.IsNotNull(diagnostics.Diagnostics);
        Assert.AreEqual(3, diagnostics.Diagnostics.SubscriptionCount);
    }

    private sealed class FakeTransportClient : ITransportClient {
        public string TransportId => "fake";
        public TransportKind Kind => TransportKind.Custom;
        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
        public Task<OperationResult> ConnectAsync(TransportEndpoint endpoint, CancellationToken cancellationToken) => Task.FromResult(OperationResult.Ok);
        public Task<OperationResult<byte[]>> SendAndReceiveAsync(byte[] request, CancellationToken cancellationToken) => Task.FromResult(OperationResult<byte[]>.Ok(Array.Empty<byte>()));
        public Task<OperationResult> DisconnectAsync(CancellationToken cancellationToken) => Task.FromResult(OperationResult.Ok);
    }

    private sealed class FakeProtocolDriver : CommunicationKernel.Communication.Protocol.Abstractions.IProtocolDriver {
        public FakeProtocolDriver(string id) {
            Metadata = new CommunicationKernel.Communication.Protocol.Abstractions.ProtocolMetadata { ProtocolId = id, DisplayName = id, PluginApiVersion = 1 };
        }

        public CommunicationKernel.Communication.Protocol.Abstractions.ProtocolMetadata Metadata { get; }
        public Task<OperationResult<byte[]>> BuildReadFrameAsync(string address, int length, CancellationToken cancellationToken) => Task.FromResult(OperationResult<byte[]>.Ok(Array.Empty<byte>()));
        public Task<OperationResult<byte[]>> BuildWriteFrameAsync(string address, byte[] payload, CancellationToken cancellationToken) => Task.FromResult(OperationResult<byte[]>.Ok(Array.Empty<byte>()));
        public Task<OperationResult<byte[]>> ReadAsync(ITransportClient client, string address, int length, CancellationToken cancellationToken) => Task.FromResult(OperationResult<byte[]>.Ok(Array.Empty<byte>()));
        public Task<OperationResult> WriteAsync(ITransportClient client, string address, byte[] payload, CancellationToken cancellationToken) => Task.FromResult(OperationResult.Ok);
    }
}
