// -----------------------------------------------------------------------------
// 文件: DeviceStationRoutingTests.cs
// 层级: Tests
// 作用: 验证「站号来自设备级配置」这条链路的正确性。
// 背景:
//   历史实现中站号只能通过地址前缀书写（如 "01:DT100"），设备表单里的站号字段
//   从未传到协议驱动。修复后站号在路由装配时注入驱动，地址可保持干净（"DT100"），
//   前缀降级为 RS-485 一主多从场景下的可选逐变量覆盖。
// 覆盖:
//   1) 设备级站号 → 驱动默认站号（无前缀地址生效）
//   2) 地址前缀仍可覆盖设备级站号
//   3) 站号缺失 / 非法时安全回落，不使路由不可用
//   4) 协议元信息正确声明传输介质与站号需求（UI 据此渲染表单）
// -----------------------------------------------------------------------------

using CommunicationKernel.Communication.Protocol.Abstractions;
using CommunicationKernel.Communication.Transport.Abstractions;

using CommunicationKernel.Plugins.Modbus.Tcp;
using CommunicationKernel.Plugins.Modbus.Rtu;
using CommunicationKernel.Plugins.Modbus.Ascii;
using CommunicationKernel.Plugins.Siemens.S7;
using CommunicationKernel.Plugins.Panasonic.MewtocolTcp;

using ModbusTcpAddress   = CommunicationKernel.Plugins.Modbus.Tcp.Internal.ModbusAddress;
using ModbusRtuAddress   = CommunicationKernel.Plugins.Modbus.Rtu.Internal.ModbusRtuAddress;
using ModbusAsciiAddress = CommunicationKernel.Plugins.Modbus.Ascii.Internal.ModbusAsciiAddress;
using MewtocolAddress    = CommunicationKernel.Plugins.Panasonic.MewtocolTcp.Internal.MewtocolAddress;

namespace CommunicationKernel.Tests;

// =============================================================================
// 设备级站号 → 驱动默认站号
// =============================================================================

[TestClass]
public class DefaultStationResolutionTests
{
    [TestMethod]
    public void Mewtocol_CleanAddress_UsesDeviceStation()
    {
        // 设备表单填了站号 5，变量地址保持干净的 "DT100"
        var parsed = MewtocolAddress.Parse("DT100", defaultStation: 5);

        Assert.IsTrue(parsed.Success);
        Assert.AreEqual((byte)5, parsed.Value.Station,
            "设备级站号应作为默认站号生效，无需在地址中书写 '05:' 前缀");
    }

    [TestMethod]
    public void Mewtocol_AddressPrefix_OverridesDeviceStation()
    {
        // RS-485 一主多从：同一路由下个别变量指向别的站
        var parsed = MewtocolAddress.Parse("07:DT100", defaultStation: 5);

        Assert.IsTrue(parsed.Success);
        Assert.AreEqual((byte)7, parsed.Value.Station,
            "地址前缀应覆盖设备级站号");
    }

    [TestMethod]
    public void ModbusTcp_CleanAddress_UsesDeviceUnitId()
    {
        var parsed = ModbusTcpAddress.Parse("40001", defaultUnitId: 9);

        Assert.IsTrue(parsed.Success);
        Assert.AreEqual((byte)9, parsed.Value.UnitId);
    }

    [TestMethod]
    public void ModbusTcp_AddressPrefix_OverridesDeviceUnitId()
    {
        var parsed = ModbusTcpAddress.Parse("3:40001", defaultUnitId: 9);

        Assert.IsTrue(parsed.Success);
        Assert.AreEqual((byte)3, parsed.Value.UnitId);
    }

    [TestMethod]
    public void ModbusRtu_CleanAddress_UsesDeviceSlaveId()
    {
        var parsed = ModbusRtuAddress.Parse("40001", defaultSlaveId: 11);

        Assert.IsTrue(parsed.Success);
        Assert.AreEqual((byte)11, parsed.Value.SlaveId);
    }

    [TestMethod]
    public void ModbusAscii_CleanAddress_UsesDeviceSlaveId()
    {
        var parsed = ModbusAsciiAddress.Parse("40001", defaultSlaveId: 12);

        Assert.IsTrue(parsed.Success);
        Assert.AreEqual((byte)12, parsed.Value.SlaveId);
    }

    [TestMethod]
    public void Parse_WithoutExplicitDefault_KeepsLegacyStationOne()
    {
        // 未传默认站号时行为与历史一致，保证既有调用方不受影响
        Assert.AreEqual((byte)1, MewtocolAddress.Parse("DT100").Value.Station);
        Assert.AreEqual((byte)1, ModbusTcpAddress.Parse("40001").Value.UnitId);
    }
}

// =============================================================================
// 站号原文 → 默认站号的安全回落
// =============================================================================

[TestClass]
public class StationFallbackTests
{
    [TestMethod]
    public void Mewtocol_NullOrEmptyStation_FallsBackToOne()
    {
        Assert.AreEqual((byte)1, MewtocolAddress.ResolveDefaultStation(null));
        Assert.AreEqual((byte)1, MewtocolAddress.ResolveDefaultStation(""));
        Assert.AreEqual((byte)1, MewtocolAddress.ResolveDefaultStation("   "));
    }

