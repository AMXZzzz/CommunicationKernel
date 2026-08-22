// -----------------------------------------------------------------------------
// 文件: ProtocolPluginsTests.cs
// 层级: Tests
// 作用: 协议插件帧构建、地址解析、校验算法、异常码映射的单元测试。
// 覆盖: Siemens S7, Panasonic Mewtocol, 各插件清单与工厂契约
// 说明: Modbus 三种变体的语义已收敛到 Plugins.Modbus.Core，
//       其测试集中在 ModbusCoreTests.cs，不再按插件分散重复。
// -----------------------------------------------------------------------------

using CommunicationKernel.Core.Abstractions.Results;

// ---- Siemens S7 internals
using CommunicationKernel.Plugins.Siemens.S7.Internal;

// ---- Panasonic Mewtocol internals
using CommunicationKernel.Plugins.Panasonic.Internal;

// ---- Public plugins (Manifest + Factory)
using CommunicationKernel.Plugins.Modbus.Tcp;
using CommunicationKernel.Plugins.Modbus.Rtu;
using CommunicationKernel.Plugins.Modbus.Ascii;
using CommunicationKernel.Plugins.Siemens.S7;
using CommunicationKernel.Plugins.Panasonic;

namespace CommunicationKernel.Tests;

// =============================================================================
// Siemens S7 — 地址解析
// =============================================================================

[TestClass]
public class S7AddressTests
{
    [TestMethod]
    public void ParseAddress_DB10_DBB0_ReturnsDataBlock()
    {
        var r = S7Frame.ParseAddress("DB10.DBB0");
        Assert.IsTrue(r.Success);
        Assert.AreEqual(S7Area.DataBlock, r.Value.area);
        Assert.AreEqual(10, r.Value.dbNumber);
        Assert.AreEqual(0, r.Value.byteOffset);
    }

    [TestMethod]
    public void ParseAddress_DB5_DBW4_ReturnsOffset4()
    {
        var r = S7Frame.ParseAddress("DB5.DBW4");
        Assert.IsTrue(r.Success);
        Assert.AreEqual(5, r.Value.dbNumber);
        Assert.AreEqual(4, r.Value.byteOffset);
    }

    [TestMethod]
    public void ParseAddress_MB10_ReturnsMerkers()
    {
        var r = S7Frame.ParseAddress("MB10");
        Assert.IsTrue(r.Success);
        Assert.AreEqual(S7Area.Merkers, r.Value.area);
        Assert.AreEqual(0, r.Value.dbNumber);
        Assert.AreEqual(10, r.Value.byteOffset);
    }

    [TestMethod]
    public void ParseAddress_V100_MapsToDataBlock1()
    {
        var r = S7Frame.ParseAddress("V100");
        Assert.IsTrue(r.Success);
        Assert.AreEqual(S7Area.V, r.Value.area);
        Assert.AreEqual(1, r.Value.dbNumber);
        Assert.AreEqual(100, r.Value.byteOffset);
    }

    [TestMethod]
    public void ParseAddress_IB0_ReturnsInputs()
    {
        var r = S7Frame.ParseAddress("IB0");
        Assert.IsTrue(r.Success);
        Assert.AreEqual(S7Area.Inputs, r.Value.area);
    }

    [TestMethod]
    public void ParseAddress_QB0_ReturnsOutputs()
    {
        var r = S7Frame.ParseAddress("QB0");
        Assert.IsTrue(r.Success);
        Assert.AreEqual(S7Area.Outputs, r.Value.area);
    }

    [TestMethod]
    public void ParseAddress_T5_ReturnsTimers()
    {
        var r = S7Frame.ParseAddress("T5");
        Assert.IsTrue(r.Success);
        Assert.AreEqual(S7Area.Timers, r.Value.area);
        Assert.AreEqual(5, r.Value.byteOffset);
    }

    [TestMethod]
    public void ParseAddress_C2_ReturnsCounters()
    {
        var r = S7Frame.ParseAddress("C2");
        Assert.IsTrue(r.Success);
        Assert.AreEqual(S7Area.Counters, r.Value.area);
    }

    [TestMethod]
    public void ParseAddress_Empty_Fails()
    {
        var r = S7Frame.ParseAddress("");
        Assert.IsFalse(r.Success);
    }

    [TestMethod]
    public void ParseAddress_InvalidFormat_Fails()
    {
        var r = S7Frame.ParseAddress("UNKNOWN99");
        Assert.IsFalse(r.Success);
    }
}

// =============================================================================
// Siemens S7 — 帧构建基本校验
// =============================================================================

[TestClass]
public class S7FrameBuilderTests
{
    [TestMethod]
    public void BuildCotpConnectRequest_Length22_StartsWithTpktVersion()
    {
        // TPKT(4) + COTP CR(18) = 22 bytes
        byte[] frame = S7Frame.BuildCotpConnectRequest(0x0300);
        Assert.HasCount(22, frame);
        Assert.AreEqual(0x03, frame[0]); // TPKT version
        Assert.AreEqual(0x00, frame[1]); // TPKT reserved
        Assert.AreEqual(0x00, frame[2]); // Length Hi
        Assert.AreEqual(0x16, frame[3]); // Length Lo = 22
        Assert.AreEqual(0xE0, frame[5]); // COTP CR
    }

