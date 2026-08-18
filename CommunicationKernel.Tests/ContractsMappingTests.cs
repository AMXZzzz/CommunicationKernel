using CommunicationKernel.Contracts.Models;
using CommunicationKernel.Core.Abstractions.Errors;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace CommunicationKernel.Tests;

[TestClass]
public sealed class ContractsMappingTests {

    [TestMethod]
    public void ContractEnvelope_ShouldExposeSuccessFromErrorCode() {
        var ok = new ContractEnvelope<string> { ErrorCode = KernelErrorCode.None, Data = "x" };
        var fail = new ContractEnvelope<string> { ErrorCode = KernelErrorCode.ProtocolError, ErrorMessage = "bad" };

        Assert.IsTrue(ok.Success);
        Assert.IsFalse(fail.Success);
    }

    [TestMethod]
    public void ReadWriteDto_ShouldHoldCoreFields() {
        var read = new ReadRequestDto { RequestId = "r1", ClientId = "c1", UiType = "blazor", ProtocolId = "modbus", TransportKind = "Tcp", Address = "D100", Length = 2 };
        var write = new WriteRequestDto { RequestId = "w1", ClientId = "c1", UiType = "blazor", ProtocolId = "modbus", TransportKind = "Tcp", Address = "D100", Value = 123 };

        Assert.AreEqual("r1", read.RequestId);
        Assert.AreEqual("w1", write.RequestId);
        Assert.AreEqual(123, write.Value);
    }
}