    [TestMethod]
    public void Mewtocol_OutOfRangeStation_FallsBackToOne()
    {
        // MEWTOCOL 有效站号 1-99，越界值不应使整条路由不可用
        Assert.AreEqual((byte)1, MewtocolAddress.ResolveDefaultStation("0"));
        Assert.AreEqual((byte)1, MewtocolAddress.ResolveDefaultStation("100"));
        Assert.AreEqual((byte)1, MewtocolAddress.ResolveDefaultStation("abc"));
    }

    [TestMethod]
    public void Mewtocol_ValidStation_IsParsed()
    {
        Assert.AreEqual((byte)1,  MewtocolAddress.ResolveDefaultStation("1"));
        Assert.AreEqual((byte)42, MewtocolAddress.ResolveDefaultStation("42"));
        Assert.AreEqual((byte)99, MewtocolAddress.ResolveDefaultStation(" 99 "));
    }

    [TestMethod]
    public void Modbus_OutOfRangeUnitId_FallsBackToOne()
    {
        // Modbus 有效从站地址 1-247，248-255 为保留值
        Assert.AreEqual((byte)1, ModbusTcpAddress.ResolveDefaultUnitId("0"));
        Assert.AreEqual((byte)1, ModbusTcpAddress.ResolveDefaultUnitId("248"));
        Assert.AreEqual((byte)1, ModbusTcpAddress.ResolveDefaultUnitId("999"));
        Assert.AreEqual((byte)247, ModbusTcpAddress.ResolveDefaultUnitId("247"));
    }

    [TestMethod]
    public void ModbusSerial_ResolveDefaultSlaveId_MatchesTcpSemantics()
    {
        Assert.AreEqual((byte)1,  ModbusRtuAddress.ResolveDefaultSlaveId(null));
        Assert.AreEqual((byte)17, ModbusRtuAddress.ResolveDefaultSlaveId("17"));
        Assert.AreEqual((byte)1,  ModbusAsciiAddress.ResolveDefaultSlaveId("300"));
        Assert.AreEqual((byte)17, ModbusAsciiAddress.ResolveDefaultSlaveId("17"));
    }
}

// =============================================================================
// 驱动工厂：站号经 ProtocolDriverContext 注入
// =============================================================================

[TestClass]
public class ProtocolDriverContextTests
{
    [TestMethod]
    public void MewtocolFactory_StationFromContext_AppliedToCleanAddress()
    {
        var factory = new MewtocolTcpProtocolDriverFactory();
        IProtocolDriver driver = factory.CreateDriver(
            new ProtocolDriverContext { Station = "23" });

        // 干净地址构建出的帧应携带设备级站号 23（十六进制 "17"）
        var frame = driver.BuildReadFrame("DT100", 2);

        Assert.IsTrue(frame.Success);
        string text = System.Text.Encoding.ASCII.GetString(frame.Value);
        StringAssert.StartsWith(text, "%17#",
            "MEWTOCOL 帧头应为 '%' + 两位十六进制站号，23 → 0x17");
    }

    [TestMethod]
    public void MewtocolFactory_NullContext_UsesStationOne()
    {
        var driver = new MewtocolTcpProtocolDriverFactory().CreateDriver(null);
        var frame  = driver.BuildReadFrame("DT100", 2);

        Assert.IsTrue(frame.Success);
        string text = System.Text.Encoding.ASCII.GetString(frame.Value);
        StringAssert.StartsWith(text, "%01#");
    }

    [TestMethod]
    public void ModbusTcpFactory_StationFromContext_AppliedToUnitIdByte()
    {
        var driver = new ModbusTcpProtocolDriverFactory().CreateDriver(
            new ProtocolDriverContext { Station = "6" });

        var frame = driver.BuildReadFrame("40001", 2);

        Assert.IsTrue(frame.Success);
        // MBAP 头第 6 字节（0 基）为 Unit ID
        Assert.AreEqual((byte)6, frame.Value[6]);
    }

    [TestMethod]
    public void ModbusTcpFactory_EmptyStation_FallsBackToUnitIdOne()
    {
        var driver = new ModbusTcpProtocolDriverFactory().CreateDriver(
            new ProtocolDriverContext { Station = "" });

        var frame = driver.BuildReadFrame("40001", 2);

        Assert.IsTrue(frame.Success);
        Assert.AreEqual((byte)1, frame.Value[6]);
    }
}

// =============================================================================
// 协议元信息：UI 渲染设备表单的唯一依据
// =============================================================================

[TestClass]
public class ProtocolMetadataContractTests
{
    [TestMethod]
    public void SerialProtocols_DeclareSerialTransport()
    {
        Assert.AreEqual(TransportKind.Serial,
            new ModbusRtuProtocolDriverFactory().Metadata.TransportKind);
        Assert.AreEqual(TransportKind.Serial,
            new ModbusAsciiProtocolDriverFactory().Metadata.TransportKind);
        Assert.AreEqual(TransportKind.Serial,
            new MewtocolSerialProtocolDriverFactory().Metadata.TransportKind);
    }

