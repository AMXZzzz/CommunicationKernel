using System;
using System.Threading;
using System.Threading.Tasks;
using CommunicationKernel.Communication.Protocol.Abstractions;
using CommunicationKernel.Communication.Transport.Abstractions;
using CommunicationKernel.Core.Abstractions.Results;
using CommunicationKernel.Engine.Router;
using CommunicationKernel.Engine.Router.Abstractions;
using CommunicationKernel.Engine.Router.Models;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace CommunicationKernel.Tests;

[TestClass]
public sealed class RouterOrchestratorTests {

    [TestMethod]
    public void Register_Get_Remove_ShouldRouteThroughConnectionRouter() {
        IRouterOrchestrator orchestrator = new RouterOrchestrator();
        var key = new RouteKey("modbus", TransportKind.Tcp, "127.0.0.1", 502, "1");
        var entry = new RouteEntry {
            Key = key,
            TransportClient = new FakeTransportClient(),
            ProtocolDriver = new FakeProtocolDriver("modbus")
        };

        Assert.IsTrue(orchestrator.TryRegister(entry));
        Assert.IsTrue(orchestrator.TryGet(key, out RouteEntry? found));
        Assert.IsNotNull(found);
        Assert.IsTrue(orchestrator.TryRemove(key, out RouteEntry? removed));
        Assert.IsNotNull(removed);
    }

    [TestMethod]
    public async Task ExecuteWriteAsync_ShouldDelegateToWriteScheduler() {
        IRouterOrchestrator orchestrator = new RouterOrchestrator();
        var key = new RouteKey("s7", TransportKind.Tcp, "192.168.0.2", 102, "0-1");

        OperationResult result = await orchestrator.ExecuteWriteAsync(
            key,
            _ => Task.FromResult(OperationResult.Ok),
            CancellationToken.None);

        Assert.IsTrue(result.Success);
    }

    [TestMethod]
    public async Task ExecuteReadAsync_ShouldDelegateToReadCoordinator() {
        IRouterOrchestrator orchestrator = new RouterOrchestrator();
        var route = new RouteKey("modbus", TransportKind.Tcp, "127.0.0.1", 502, "1");
        var request = new ReadRequestKey(route, "D100", 2);

        OperationResult<byte[]> result = await orchestrator.ExecuteReadAsync(
            request,
            _ => Task.FromResult(OperationResult<byte[]>.Ok(new byte[] { 0x01, 0x02 })),
            CancellationToken.None);

        Assert.IsTrue(result.Success);
        CollectionAssert.AreEqual(new byte[] { 0x01, 0x02 }, result.Value);
    }

    [TestMethod]
    public async Task PublishAsync_ShouldDelegateToSubscriptionHub() {
        IRouterOrchestrator orchestrator = new RouterOrchestrator();
        var topic = new SubscriptionTopic("device", "online", "r1");
        int called = 0;

        orchestrator.Subscribe(topic, (payload, ct) => {
            Interlocked.Increment(ref called);
            return Task.CompletedTask;
        });

        await orchestrator.PublishAsync(topic, "ok", CancellationToken.None);

        Assert.AreEqual(1, called);
    }

    [TestMethod]
    public async Task ExecuteWriteAsync_WhenActionThrows_ShouldReturnFail() {
        IRouterOrchestrator orchestrator = new RouterOrchestrator();
        var key = new RouteKey("modbus", TransportKind.Tcp, "127.0.0.1", 502, "1");

        OperationResult result = await orchestrator.ExecuteWriteAsync(
            key,
            _ => throw new InvalidOperationException("write failed"),
            CancellationToken.None);

        Assert.IsFalse(result.Success);
        StringAssert.Contains(result.ErrorMessage, "write failed");
    }

    [TestMethod]
    public async Task ExecuteReadAsync_WhenActionThrows_ShouldReturnFail() {
        IRouterOrchestrator orchestrator = new RouterOrchestrator();
        var route = new RouteKey("modbus", TransportKind.Tcp, "127.0.0.1", 502, "1");
        var request = new ReadRequestKey(route, "D100", 2);

        OperationResult<byte[]> result = await orchestrator.ExecuteReadAsync(
            request,
            _ => throw new InvalidOperationException("read failed"),
            CancellationToken.None);

        Assert.IsFalse(result.Success);
        StringAssert.Contains(result.ErrorMessage, "read failed");
    }

    private sealed class FakeTransportClient : ITransportClient {
        public string TransportId => "fake";
        public TransportKind Kind => TransportKind.Custom;
        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
        public Task<OperationResult> ConnectAsync(TransportEndpoint endpoint, CancellationToken cancellationToken) => Task.FromResult(OperationResult.Ok);
        public Task<OperationResult<byte[]>> SendAndReceiveAsync(byte[] request, CancellationToken cancellationToken) => Task.FromResult(OperationResult<byte[]>.Ok(Array.Empty<byte>()));
        public Task<OperationResult> DisconnectAsync(CancellationToken cancellationToken) => Task.FromResult(OperationResult.Ok);
    }

    private sealed class FakeProtocolDriver : IProtocolDriver {
        public FakeProtocolDriver(string id) {
            Metadata = new ProtocolMetadata { ProtocolId = id, DisplayName = id, PluginApiVersion = 1 };
        }

        public ProtocolMetadata Metadata { get; }
        public Task<OperationResult<byte[]>> BuildReadFrameAsync(string address, int length, CancellationToken cancellationToken) => Task.FromResult(OperationResult<byte[]>.Ok(Array.Empty<byte>()));
        public Task<OperationResult<byte[]>> BuildWriteFrameAsync(string address, byte[] payload, CancellationToken cancellationToken) => Task.FromResult(OperationResult<byte[]>.Ok(Array.Empty<byte>()));
        public Task<OperationResult<byte[]>> ReadAsync(ITransportClient client, string address, int length, CancellationToken cancellationToken) => Task.FromResult(OperationResult<byte[]>.Ok(Array.Empty<byte>()));
        public Task<OperationResult> WriteAsync(ITransportClient client, string address, byte[] payload, CancellationToken cancellationToken) => Task.FromResult(OperationResult.Ok);
    }
}
