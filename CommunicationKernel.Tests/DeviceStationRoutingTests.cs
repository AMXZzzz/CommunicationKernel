// -----------------------------------------------------------------------------
// 文件: DeviceStationRoutingTests.cs
// 层级: 测试
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

using CommunicationKernel.Core.Protocol.Abstractions;
using CommunicationKernel.Core.Transport.Abstractions;

using CommunicationKernel.Plugins.Protocol.Modbus.Tcp;
using CommunicationKernel.Plugins.Protocol.Modbus.Rtu;
using CommunicationKernel.Plugins.Protocol.Modbus.Ascii;
using CommunicationKernel.Plugins.Protocol.Siemens.S7;
using CommunicationKernel.Plugins.Protocol.Panasonic;

// Modbus 三种变体的地址语义已收敛到共享 Core，不再各持一份
using ModbusSharedAddress = CommunicationKernel.Plugins.Protocol.Modbus.Core.ModbusAddress;
using MewtocolAddress     = CommunicationKernel.Plugins.Protocol.Panasonic.Internal.MewtocolAddress;

namespace CommunicationKernel.Tests;

// =============================================================================
// 设备级站号 → 驱动默认站号
// =============================================================================

// 干净地址必须吃到设备表单里填的站号，无需再写 "05:" 前缀
[TestClass]
public class DefaultStationResolutionTests
{
    // MEWTOCOL：设备站号 5 + 干净地址 DT100 → 实际站号 5
    [TestMethod]
    public void Mewtocol_CleanAddress_UsesDeviceStation()
    {
        // ============================================================================
        // Arrange / Act
        // ============================================================================
        // 设备表单填了站号 5，变量地址保持干净的 "DT100"
        var parsed = MewtocolAddress.Parse("DT100", defaultStation: 5);

        // ============================================================================
        // Assert
        // ============================================================================
        Assert.IsTrue(parsed.Success);
        Assert.AreEqual((byte)5, parsed.Value.Station,
            "设备级站号应作为默认站号生效，无需在地址中书写 '05:' 前缀");
    }

    // 站号前缀已废弃：必须明确拒绝，不能静默当成别的地址
    [TestMethod]
    public void Mewtocol_AddressPrefix_IsRejected()
    {
        // ============================================================================
        // Arrange / Act
        // ============================================================================
        // 曾支持 "07:DT100" 覆盖设备级站号。现已禁止——站号是 RouteKey 的组成部分，
        // 让地址覆盖它会使同一条路由悄悄读写两个物理站，
        // 而路由表、状态灯与串口帧间静默全都只认一个。
        var parsed = MewtocolAddress.Parse("07:DT100", defaultStation: 5);

        // ============================================================================
        // Assert
        // ============================================================================
        Assert.IsFalse(parsed.Success,
            "站号前缀必须被拒绝，而不是静默忽略——静默改变读写目标比报错危险得多");
        StringAssert.Contains(parsed.ErrorMessage, "站号",
            "错误信息必须指出问题在站号，并指引到设备配置");
    }

    // 干净地址在禁用前缀后仍必须正常吃到设备级站号
    [TestMethod]
    public void Mewtocol_CleanAddress_StillUsesDeviceStation()
    {
        // ============================================================================
        // Arrange / Act
        // ============================================================================
        var parsed = MewtocolAddress.Parse("DT100", defaultStation: 5);

        // ============================================================================
        // Assert
        // ============================================================================
        Assert.IsTrue(parsed.Success);
        Assert.AreEqual((byte)5, parsed.Value.Station);
    }

    // Modbus 三种变体共用同一份解析，干净地址吃到设备 UnitId
    [TestMethod]
    public void Modbus_CleanAddress_UsesDeviceUnitId()
    {
        // ============================================================================
        // Arrange / Act
        // ============================================================================
        // 三种 Modbus 变体共用同一份地址解析，一次断言即覆盖全部
        var parsed = ModbusSharedAddress.Parse("40001", defaultUnitId: 9);

        // ============================================================================
        // Assert
        // ============================================================================
        Assert.IsTrue(parsed.Success);
        Assert.AreEqual((byte)9, parsed.Value.UnitId);
    }

