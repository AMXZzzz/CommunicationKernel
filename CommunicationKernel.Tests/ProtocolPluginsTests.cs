// -----------------------------------------------------------------------------
// 文件: ProtocolPluginsTests.cs
// 层级: 测试
// 作用: 协议插件帧构建、地址解析、校验算法、异常码映射的单元测试。
// 覆盖: Siemens S7, Panasonic Mewtocol, 各插件清单与工厂契约
// 说明: Modbus 三种变体的语义已收敛到 Plugins.Modbus.Core，
//       其测试集中在 ModbusCoreTests.cs，不再按插件分散重复。
// -----------------------------------------------------------------------------

using CommunicationKernel.Core.Abstractions.Results;

// ---- Siemens S7 internals
using CommunicationKernel.Plugins.Protocol.Siemens.S7.Internal;

// ---- Panasonic Mewtocol internals
using CommunicationKernel.Plugins.Protocol.Panasonic.Internal;

// ---- Public plugins (Manifest + Factory)
using CommunicationKernel.Plugins.Protocol.Modbus.Tcp;
using CommunicationKernel.Plugins.Protocol.Modbus.Rtu;
using CommunicationKernel.Plugins.Protocol.Modbus.Ascii;
using CommunicationKernel.Plugins.Protocol.Siemens.S7;
using CommunicationKernel.Plugins.Protocol.Panasonic;

namespace CommunicationKernel.Tests;

// =============================================================================
// Siemens S7 — 地址解析
// =============================================================================

// S7 地址字符串必须映射到正确区域、DB 号与字节偏移
[TestClass]
public class S7AddressTests
{
    // DB10.DBB0 → DataBlock 区、DB 号 10、偏移 0
    [TestMethod]
    public void ParseAddress_DB10_DBB0_ReturnsDataBlock()
    {
        // ============================================================================
        // Arrange / Act
        // ============================================================================
        var r = S7Frame.ParseAddress("DB10.DBB0");

        // ============================================================================
        // Assert
        // ============================================================================
        Assert.IsTrue(r.Success);
        Assert.AreEqual(S7Area.DataBlock, r.Value.area);
        Assert.AreEqual(10, r.Value.dbNumber);
        Assert.AreEqual(0, r.Value.byteOffset);
    }

    // DB5.DBW4 的字节偏移必须是 4，不得按字号再乘 2
    [TestMethod]
    public void ParseAddress_DB5_DBW4_ReturnsOffset4()
    {
        // ============================================================================
        // Arrange / Act
        // ============================================================================
        var r = S7Frame.ParseAddress("DB5.DBW4");

        // ============================================================================
        // Assert
        // ============================================================================
        Assert.IsTrue(r.Success);
        Assert.AreEqual(5, r.Value.dbNumber);
        Assert.AreEqual(4, r.Value.byteOffset);
    }

    // MB10 → Merkers 区，偏移 10
    [TestMethod]
    public void ParseAddress_MB10_ReturnsMerkers()
    {
        // ============================================================================
        // Arrange / Act
        // ============================================================================
        var r = S7Frame.ParseAddress("MB10");

        // ============================================================================
        // Assert
        // ============================================================================
        Assert.IsTrue(r.Success);
        Assert.AreEqual(S7Area.Merkers, r.Value.area);
        Assert.AreEqual(0, r.Value.dbNumber);
        Assert.AreEqual(10, r.Value.byteOffset);
    }

    // V 区是 S7-200 的别名，映射到 DB1
    [TestMethod]
    public void ParseAddress_V100_MapsToDataBlock1()
    {
        // ============================================================================
        // Arrange / Act
        // ============================================================================
        var r = S7Frame.ParseAddress("V100");

        // ============================================================================
        // Assert
        // ============================================================================
        Assert.IsTrue(r.Success);
        Assert.AreEqual(S7Area.V, r.Value.area);
        Assert.AreEqual(1, r.Value.dbNumber);
        Assert.AreEqual(100, r.Value.byteOffset);
    }

