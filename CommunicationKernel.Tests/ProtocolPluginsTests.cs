// -----------------------------------------------------------------------------
// 文件: ProtocolPluginsTests.cs
// 层级: Tests
// 作用: 协议插件帧构建、地址解析、校验算法、异常码映射的单元测试。
// 覆盖: Modbus TCP / RTU / ASCII, Siemens S7, Panasonic Mewtocol TCP
// -----------------------------------------------------------------------------

using CommunicationKernel.Core.Abstractions.Results;

// ---- Modbus TCP internals
using CommunicationKernel.Plugins.Modbus.Tcp.Internal;

// ---- Modbus RTU internals
using ModbusRtuFrame   = CommunicationKernel.Plugins.Modbus.Rtu.Internal.ModbusRtuFrame;
using ModbusRtuAddress = CommunicationKernel.Plugins.Modbus.Rtu.Internal.ModbusRtuAddress;

// ---- Modbus ASCII internals
using ModbusAsciiFrame   = CommunicationKernel.Plugins.Modbus.Ascii.Internal.ModbusAsciiFrame;
using ModbusAsciiAddress = CommunicationKernel.Plugins.Modbus.Ascii.Internal.ModbusAsciiAddress;

// ---- Siemens S7 internals
using CommunicationKernel.Plugins.Siemens.S7.Internal;

// ---- Panasonic Mewtocol internals
using CommunicationKernel.Plugins.Panasonic.MewtocolTcp.Internal;

// ---- Public plugins (Manifest + Factory)
using CommunicationKernel.Plugins.Modbus.Tcp;
using CommunicationKernel.Plugins.Modbus.Rtu;
using CommunicationKernel.Plugins.Modbus.Ascii;
using CommunicationKernel.Plugins.Siemens.S7;
using CommunicationKernel.Plugins.Panasonic.MewtocolTcp;

namespace CommunicationKernel.Tests;

// =============================================================================
// Modbus TCP — 地址解析
// =============================================================================

[TestClass]
public class ModbusTcpAddressTests
{
    [TestMethod]
    public void Parse_40001_ReturnsHoldingRegisterAt0()
    {
        var r = ModbusAddress.Parse("40001");
        Assert.IsTrue(r.Success);
        Assert.AreEqual(0, r.Value.RegisterAddress);
        Assert.IsFalse(r.Value.IsCoil);
        Assert.AreEqual(1, r.Value.UnitId);
    }

    [TestMethod]
    public void Parse_40100_ReturnsOffset99()
    {
        var r = ModbusAddress.Parse("40100");
        Assert.IsTrue(r.Success);
        Assert.AreEqual(99, r.Value.RegisterAddress);
    }

    [TestMethod]
    public void Parse_CoilPrefix_ReturnsCoil()
    {
        var r = ModbusAddress.Parse("coil:5");
        Assert.IsTrue(r.Success);
        Assert.IsTrue(r.Value.IsCoil);
        Assert.AreEqual(5, r.Value.RegisterAddress);
    }

    [TestMethod]
    public void Parse_UnitIdPrefix_ExtractsUnitId()
    {
        var r = ModbusAddress.Parse("3:40001");
        Assert.IsTrue(r.Success);
        Assert.AreEqual(3, r.Value.UnitId);
        Assert.AreEqual(0, r.Value.RegisterAddress);
    }

    [TestMethod]
    public void Parse_BareNumber_ReturnsRegister()
    {
        var r = ModbusAddress.Parse("100");
        Assert.IsTrue(r.Success);
        Assert.AreEqual(100, r.Value.RegisterAddress);
        Assert.IsFalse(r.Value.IsCoil);
    }

    [TestMethod]
    public void Parse_Empty_Fails()
    {
        var r = ModbusAddress.Parse("");
        Assert.IsFalse(r.Success);
    }
}

// =============================================================================
// Modbus TCP — 帧构建
// =============================================================================