    // 站号前缀已废弃：必须明确拒绝
    [TestMethod]
    public void Modbus_AddressPrefix_IsRejected()
    {
        // ============================================================================
        // Arrange / Act
        // ============================================================================
        // 曾支持 "3:40001" 覆盖设备 UnitId。现已禁止——理由同 Mewtocol，
        // 另有一条：写串行化与串口帧间静默按 RouteKey 归组，
        // 地址里换站号等于让这些变量跳出调度组，在共享 RS-485 上直接制造帧冲突。
        var parsed = ModbusSharedAddress.Parse("3:40001", defaultUnitId: 9);

        // ============================================================================
        // Assert
        // ============================================================================
        Assert.IsFalse(parsed.Success,
            "站号前缀必须被拒绝，而不是静默按 40001 解析");
        StringAssert.Contains(parsed.ErrorMessage, "站号",
            "错误信息必须指出问题在站号，并指引到设备配置");
    }

    // 未传默认站号时保持历史行为（站号 1），既有调用方不受影响
    [TestMethod]
    public void Parse_WithoutExplicitDefault_KeepsLegacyStationOne()
    {
        // ============================================================================
        // Assert
        // ============================================================================
        // 未传默认站号时行为与历史一致，保证既有调用方不受影响
        Assert.AreEqual((byte)1, MewtocolAddress.Parse("DT100").Value.Station);
        Assert.AreEqual((byte)1, ModbusSharedAddress.Parse("40001").Value.UnitId);
    }
}

// =============================================================================
// 站号原文 → 默认站号的安全回落
// =============================================================================

// 非法/越界站号必须回落到 1，不得让整条路由注册失败
[TestClass]
public class StationFallbackTests
{
    // null / 空 / 空白一律回落到站号 1
    [TestMethod]
    public void Mewtocol_NullOrEmptyStation_FallsBackToOne()
    {
        // ============================================================================
        // Assert
        // ============================================================================
        Assert.AreEqual((byte)1, MewtocolAddress.ResolveDefaultStation(null));
        Assert.AreEqual((byte)1, MewtocolAddress.ResolveDefaultStation(""));
        Assert.AreEqual((byte)1, MewtocolAddress.ResolveDefaultStation("   "));
    }

    // MEWTOCOL 有效站号 1-99，越界与非数字都回落，路由仍可用
    [TestMethod]
    public void Mewtocol_OutOfRangeStation_FallsBackToOne()
    {
        // ============================================================================
        // Assert
        // ============================================================================
        // MEWTOCOL 有效站号 1-99，越界值不应使整条路由不可用
        Assert.AreEqual((byte)1, MewtocolAddress.ResolveDefaultStation("0"));
        Assert.AreEqual((byte)1, MewtocolAddress.ResolveDefaultStation("100"));
        Assert.AreEqual((byte)1, MewtocolAddress.ResolveDefaultStation("abc"));
    }

    // 合法站号（含首尾空白）必须按数字解析
    [TestMethod]
    public void Mewtocol_ValidStation_IsParsed()
    {
        // ============================================================================
        // Assert
        // ============================================================================
        Assert.AreEqual((byte)1,  MewtocolAddress.ResolveDefaultStation("1"));
        Assert.AreEqual((byte)42, MewtocolAddress.ResolveDefaultStation("42"));
        Assert.AreEqual((byte)99, MewtocolAddress.ResolveDefaultStation(" 99 "));
    }

    // Modbus 从站 1-247；248-255 为保留值，越界回落到 1
    [TestMethod]
    public void Modbus_OutOfRangeUnitId_FallsBackToOne()
    {
        // ============================================================================
        // Assert
        // ============================================================================
        // Modbus 有效从站地址 1-247，248-255 为保留值。
        // 三种变体共用同一份解析，不再存在"只在其中一份修好"的漂移风险。
        Assert.AreEqual((byte)1, ModbusSharedAddress.ResolveDefaultUnitId("0"));
        Assert.AreEqual((byte)1, ModbusSharedAddress.ResolveDefaultUnitId("248"));
        Assert.AreEqual((byte)1, ModbusSharedAddress.ResolveDefaultUnitId("999"));
        Assert.AreEqual((byte)247, ModbusSharedAddress.ResolveDefaultUnitId("247"));
    }
}