    // IB0 → 输入映像区
    [TestMethod]
    public void ParseAddress_IB0_ReturnsInputs()
    {
        // ============================================================================
        // Arrange / Act
        // ============================================================================
        var r = S7Frame.ParseAddress("IB0");

        // ============================================================================
        // Assert
        // ============================================================================
        Assert.IsTrue(r.Success);
        Assert.AreEqual(S7Area.Inputs, r.Value.area);
    }

    // QB0 → 输出映像区
    [TestMethod]
    public void ParseAddress_QB0_ReturnsOutputs()
    {
        // ============================================================================
        // Arrange / Act
        // ============================================================================
        var r = S7Frame.ParseAddress("QB0");

        // ============================================================================
        // Assert
        // ============================================================================
        Assert.IsTrue(r.Success);
        Assert.AreEqual(S7Area.Outputs, r.Value.area);
    }

    // T5 → 定时器区，偏移 5
    [TestMethod]
    public void ParseAddress_T5_ReturnsTimers()
    {
        // ============================================================================
        // Arrange / Act
        // ============================================================================
        var r = S7Frame.ParseAddress("T5");

        // ============================================================================
        // Assert
        // ============================================================================
        Assert.IsTrue(r.Success);
        Assert.AreEqual(S7Area.Timers, r.Value.area);
        Assert.AreEqual(5, r.Value.byteOffset);
    }

    // C2 → 计数器区
    [TestMethod]
    public void ParseAddress_C2_ReturnsCounters()
    {
        // ============================================================================
        // Arrange / Act
        // ============================================================================
        var r = S7Frame.ParseAddress("C2");

        // ============================================================================
        // Assert
        // ============================================================================
        Assert.IsTrue(r.Success);
        Assert.AreEqual(S7Area.Counters, r.Value.area);
    }

    // 空地址必须失败
    [TestMethod]
    public void ParseAddress_Empty_Fails()
    {
        // ============================================================================
        // Arrange / Act
        // ============================================================================
        var r = S7Frame.ParseAddress("");

        // ============================================================================
        // Assert
        // ============================================================================
        Assert.IsFalse(r.Success);
    }

    // 无法识别的格式必须失败，不得默默落到某个区
    [TestMethod]
    public void ParseAddress_InvalidFormat_Fails()
    {
        // ============================================================================
        // Arrange / Act
        // ============================================================================
        var r = S7Frame.ParseAddress("UNKNOWN99");

        // ============================================================================
        // Assert
        // ============================================================================
        Assert.IsFalse(r.Success);
    }
}

// =============================================================================
// Siemens S7 — 帧构建基本校验
// =============================================================================

// COTP 连接、S7 Setup、Read Var 的字节布局是握手与读写的根基
[TestClass]
public class S7FrameBuilderTests
{
    // COTP CR 固定 22 字节：TPKT 版本 0x03、长度 22、PDU type 0xE0
    [TestMethod]
    public void BuildCotpConnectRequest_Length22_StartsWithTpktVersion()
    {
        // ============================================================================
        // Arrange / Act
        // ============================================================================
        // TPKT(4) + COTP CR(18) = 22 bytes
        byte[] frame = S7Frame.BuildCotpConnectRequest(0x0300);

        // ============================================================================
        // Assert
        // ============================================================================
        Assert.HasCount(22, frame);
        Assert.AreEqual(0x03, frame[0]); // TPKT version
        Assert.AreEqual(0x00, frame[1]); // TPKT reserved
        Assert.AreEqual(0x00, frame[2]); // Length Hi
        Assert.AreEqual(0x16, frame[3]); // Length Lo = 22
        Assert.AreEqual(0xE0, frame[5]); // COTP CR
    }

    // Setup Communication 功能码 0xF0 位于 S7 头之后（offset 17）
    [TestMethod]
    public void BuildSetupCommunication_ContainsSetupFunctionCode()
    {
        // ============================================================================
        // Arrange / Act
        // ============================================================================
        byte[] frame = S7Frame.BuildSetupCommunication();

        // ============================================================================
        // Assert
        // ============================================================================
        Assert.IsGreaterThan(19, frame.Length); // frame.Length > 19
        // S7Comm function: Setup Communication = 0xF0
        // TPKT(4) + COTP DT(3) + S7Header(10) = offset 17
        Assert.AreEqual(0xF0, frame[17]);
    }

