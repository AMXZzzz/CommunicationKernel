// -----------------------------------------------------------------------------
// 文件: RouteKeyTests.cs
// 层级: 测试
// 作用: 覆盖 RouteKey 的相等性与诊断字符串。
// 说明:
//   RouteKey 是路由表的字典键。相等性或哈希不一致会导致
//   「明明已登记却查不到」或「两条路由被当成一条」。
// -----------------------------------------------------------------------------

using CommunicationKernel.Communication.Transport.Abstractions;
using CommunicationKernel.Core.EngineRouter.Models;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace CommunicationKernel.Tests;

// 值对象契约：同字段必须相等，ToString 必须能定位到具体设备
[TestClass]
public sealed class RouteKeyTests {

    // 相同五元组必须相等且哈希一致，否则 ConcurrentDictionary 会把它当成两条路由
    [TestMethod]
    public void RouteKey_SameFields_ShouldBeEqual() {
        // ============================================================================
        // Arrange
        // ============================================================================
        // 两条字段完全相同的路由键
        var a = new RouteKey("modbus", TransportKind.Tcp, "127.0.0.1", 502, "1");
        var b = new RouteKey("modbus", TransportKind.Tcp, "127.0.0.1", 502, "1");

        // ============================================================================
        // Act / Assert
        // ============================================================================
        // 相等性与哈希必须同时成立，缺一不可
        Assert.AreEqual(a, b);
        Assert.AreEqual(a.GetHashCode(), b.GetHashCode());
    }

    // ToString 必须包含协议、介质、地址、端口、站号，日志才能定位到具体设备
    [TestMethod]
    public void RouteKey_ToString_ShouldContainKeyParts() {
        // ============================================================================
        // Arrange
        // ============================================================================
        var key = new RouteKey("s7", TransportKind.Tcp, "192.168.1.10", 102, "rack0-slot1");

        // ============================================================================
        // Act
        // ============================================================================
        string text = key.ToString();

        // ============================================================================
        // Assert
        // ============================================================================
        // 诊断字符串缺任何一段都会让现场日志无法反查设备
        StringAssert.Contains(text, "s7");
        StringAssert.Contains(text, "Tcp");
        StringAssert.Contains(text, "192.168.1.10:102");
        StringAssert.Contains(text, "rack0-slot1");
    }
}