// =============================================================================
// 驱动工厂：站号经 ProtocolDriverContext 注入
// =============================================================================

// 工厂必须把 Context.Station 写进实际发出的帧，而不是只存在于配置对象里
[TestClass]
public class ProtocolDriverContextTests
{
    // MEWTOCOL 帧头 '%' + 两位十六进制站号：23 → 0x17
    [TestMethod]
    public void MewtocolFactory_StationFromContext_AppliedToCleanAddress()
    {
        // ============================================================================
        // Arrange
        // ============================================================================
        var factory = new MewtocolProtocolDriverFactory();
        IProtocolDriver driver = factory.CreateDriver(
            new ProtocolDriverContext { Station = "23" });

        // ============================================================================
        // Act
        // ============================================================================
        // 干净地址构建出的帧应携带设备级站号 23（十六进制 "17"）
        var frame = driver.BuildReadFrame("DT100", 2);

        // ============================================================================
        // Assert
        // ============================================================================
        Assert.IsTrue(frame.Success);
        string text = System.Text.Encoding.ASCII.GetString(frame.Value);
        StringAssert.StartsWith(text, "%17#",
            "MEWTOCOL 帧头应为 '%' + 两位十六进制站号，23 → 0x17");
    }

    // 未传 Context 时回落到站号 1，帧头为 %01#
    [TestMethod]
    public void MewtocolFactory_NullContext_UsesStationOne()
    {
        // ============================================================================
        // Arrange
        // ============================================================================
        var driver = new MewtocolProtocolDriverFactory().CreateDriver(null);

        // ============================================================================
        // Act
        // ============================================================================
        var frame  = driver.BuildReadFrame("DT100", 2);

        // ============================================================================
        // Assert
        // ============================================================================
        Assert.IsTrue(frame.Success);
        string text = System.Text.Encoding.ASCII.GetString(frame.Value);
        StringAssert.StartsWith(text, "%01#");
    }

    // Modbus TCP：设备站号写入 MBAP 第 6 字节（Unit ID）
    [TestMethod]
    public void ModbusTcpFactory_StationFromContext_AppliedToUnitIdByte()
    {
        // ============================================================================
        // Arrange
        // ============================================================================
        var driver = new ModbusTcpProtocolDriverFactory().CreateDriver(
            new ProtocolDriverContext { Station = "6" });

        // ============================================================================
        // Act
        // ============================================================================
        var frame = driver.BuildReadFrame("40001", 2);

        // ============================================================================
        // Assert
        // ============================================================================
        Assert.IsTrue(frame.Success);
        // MBAP 头第 6 字节（0 基）为 Unit ID
        Assert.AreEqual((byte)6, frame.Value[6]);
    }

    // 空站号回落到 Unit ID 1，不得发出 Unit ID 0（广播）
    [TestMethod]
    public void ModbusTcpFactory_EmptyStation_FallsBackToUnitIdOne()
    {
        // ============================================================================
        // Arrange
        // ============================================================================
        var driver = new ModbusTcpProtocolDriverFactory().CreateDriver(
            new ProtocolDriverContext { Station = "" });

        // ============================================================================
        // Act
        // ============================================================================
        var frame = driver.BuildReadFrame("40001", 2);

        // ============================================================================
        // Assert
        // ============================================================================
        Assert.IsTrue(frame.Success);
        Assert.AreEqual((byte)1, frame.Value[6]);
    }
}

// =============================================================================
// 协议元信息：UI 渲染设备表单的唯一依据
// =============================================================================

// UI 完全按元信息渲染：介质清单、站号是否必填、ProtocolId 格式都必须正确
[TestClass]
public class ProtocolMetadataContractTests
{
    // 能跑在串口上的协议必须声明 Serial，否则 UI 不会给出串口选项
    [TestMethod]
    public void SerialCapableProtocols_DeclareSerialSupport()
    {
        // ============================================================================
        // Assert
        // ============================================================================
        CollectionAssert.Contains(
            new ModbusRtuProtocolDriverFactory().Metadata.SupportedTransports.ToArray(),
            TransportKind.Serial);
        CollectionAssert.Contains(
            new ModbusAsciiProtocolDriverFactory().Metadata.SupportedTransports.ToArray(),
            TransportKind.Serial);
        CollectionAssert.Contains(
            new MewtocolProtocolDriverFactory().Metadata.SupportedTransports.ToArray(),
            TransportKind.Serial);
    }

