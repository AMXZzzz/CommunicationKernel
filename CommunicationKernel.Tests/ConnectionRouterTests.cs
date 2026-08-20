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
public sealed class ConnectionRouterTests {

    [TestMethod]
    public void TryRegister_SameKeyTwice_SecondShouldFail() {
        var router = new ConnectionRouter();
        var key = new RouteKey("modbus", TransportKind.Tcp, "127.0.0.1", 502, "1");

        bool first = router.TryRegister(new RouteEntry {
            Key = key,
            TransportClient = new FakeTransportClient(),
            ProtocolDriver = new FakeProtocolDriver("modbus")
        });
        bool second = router.TryRegister(new RouteEntry {
            Key = key,
            TransportClient = new FakeTransportClient(),
            ProtocolDriver = new FakeProtocolDriver("modbus")
        });

        Assert.IsTrue(first);
        Assert.IsFalse(second);
        Assert.AreEqual(1, router.Count);
    }

    [TestMethod]
    public void TryRemove_AfterRegister_ShouldReturnTrue() {
        var router = new ConnectionRouter();
        var key = new RouteKey("s7", TransportKind.Tcp, "192.168.0.2", 102, "0-1");
        router.TryRegister(new RouteEntry {
            Key = key,
            TransportClient = new FakeTransportClient(),
            ProtocolDriver = new FakeProtocolDriver("s7")
        });

        bool removed = router.TryRemove(key, out RouteEntry? entry);

        Assert.IsTrue(removed);
        Assert.IsNotNull(entry);
        Assert.AreEqual(0, router.Count);
    }

    private sealed class FakeTransportClient : ITransportClient {
        public string TransportId => "fake";
        public TransportKind Kind => TransportKind.Custom;

        public ValueTask DisposeAsync() => ValueTask.CompletedTask;

        public Task<OperationResult> ConnectAsync(TransportEndpoint endpoint, CancellationToken cancellationToken)
            => Task.FromResult(OperationResult.Ok);

        public Task<OperationResult> DisconnectAsync(CancellationToken cancellationToken)
            => Task.FromResult(OperationResult.Ok);

        public Task<OperationResult<byte[]>> SendAndReceiveAsync(byte[] request, CancellationToken cancellationToken)
            => Task.FromResult(OperationResult<byte[]>.Ok(Array.Empty<byte>()));
    }

    private sealed class FakeProtocolDriver : IProtocolDriver {
        public FakeProtocolDriver(string id) {
            Metadata = new ProtocolMetadata { ProtocolId = id, DisplayName = id, PluginApiVersion = 1 };
        }

        public ProtocolMetadata Metadata { get; }

        public OperationResult<byte[]> BuildReadFrame(string address, int length)
            => OperationResult<byte[]>.Ok(Array.Empty<byte>());

        public OperationResult<byte[]> BuildWriteFrame(string address, byte[] payload)
            => OperationResult<byte[]>.Ok(Array.Empty<byte>());

        public Task<OperationResult<byte[]>> ReadAsync(ITransportClient client, string address, int length, CancellationToken cancellationToken)
            => Task.FromResult(OperationResult<byte[]>.Ok(Array.Empty<byte>()));

        public Task<OperationResult> WriteAsync(ITransportClient client, string address, byte[] payload, CancellationToken cancellationToken)
            => Task.FromResult(OperationResult.Ok);
    }
}