    [TestMethod]
    public void MewtocolSerial_HasDistinctProtocolIdFromTcpVariant()
    {
        string tcpId    = new MewtocolTcpProtocolDriverFactory().Metadata.ProtocolId;
        string serialId = new MewtocolSerialProtocolDriverFactory().Metadata.ProtocolId;

        Assert.AreEqual("panasonic-mewtocol-tcp", tcpId);
        Assert.AreEqual("panasonic-mewtocol-serial", serialId);
        Assert.AreNotEqual(tcpId, serialId,
            "两种介质必须是不同的 ProtocolId，否则装配时无法区分");
    }

    [TestMethod]
    public void MewtocolSerial_ProducesIdenticalFramesToTcpVariant()
    {
        // MEWTOCOL-COM 的帧格式与介质无关：同站号同地址下两者必须逐字节相同。
        // 这正是「同一驱动可同时服务 TCP 与串口」的前提。
        var ctx = new ProtocolDriverContext { Station = "9" };

        var tcpFrame = new MewtocolTcpProtocolDriverFactory()
            .CreateDriver(ctx).BuildReadFrame("DT100", 2);
        var serialFrame = new MewtocolSerialProtocolDriverFactory()
            .CreateDriver(ctx).BuildReadFrame("DT100", 2);

        Assert.IsTrue(tcpFrame.Success);
        Assert.IsTrue(serialFrame.Success);
        CollectionAssert.AreEqual(tcpFrame.Value, serialFrame.Value);
    }

    [TestMethod]
    public void MewtocolSerial_AppliesDeviceStation()
    {
        var driver = new MewtocolSerialProtocolDriverFactory()
            .CreateDriver(new ProtocolDriverContext { Station = "23" });

        var frame = driver.BuildReadFrame("DT100", 2);

        Assert.IsTrue(frame.Success);
        string text = System.Text.Encoding.ASCII.GetString(frame.Value);
        StringAssert.StartsWith(text, "%17#", "站号 23 → 十六进制 0x17");
    }

    [TestMethod]
    public void TcpProtocols_DeclareTcpTransport()
    {
        Assert.AreEqual(TransportKind.Tcp,
            new ModbusTcpProtocolDriverFactory().Metadata.TransportKind);
        Assert.AreEqual(TransportKind.Tcp,
            new MewtocolTcpProtocolDriverFactory().Metadata.TransportKind);
        Assert.AreEqual(TransportKind.Tcp,
            new SiemensS7_1200ProtocolDriverFactory().Metadata.TransportKind);
    }

    [TestMethod]
    public void StationBasedProtocols_RequireStation()
    {
        Assert.IsTrue(new ModbusTcpProtocolDriverFactory().Metadata.RequiresStation);
        Assert.IsTrue(new ModbusRtuProtocolDriverFactory().Metadata.RequiresStation);
        Assert.IsTrue(new MewtocolTcpProtocolDriverFactory().Metadata.RequiresStation);
    }

    [TestMethod]
    public void S7_DoesNotRequireStation_RackSlotFixedInTsap()
    {
        Assert.IsFalse(new SiemensS7_1200ProtocolDriverFactory().Metadata.RequiresStation);
        Assert.IsFalse(new SiemensS7_200SmartProtocolDriverFactory().Metadata.RequiresStation);
    }

    [TestMethod]
    public void ProtocolsRequiringStation_ProvideHintText()
    {
        // UI 直接展示该文案，缺失会导致操作员不知道填什么范围
        Assert.IsFalse(string.IsNullOrWhiteSpace(
            new ModbusTcpProtocolDriverFactory().Metadata.StationHint));
        Assert.IsFalse(string.IsNullOrWhiteSpace(
            new MewtocolTcpProtocolDriverFactory().Metadata.StationHint));
    }

    [TestMethod]
    public void AllProtocolIds_AreLowerKebabCase_NotDisplayNames()
    {
        // 防回归：UI 曾把展示名当 ProtocolId 回传，导致服务端匹配不到协议工厂
        string[] ids = {
            new ModbusTcpProtocolDriverFactory().Metadata.ProtocolId,
            new ModbusRtuProtocolDriverFactory().Metadata.ProtocolId,
            new ModbusAsciiProtocolDriverFactory().Metadata.ProtocolId,
            new MewtocolTcpProtocolDriverFactory().Metadata.ProtocolId,
            new MewtocolSerialProtocolDriverFactory().Metadata.ProtocolId,
            new SiemensS7_1200ProtocolDriverFactory().Metadata.ProtocolId,
            new SiemensS7_200SmartProtocolDriverFactory().Metadata.ProtocolId,
        };

        foreach (string id in ids)
        {
            Assert.IsFalse(string.IsNullOrWhiteSpace(id));
            Assert.DoesNotContain(" ", id, $"ProtocolId 不应含空格: {id}");
            Assert.AreEqual(id.ToLowerInvariant(), id, $"ProtocolId 应为小写: {id}");
        }
    }
}