    // RTU/ASCII/MEWTOCOL 也必须支持 TCP：现场常见串口服务器透传
    [TestMethod]
    public void RtuAndAscii_AlsoSupportTcp_ForSerialOverEthernetGateways()
    {
        // ============================================================================
        // Assert
        // ============================================================================
        // 现场常见接法：Modbus RTU 经 TCP 转串口透传装置（Moxa NPort、USR-TCP232 等）
        // 接入以太网。若把协议锁死为单一介质，这类设备将完全无法接入。
        CollectionAssert.Contains(
            new ModbusRtuProtocolDriverFactory().Metadata.SupportedTransports.ToArray(),
            TransportKind.Tcp);
        CollectionAssert.Contains(
            new ModbusAsciiProtocolDriverFactory().Metadata.SupportedTransports.ToArray(),
            TransportKind.Tcp);
        CollectionAssert.Contains(
            new MewtocolProtocolDriverFactory().Metadata.SupportedTransports.ToArray(),
            TransportKind.Tcp);
    }

    // 防回归：读 1 字节 DT 不得因 length==1 被转成 RCS 触点读
    [TestMethod]
    public void Mewtocol_SingleByteReadOnWordAddress_StaysDataRead()
    {
        // ============================================================================
        // Arrange
        // ============================================================================
        // 防回归：历史实现写作 (addr.IsBit || length == 1)，
        // 读 1 字节的 DT 字寄存器会被转成 RCS 单点读，
        // 返回完全不同数据区的位值且全程 Success = true。
        // 与 Modbus 侧曾经的 "|| length == 1" 是同一缺陷模式。
        var driver = new MewtocolProtocolDriverFactory()
            .CreateDriver(new ProtocolDriverContext { Station = "1" });

        // ============================================================================
        // Act
        // ============================================================================
        var frame = driver.BuildReadFrame("DT100", 1);

        // ============================================================================
        // Assert
        // ============================================================================
        Assert.IsTrue(frame.Success);
        string text = System.Text.Encoding.ASCII.GetString(frame.Value);
        StringAssert.Contains(text, "#RD",
            "DT 是字地址，无论读几个字节都必须走 RD 读数据寄存器，不能因长度为 1 转成 RCS");
        Assert.DoesNotContain("#RCS", text);
    }

    // 位地址不论请求多少字节都走 RCS——数据区只由地址决定
    [TestMethod]
    public void Mewtocol_BitAddress_AlwaysUsesContactRead()
    {
        // ============================================================================
        // Arrange
        // ============================================================================
        var driver = new MewtocolProtocolDriverFactory()
            .CreateDriver(new ProtocolDriverContext { Station = "1" });

        // ============================================================================
        // Act / Assert
        // ============================================================================
        // 位地址不论请求多少字节都走 RCS——数据区只由地址决定
        foreach (int len in new[] { 1, 2, 8 })
        {
            var frame = driver.BuildReadFrame("X0", len);
            Assert.IsTrue(frame.Success);
            StringAssert.Contains(System.Text.Encoding.ASCII.GetString(frame.Value), "#RCS");
        }
    }

    // length<=0 必须拒绝，不得发出读 0 个的帧
    [TestMethod]
    public void Mewtocol_NonPositiveLength_IsRejected()
    {
        // ============================================================================
        // Arrange
        // ============================================================================
        var driver = new MewtocolProtocolDriverFactory().CreateDriver(null);

        // ============================================================================
        // Assert
        // ============================================================================
        Assert.IsFalse(driver.BuildReadFrame("DT100", 0).Success);
        Assert.IsFalse(driver.BuildReadFrame("DT100", -1).Success);
    }