[TestClass]
public class ModbusTcpFrameTests
{
    [TestMethod]
    public void BuildReadFrame_FC03_CorrectMbapAndPdu()
    {
        byte[] frame = ModbusFrame.BuildReadFrame(
            transactionId: 0x0001, unitId: 1,
            isCoil: false, startAddress: 0, quantity: 10);

        Assert.HasCount(12, frame);
        Assert.AreEqual(0x00, frame[0]); // Transaction ID Hi
        Assert.AreEqual(0x01, frame[1]); // Transaction ID Lo
        Assert.AreEqual(0x00, frame[2]); // Protocol Hi
        Assert.AreEqual(0x00, frame[3]); // Protocol Lo
        Assert.AreEqual(0x00, frame[4]); // Length Hi
        Assert.AreEqual(0x06, frame[5]); // Length Lo
        Assert.AreEqual(0x01, frame[6]); // Unit ID
        Assert.AreEqual(0x03, frame[7]); // FC03
        Assert.AreEqual(0x00, frame[8]); // Addr Hi
        Assert.AreEqual(0x00, frame[9]); // Addr Lo
        Assert.AreEqual(0x00, frame[10]); // Qty Hi
        Assert.AreEqual(0x0A, frame[11]); // Qty Lo = 10
    }

    [TestMethod]
    public void BuildReadFrame_FC01_UsesReadCoilsFunctionCode()
    {
        byte[] frame = ModbusFrame.BuildReadFrame(0x0001, 1, isCoil: true, startAddress: 0, quantity: 8);
        Assert.AreEqual(0x01, frame[7]); // FC01
    }

    [TestMethod]
    public void BuildWriteSingleRegister_FC06_CorrectFrame()
    {
        byte[] frame = ModbusFrame.BuildWriteSingleRegister(0x0002, 1, address: 5, value: 0x1234);
        Assert.HasCount(12, frame);
        Assert.AreEqual(0x06, frame[7]); // FC06
        Assert.AreEqual(0x00, frame[8]); // Addr Hi
        Assert.AreEqual(0x05, frame[9]); // Addr Lo
        Assert.AreEqual(0x12, frame[10]); // Value Hi
        Assert.AreEqual(0x34, frame[11]); // Value Lo
    }

    [TestMethod]
    public void ParseReadRegistersResponse_ExceptionResponse_ReturnsError()
    {
        // FC03 | 0x80 = 0x83 → exception, code 0x02 (Illegal Data Address)
        byte[] response = new byte[] {
            0x00, 0x01, 0x00, 0x00, 0x00, 0x03, 0x01,
            0x83, 0x02
        };
        var r = ModbusFrame.ParseReadRegistersResponse(response);
        Assert.IsFalse(r.Success);
        StringAssert.Contains(r.ErrorMessage, "Illegal Data Address");
    }

    [TestMethod]
    public void ParseReadRegistersResponse_ValidResponse_ReturnsData()
    {
        // MBAP(7) + FC(1) + ByteCount(1) + Data(4) = 13 bytes
        byte[] response = new byte[] {
            0x00, 0x01, 0x00, 0x00, 0x00, 0x07, 0x01,
            0x03,              // FC03
            0x04,              // ByteCount = 4
            0x00, 0x0A, 0x00, 0x14
        };
        var r = ModbusFrame.ParseReadRegistersResponse(response);
        Assert.IsTrue(r.Success);
        Assert.HasCount(4, r.Value);
        Assert.AreEqual(0x00, r.Value[0]);
        Assert.AreEqual(0x0A, r.Value[1]);
    }
}

// =============================================================================
// Modbus RTU — CRC16 与帧构建
// =============================================================================