    // Read Var：31 字节，功能码 0x04
    [TestMethod]
    public void BuildReadVar_DB10_0_4bytes_CorrectFrame()
    {
        // ============================================================================
        // Arrange / Act
        // ============================================================================
        byte[] frame = S7Frame.BuildReadVar(S7Area.DataBlock, 10, 0, 4);

        // ============================================================================
        // Assert
        // ============================================================================
        // TPKT(4) + COTP DT(3) + S7Header(10) + Param(14) = 31
        Assert.HasCount(31, frame);
        Assert.AreEqual(0x03, frame[0]); // TPKT version
        Assert.AreEqual(0x04, frame[17]); // S7 function: Read Var
    }

    // 响应过短必须失败，不得按偏移硬切出「数据」
    [TestMethod]
    public void ParseReadResponse_TooShort_ReturnsError()
    {
        // ============================================================================
        // Arrange / Act
        // ============================================================================
        var r = S7Frame.ParseReadResponse(new byte[10], 4);

        // ============================================================================
        // Assert
        // ============================================================================
        Assert.IsFalse(r.Success);
    }
}

// =============================================================================
// Panasonic Mewtocol — 地址解析
// =============================================================================

// MEWTOCOL 区前缀必须按文档：X/Y 是触点，DT 是数据寄存器
[TestClass]
public class MewtocolAddressTests
{
    // DT100 → DT 区、索引 100
    [TestMethod]
    public void Parse_DT100_ReturnsDtAreaIndex100()
    {
        // ============================================================================
        // Arrange / Act
        // ============================================================================
        var r = MewtocolAddress.Parse("DT100");

        // ============================================================================
        // Assert
        // ============================================================================
        Assert.IsTrue(r.Success, r.ErrorMessage);
        Assert.AreEqual(MewtocolArea.DT, r.Value.Area);
        Assert.AreEqual(100, r.Value.Index);
    }

    // 外部输入触点区前缀为 'X'，不是 'WX'
    [TestMethod]
    public void Parse_X0_ReturnsXArea()
    {
        // ============================================================================
        // Arrange / Act
        // ============================================================================
        // MEWTOCOL 外部输入触点区前缀为 'X'，不是 'WX'
        var r = MewtocolAddress.Parse("X0");

        // ============================================================================
        // Assert
        // ============================================================================
        Assert.IsTrue(r.Success, r.ErrorMessage);
        Assert.AreEqual(MewtocolArea.X, r.Value.Area);
    }

    // 外部输出触点区前缀为 'Y'
    [TestMethod]
    public void Parse_Y10_ReturnsYAreaIndex10()
    {
        // ============================================================================
        // Arrange / Act
        // ============================================================================
        // 外部输出触点区前缀为 'Y'
        var r = MewtocolAddress.Parse("Y10");

        // ============================================================================
        // Assert
        // ============================================================================
        Assert.IsTrue(r.Success, r.ErrorMessage);
        Assert.AreEqual(MewtocolArea.Y, r.Value.Area);
        Assert.AreEqual(10, r.Value.Index);
    }

    // 空地址必须失败
    [TestMethod]
    public void Parse_Empty_Fails()
    {
        // ============================================================================
        // Arrange / Act
        // ============================================================================
        var r = MewtocolAddress.Parse("");

        // ============================================================================
        // Assert
        // ============================================================================
        Assert.IsFalse(r.Success);
    }
}

// =============================================================================
// Plugin Manifest & Factory — 元数据契约
// =============================================================================

// PluginId / ProtocolId / ApiVersion 是宿主匹配工厂的唯一依据
[TestClass]
public class PluginManifestTests
{
    // Modbus TCP 清单：ApiVersion=1，PluginId 为 kebab-case
    [TestMethod]
    public void ModbusTcpManifest_ApiVersion1_PluginIdCorrect()
    {
        // ============================================================================
        // Arrange / Act
        // ============================================================================
        var manifest = new ModbusTcpPluginManifest();

        // ============================================================================
        // Assert
        // ============================================================================
        Assert.AreEqual(1, manifest.Descriptor.ApiVersion);
        Assert.AreEqual("modbus-tcp", manifest.Descriptor.PluginId);
    }