    // 帧格式与介质无关，只应有一个 ProtocolId，拆成 -tcp/-serial 会让同一协议出现两份元信息
    [TestMethod]
    public void Mewtocol_IsSingleProtocolAcrossBothTransports()
    {
        // ============================================================================
        // Arrange
        // ============================================================================
        // 帧格式与介质无关，因此只应存在一个 ProtocolId，
        // 拆成 -tcp / -serial 会让同一协议出现两份元信息
        ProtocolMetadata meta = new MewtocolProtocolDriverFactory().Metadata;

        // ============================================================================
        // Assert
        // ============================================================================
        Assert.AreEqual("panasonic-mewtocol", meta.ProtocolId);
        Assert.HasCount(2, meta.SupportedTransports);
    }

    // MBAP 与 TPKT/COTP 依赖 TCP 可靠流，不得声明串口
    [TestMethod]
    public void TcpOnlyProtocols_DoNotDeclareSerial()
    {
        // ============================================================================
        // Assert
        // ============================================================================
        // MBAP 与 TPKT/COTP 均依赖 TCP 的可靠有序流，无串口对应形式
        CollectionAssert.DoesNotContain(
            new ModbusTcpProtocolDriverFactory().Metadata.SupportedTransports.ToArray(),
            TransportKind.Serial);
        CollectionAssert.DoesNotContain(
            new SiemensS7_1200ProtocolDriverFactory().Metadata.SupportedTransports.ToArray(),
            TransportKind.Serial);
    }

    // 站号型协议必须声明 RequiresStation，UI 才会渲染站号输入框
    [TestMethod]
    public void StationBasedProtocols_RequireStation()
    {
        // ============================================================================
        // Assert
        // ============================================================================
        Assert.IsTrue(new ModbusTcpProtocolDriverFactory().Metadata.RequiresStation);
        Assert.IsTrue(new ModbusRtuProtocolDriverFactory().Metadata.RequiresStation);
        Assert.IsTrue(new MewtocolProtocolDriverFactory().Metadata.RequiresStation);
    }

    // S7 站号已固化在 TSAP 里，不得再要操作员填站号
    [TestMethod]
    public void S7_DoesNotRequireStation_RackSlotFixedInTsap()
    {
        // ============================================================================
        // Assert
        // ============================================================================
        Assert.IsFalse(new SiemensS7_1200ProtocolDriverFactory().Metadata.RequiresStation);
        Assert.IsFalse(new SiemensS7_200SmartProtocolDriverFactory().Metadata.RequiresStation);
    }

    // StationHint 会直接展示给操作员，缺失会导致不知道填什么范围
    [TestMethod]
    public void ProtocolsRequiringStation_ProvideHintText()
    {
        // ============================================================================
        // Assert
        // ============================================================================
        // UI 直接展示该文案，缺失会导致操作员不知道填什么范围
        Assert.IsFalse(string.IsNullOrWhiteSpace(
            new ModbusTcpProtocolDriverFactory().Metadata.StationHint));
        Assert.IsFalse(string.IsNullOrWhiteSpace(
            new MewtocolProtocolDriverFactory().Metadata.StationHint));
    }

    // 防回归：UI 曾把展示名当 ProtocolId 回传，服务端匹配不到工厂
    [TestMethod]
    public void AllProtocolIds_AreLowerKebabCase_NotDisplayNames()
    {
        // ============================================================================
        // Arrange
        // ============================================================================
        // 防回归：UI 曾把展示名当 ProtocolId 回传，导致服务端匹配不到协议工厂
        string[] ids = {
            new ModbusTcpProtocolDriverFactory().Metadata.ProtocolId,
            new ModbusRtuProtocolDriverFactory().Metadata.ProtocolId,
            new ModbusAsciiProtocolDriverFactory().Metadata.ProtocolId,
            new MewtocolProtocolDriverFactory().Metadata.ProtocolId,
            new SiemensS7_1200ProtocolDriverFactory().Metadata.ProtocolId,
            new SiemensS7_200SmartProtocolDriverFactory().Metadata.ProtocolId,
        };

        // ============================================================================
        // Assert
        // ============================================================================
        foreach (string id in ids)
        {
            Assert.IsFalse(string.IsNullOrWhiteSpace(id));
            Assert.DoesNotContain(" ", id, $"ProtocolId 不应含空格: {id}");
            Assert.AreEqual(id.ToLowerInvariant(), id, $"ProtocolId 应为小写: {id}");
        }
    }
}