[TestClass]
public class ModbusRtuCrcTests
{
    [TestMethod]
    public void ComputeCrc_KnownVector_MatchesExpected()
    {
        // 01 03 00 00 00 0A → CRC-Lo=0xC5, CRC-Hi=0xCD
        // ushort（小端解释）= 0xCD<<8 | 0xC5 = 0xCDC5
        byte[] data = new byte[] { 0x01, 0x03, 0x00, 0x00, 0x00, 0x0A };
        ushort crc = ModbusRtuFrame.ComputeCrc(data, data.Length);
        Assert.AreEqual((ushort)0xCDC5, crc);
        // 验证 Lo/Hi 字节顺序（实际帧写入顺序）
        Assert.AreEqual(0xC5, crc & 0xFF);   // Lo byte（先写）
        Assert.AreEqual(0xCD, crc >> 8);     // Hi byte（后写）
    }

    [TestMethod]
    public void BuildReadFrame_FC03_AppendsCrcLittleEndian()
    {
        byte[] frame = ModbusRtuFrame.BuildReadFrame(
            slaveId: 1, isCoil: false, startAddress: 0, quantity: 10);

        // frame = [01][03][00][00][00][0A][CRC-Lo][CRC-Hi]
        Assert.HasCount(8, frame);
        Assert.AreEqual(0x01, frame[0]); // SlaveId
        Assert.AreEqual(0x03, frame[1]); // FC03

        ushort expectedCrc = ModbusRtuFrame.ComputeCrc(frame, 6);
        ushort actualCrc   = (ushort)(frame[6] | (frame[7] << 8));
        Assert.AreEqual(expectedCrc, actualCrc);
    }

    [TestMethod]
    public void BuildWriteMultipleRegisters_CorrectPayloadLength()
    {
        byte[] payload = new byte[] { 0x00, 0x01, 0x00, 0x02 }; // 2 registers
        byte[] frame = ModbusRtuFrame.BuildWriteMultipleRegisters(1, 0, payload);

        // [SlaveId][FC10][Addr(2)][RegCount(2)][ByteCount(1)][Data(4)][CRC(2)] = 13
        Assert.HasCount(13, frame);
        Assert.AreEqual(0x10, frame[1]); // FC10
        Assert.AreEqual(0x00, frame[4]); // RegCount Hi
        Assert.AreEqual(0x02, frame[5]); // RegCount Lo = 2
        Assert.AreEqual(0x04, frame[6]); // ByteCount = 4
    }

    [TestMethod]
    public void ParseReadRegistersResponse_ShortFrame_ReturnsError()
    {
        byte[] shortResponse = new byte[] { 0x01, 0x03, 0x02, 0x00, 0x0A };
        var r = ModbusRtuFrame.ParseReadRegistersResponse(shortResponse);
        Assert.IsFalse(r.Success);
    }

    [TestMethod]
    public void ParseReadRegistersResponse_ValidFrameWithCrc_ReturnsData()
    {
        // 手工构建合法 FC03 响应: [01][03][04][00 0A][00 14][CRC-Lo][CRC-Hi]
        byte[] pdu  = new byte[] { 0x01, 0x03, 0x04, 0x00, 0x0A, 0x00, 0x14 };
        ushort crc  = ModbusRtuFrame.ComputeCrc(pdu, pdu.Length);
        byte[] full = new byte[pdu.Length + 2];
        Buffer.BlockCopy(pdu, 0, full, 0, pdu.Length);
        full[pdu.Length]     = (byte)(crc & 0xFF);
        full[pdu.Length + 1] = (byte)(crc >> 8);

        var r = ModbusRtuFrame.ParseReadRegistersResponse(full);
        Assert.IsTrue(r.Success);
        Assert.HasCount(4, r.Value);
    }
}

// =============================================================================
// Modbus ASCII — LRC 与帧往返
// =============================================================================

[TestClass]
public class ModbusAsciiFrameTests
{
    [TestMethod]
    public void ComputeLrc_KnownVector_MatchesExpected()
    {
        // SlaveId=01 FC=03 Addr=0000 Qty=000A → sum=0x0E → LRC = 0x100-0x0E = 0xF2
        byte[] data = new byte[] { 0x01, 0x03, 0x00, 0x00, 0x00, 0x0A };
        byte lrc = ModbusAsciiFrame.ComputeLrc(data, 0, data.Length);
        Assert.AreEqual(0xF2, lrc);
    }

