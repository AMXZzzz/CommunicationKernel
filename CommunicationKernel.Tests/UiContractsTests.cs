using CommunicationKernel.Contracts.Models;
using CommunicationKernel.Core.Abstractions.Errors;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace CommunicationKernel.Tests;

[TestClass]
public sealed class UiContractsTests {

    [TestMethod]
    public void UiRequestAndResponse_ShouldCarryPayloadAndStatus() {
        var req = new UiRequestDto<WriteRequestDto> {
            RequestId = "req-1",
            ClientId = "client-1",
            UiType = "blazor",
            Operation = UiOperationType.Write,
            Payload = new WriteRequestDto { Address = "D100", Value = 123 }
        };

        var ok = new UiResponseDto<string> { RequestId = req.RequestId, ErrorCode = KernelErrorCode.None, Data = "done" };
        var fail = new UiResponseDto<string> { RequestId = req.RequestId, ErrorCode = KernelErrorCode.ProtocolError, ErrorMessage = "bad" };

        Assert.AreEqual(UiOperationType.Write, req.Operation);
        Assert.AreEqual("done", ok.Data);
        Assert.IsTrue(ok.Success);
        Assert.IsFalse(fail.Success);
    }

    [TestMethod]
    public void ExistingDtos_ShouldExposeUnifiedBaseProperties() {
        var read = new ReadRequestDto { RequestId = "r1", ClientId = "c1", UiType = "blazor", Operation = UiOperationType.Read, Address = "D100", Length = 2 };
        var write = new WriteRequestDto { RequestId = "w1", ClientId = "c1", UiType = "blazor", Operation = UiOperationType.Write, Address = "D100", Value = 123 };
        var query = new QueryRoutesRequestDto { RequestId = "q1", ClientId = "c1", UiType = "blazor", Operation = UiOperationType.QueryRoutes, Payload = new UiRouteQueryDto { ProtocolId = "modbus" } };
        var diag = new DiagnosticsResponseDto { RequestId = "d1", ErrorCode = KernelErrorCode.None, Diagnostics = new DiagnosticsDto { RouteCount = 1, WriteQueueCount = 2, SubscriptionCount = 3, HostVersion = "1.0" } };

        Assert.AreEqual(UiOperationType.Read, read.Operation);
        Assert.AreEqual(UiOperationType.Write, write.Operation);
        Assert.AreEqual(UiOperationType.QueryRoutes, query.Operation);
        Assert.IsTrue(diag.Success);
        Assert.AreEqual(3, diag.Diagnostics.SubscriptionCount);
    }

    [TestMethod]
    public void QueryDtos_ShouldBeSimpleSerializableModels() {
        var route = new UiRouteQueryDto { RouteId = "r1", ProtocolId = "modbus", TransportKind = "Tcp" };
        var diag = new UiDiagnosticsQueryDto { IncludeQueues = false, IncludeRoutes = true, IncludeSubscriptions = false };

        Assert.AreEqual("modbus", route.ProtocolId);
        Assert.IsTrue(diag.IncludeRoutes);
        Assert.IsFalse(diag.IncludeQueues);
    }

    [TestMethod]
    public void CommonRequestTypes_ShouldMapCorrectly() {
        var subscribe = new SubscribeRequestDto { RequestId = "s1", ClientId = "c1", UiType = "blazor", Operation = UiOperationType.Subscribe, TopicCategory = "device", TopicName = "online" };
        var unsubscribe = new UnsubscribeRequestDto { RequestId = "u1", ClientId = "c1", UiType = "blazor", Operation = UiOperationType.Unsubscribe, SubscriptionId = "sub-1" };
        var readResp = new ReadResponseDto { RequestId = "rr1", ErrorCode = KernelErrorCode.None, Data = new byte[] { 1, 2 } };
        var writeResp = new WriteResponseDto { RequestId = "wr1", ErrorCode = KernelErrorCode.ProtocolError, ErrorMessage = "bad" };
        var queryResp = new QueryRoutesResponseDto { RequestId = "qr1", ErrorCode = KernelErrorCode.None, Routes = new[] { new RouteInfoDto { RouteId = "r1", ProtocolId = "modbus", TransportKind = "Tcp" } } };

        Assert.AreEqual(UiOperationType.Subscribe, subscribe.Operation);
        Assert.AreEqual("sub-1", unsubscribe.SubscriptionId);
        Assert.IsTrue(readResp.Success);
        Assert.IsFalse(writeResp.Success);
        Assert.HasCount(1, queryResp.Routes);
    }
}