    [TestMethod]
    public void BuildSetupCommunication_ContainsSetupFunctionCode()
    {
        byte[] frame = S7Frame.BuildSetupCommunication();
        Assert.IsGreaterThan(19, frame.Length); // frame.Length > 19
        // S7Comm function: Setup Communication = 0xF0
        // TPKT(4) + COTP DT(3) + S7Header(10) = offset 17
        Assert.AreEqual(0xF0, frame[17]);
    }

    [TestMethod]
    public void BuildReadVar_DB10_0_4bytes_CorrectFrame()
    {
        byte[] frame = S7Frame.BuildReadVar(S7Area.DataBlock, 10, 0, 4);
        // TPKT(4) + COTP DT(3) + S7Header(10) + Param(14) = 31
        Assert.HasCount(31, frame);
        Assert.AreEqual(0x03, frame[0]); // TPKT version
        Assert.AreEqual(0x04, frame[17]); // S7 function: Read Var
    }

    [TestMethod]
    public void ParseReadResponse_TooShort_ReturnsError()
    {
        var r = S7Frame.ParseReadResponse(new byte[10], 4);
        Assert.IsFalse(r.Success);
    }
}

// =============================================================================
// Panasonic Mewtocol — 地址解析
// =============================================================================

[TestClass]
public class MewtocolAddressTests
{
    [TestMethod]
    public void Parse_DT100_ReturnsDtAreaIndex100()
    {
        var r = MewtocolAddress.Parse("DT100");
        Assert.IsTrue(r.Success, r.ErrorMessage);
        Assert.AreEqual(MewtocolArea.DT, r.Value.Area);
        Assert.AreEqual(100, r.Value.Index);
    }

    [TestMethod]
    public void Parse_X0_ReturnsXArea()
    {
        // MEWTOCOL 外部输入触点区前缀为 'X'，不是 'WX'
        var r = MewtocolAddress.Parse("X0");
        Assert.IsTrue(r.Success, r.ErrorMessage);
        Assert.AreEqual(MewtocolArea.X, r.Value.Area);
    }

    [TestMethod]
    public void Parse_Y10_ReturnsYAreaIndex10()
    {
        // 外部输出触点区前缀为 'Y'
        var r = MewtocolAddress.Parse("Y10");
        Assert.IsTrue(r.Success, r.ErrorMessage);
        Assert.AreEqual(MewtocolArea.Y, r.Value.Area);
        Assert.AreEqual(10, r.Value.Index);
    }

    [TestMethod]
    public void Parse_Empty_Fails()
    {
        var r = MewtocolAddress.Parse("");
        Assert.IsFalse(r.Success);
    }
}

// =============================================================================
// Plugin Manifest & Factory — 元数据契约
// =============================================================================

[TestClass]
public class PluginManifestTests
{
    [TestMethod]
    public void ModbusTcpManifest_ApiVersion1_PluginIdCorrect()
    {
        var manifest = new ModbusTcpPluginManifest();
        Assert.AreEqual(1, manifest.Descriptor.ApiVersion);
        Assert.AreEqual("modbus-tcp", manifest.Descriptor.PluginId);
    }

    [TestMethod]
    public void ModbusRtuManifest_ApiVersion1_PluginIdCorrect()
    {
        var manifest = new ModbusRtuPluginManifest();
        Assert.AreEqual(1, manifest.Descriptor.ApiVersion);
        Assert.AreEqual("modbus-rtu", manifest.Descriptor.PluginId);
    }

    [TestMethod]
    public void ModbusAsciiManifest_ApiVersion1_PluginIdCorrect()
    {
        var manifest = new ModbusAsciiPluginManifest();
        Assert.AreEqual(1, manifest.Descriptor.ApiVersion);
        Assert.AreEqual("modbus-ascii", manifest.Descriptor.PluginId);
    }

    [TestMethod]
    public void SiemensS7_1200Factory_ProtocolIdAndApiVersionCorrect()
    {
        var factory = new SiemensS7_1200ProtocolDriverFactory();
        Assert.AreEqual("siemens-s7-1200", factory.Metadata.ProtocolId);
        Assert.AreEqual(1, factory.Metadata.PluginApiVersion);
    }

    [TestMethod]
    public void SiemensS7_200SmartFactory_ProtocolIdCorrect()
    {
        var factory = new SiemensS7_200SmartProtocolDriverFactory();
        Assert.AreEqual("siemens-s7-200smart", factory.Metadata.ProtocolId);
    }

    [TestMethod]
    public void MewtocolManifest_ApiVersion1_PluginIdCorrect()
    {
        var manifest = new MewtocolTcpPluginManifest();
        Assert.AreEqual(1, manifest.Descriptor.ApiVersion);
        Assert.AreEqual("panasonic-mewtocol", manifest.Descriptor.PluginId);
    }

    [TestMethod]
    public void ModbusRtuFactory_CreateDriver_ReturnsDriverWithCorrectProtocolId()
    {
        var factory = new ModbusRtuProtocolDriverFactory();
        var driver = factory.CreateDriver();
        Assert.IsNotNull(driver);
        Assert.AreEqual("modbus-rtu", driver.Metadata.ProtocolId);
    }

    [TestMethod]
    public void ModbusAsciiFactory_CreateDriver_ReturnsNonNull()
    {
        var factory = new ModbusAsciiProtocolDriverFactory();
        var driver = factory.CreateDriver();
        Assert.IsNotNull(driver);
        Assert.AreEqual("modbus-ascii", driver.Metadata.ProtocolId);
    }
}
