using CommunicationKernel.Communication.Transport.Abstractions;
using CommunicationKernel.Engine.Router.Models;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace CommunicationKernel.Tests;

[TestClass]
public sealed class RouteKeyTests {

    [TestMethod]
    public void RouteKey_SameFields_ShouldBeEqual() {
        var a = new RouteKey("modbus", TransportKind.Tcp, "127.0.0.1", 502, "1");
        var b = new RouteKey("modbus", TransportKind.Tcp, "127.0.0.1", 502, "1");

        Assert.AreEqual(a, b);
        Assert.AreEqual(a.GetHashCode(), b.GetHashCode());
    }

    [TestMethod]
    public void RouteKey_ToString_ShouldContainKeyParts() {
        var key = new RouteKey("s7", TransportKind.Tcp, "192.168.1.10", 102, "rack0-slot1");
        string text = key.ToString();

        StringAssert.Contains(text, "s7");
        StringAssert.Contains(text, "Tcp");
        StringAssert.Contains(text, "192.168.1.10:102");
        StringAssert.Contains(text, "rack0-slot1");
    }
}