    [TestMethod]
    public void BuildReadFrame_StartsWithColon_EndsWithCrLf()
    {
        byte[] frame = ModbusAsciiFrame.BuildReadFrame(1, false, 0, 10);
        Assert.AreEqual((byte)':', frame[0]);
        Assert.AreEqual(0x0D, frame[frame.Length - 2]); // CR
        Assert.AreEqual(0x0A, frame[frame.Length - 1]); // LF
    }

    [TestMethod]
    public void BuildAndParseReadFrame_Roundtrip_ReturnsCorrectData()
    {
        // 构造合法的读响应 ASCII 帧
        byte[] pduBytes = new byte[] { 0x01, 0x03, 0x04, 0x00, 0x0A, 0x00, 0x14 };
        byte lrc = ModbusAsciiFrame.ComputeLrc(pduBytes, 0, pduBytes.Length);

        var sb = new System.Text.StringBuilder();
        sb.Append(':');
        foreach (byte b in pduBytes)
            sb.Append(b.ToString("X2"));
        sb.Append(lrc.ToString("X2"));
        sb.Append("\r\n");

        byte[] asciiFrame = System.Text.Encoding.ASCII.GetBytes(sb.ToString());

        var r = ModbusAsciiFrame.ParseReadRegistersResponse(asciiFrame);
        Assert.IsTrue(r.Success, r.ErrorMessage);
        Assert.HasCount(4, r.Value); // ByteCount=4
        Assert.AreEqual(0x0A, r.Value[1]);
    }

    [TestMethod]
    public void ParseReadCoilsResponse_LrcMismatch_ReturnsError()
    {
        byte[] frame = ModbusAsciiFrame.BuildReadFrame(1, true, 0, 8);
        // 篡改 LRC hex 字节（倒数第3、4字节）
        frame[frame.Length - 3] ^= 0x01;
        var r = ModbusAsciiFrame.ParseReadCoilsResponse(frame);
        Assert.IsFalse(r.Success);
    }

    [TestMethod]
    public void BuildWriteSingleCoil_ON_ContainsFF00()
    {
        byte[] frame = ModbusAsciiFrame.BuildWriteSingleCoil(1, address: 0, value: true);
        string ascii = System.Text.Encoding.ASCII.GetString(frame);
        Assert.StartsWith(":", ascii);
        Assert.Contains("FF00", ascii);
    }

    [TestMethod]
    public void BuildWriteSingleCoil_OFF_ContainsZeroValue()
    {
        byte[] frame = ModbusAsciiFrame.BuildWriteSingleCoil(1, address: 0, value: false);
        string ascii = System.Text.Encoding.ASCII.GetString(frame);
        Assert.Contains("0000", ascii);
    }
}

// =============================================================================
// Modbus RTU — 地址解析
// =============================================================================

[TestClass]
public class ModbusRtuAddressTests
{
    [TestMethod]
    public void Parse_SlaveIdAndRegister_ExtractsBoth()
    {
        var r = ModbusRtuAddress.Parse("2:40010");
        Assert.IsTrue(r.Success);
        Assert.AreEqual(2, r.Value.SlaveId);
        Assert.AreEqual(9, r.Value.RegisterAddress);
    }

    [TestMethod]
    public void Parse_CoilPrefix_IsCoilTrue()
    {
        var r = ModbusRtuAddress.Parse("coil:100");
        Assert.IsTrue(r.Success);
        Assert.IsTrue(r.Value.IsCoil);
        Assert.AreEqual(100, r.Value.RegisterAddress);
    }
}

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
    public void MewtocolTcpManifest_ApiVersion1_PluginIdCorrect()
    {
        var manifest = new MewtocolTcpPluginManifest();
        Assert.AreEqual(1, manifest.Descriptor.ApiVersion);
        Assert.AreEqual("panasonic-mewtocol-tcp", manifest.Descriptor.PluginId);
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