    // Modbus RTU 清单
    [TestMethod]
    public void ModbusRtuManifest_ApiVersion1_PluginIdCorrect()
    {
        // ============================================================================
        // Arrange / Act
        // ============================================================================
        var manifest = new ModbusRtuPluginManifest();

        // ============================================================================
        // Assert
        // ============================================================================
        Assert.AreEqual(1, manifest.Descriptor.ApiVersion);
        Assert.AreEqual("modbus-rtu", manifest.Descriptor.PluginId);
    }

    // Modbus ASCII 清单
    [TestMethod]
    public void ModbusAsciiManifest_ApiVersion1_PluginIdCorrect()
    {
        // ============================================================================
        // Arrange / Act
        // ============================================================================
        var manifest = new ModbusAsciiPluginManifest();

        // ============================================================================
        // Assert
        // ============================================================================
        Assert.AreEqual(1, manifest.Descriptor.ApiVersion);
        Assert.AreEqual("modbus-ascii", manifest.Descriptor.PluginId);
    }

    // S7-1200 工厂的 ProtocolId 与插件 API 版本
    [TestMethod]
    public void SiemensS7_1200Factory_ProtocolIdAndApiVersionCorrect()
    {
        // ============================================================================
        // Arrange / Act
        // ============================================================================
        var factory = new SiemensS7_1200ProtocolDriverFactory();

        // ============================================================================
        // Assert
        // ============================================================================
        Assert.AreEqual("siemens-s7-1200", factory.Metadata.ProtocolId);
        Assert.AreEqual(1, factory.Metadata.PluginApiVersion);
    }

    // S7-200 Smart 不得与 1200 共用同一个 ProtocolId
    [TestMethod]
    public void SiemensS7_200SmartFactory_ProtocolIdCorrect()
    {
        // ============================================================================
        // Arrange / Act
        // ============================================================================
        var factory = new SiemensS7_200SmartProtocolDriverFactory();

        // ============================================================================
        // Assert
        // ============================================================================
        Assert.AreEqual("siemens-s7-200smart", factory.Metadata.ProtocolId);
    }

    // MEWTOCOL 清单：单一 ProtocolId 覆盖 TCP 与串口
    [TestMethod]
    public void MewtocolManifest_ApiVersion1_PluginIdCorrect()
    {
        // ============================================================================
        // Arrange / Act
        // ============================================================================
        var manifest = new MewtocolTcpPluginManifest();

        // ============================================================================
        // Assert
        // ============================================================================
        Assert.AreEqual(1, manifest.Descriptor.ApiVersion);
        Assert.AreEqual("panasonic-mewtocol", manifest.Descriptor.PluginId);
    }

    // 工厂 CreateDriver 必须返回带正确 ProtocolId 的驱动实例
    [TestMethod]
    public void ModbusRtuFactory_CreateDriver_ReturnsDriverWithCorrectProtocolId()
    {
        // ============================================================================
        // Arrange
        // ============================================================================
        var factory = new ModbusRtuProtocolDriverFactory();

        // ============================================================================
        // Act
        // ============================================================================
        var driver = factory.CreateDriver();

        // ============================================================================
        // Assert
        // ============================================================================
        Assert.IsNotNull(driver);
        Assert.AreEqual("modbus-rtu", driver.Metadata.ProtocolId);
    }

    // ASCII 工厂同样必须能实例化
    [TestMethod]
    public void ModbusAsciiFactory_CreateDriver_ReturnsNonNull()
    {
        // ============================================================================
        // Arrange
        // ============================================================================
        var factory = new ModbusAsciiProtocolDriverFactory();

        // ============================================================================
        // Act
        // ============================================================================
        var driver = factory.CreateDriver();

        // ============================================================================
        // Assert
        // ============================================================================
        Assert.IsNotNull(driver);
        Assert.AreEqual("modbus-ascii", driver.Metadata.ProtocolId);
    }
}
