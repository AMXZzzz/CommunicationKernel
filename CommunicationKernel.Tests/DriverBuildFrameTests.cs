// -----------------------------------------------------------------------------
// 文件: DriverBuildFrameTests.cs
// 层级: Tests
// 作用: 通过公开 IProtocolDriver 接口验证各协议 BuildReadFrame / BuildWriteFrame
//       返回正确的字节帧（驱动级别，不依赖 internal 类）。
// -----------------------------------------------------------------------------

using CommunicationKernel.Communication.Protocol.Abstractions;
using CommunicationKernel.Core.Abstractions.Results;
using CommunicationKernel.Plugins.Modbus.Tcp;
using CommunicationKernel.Plugins.Modbus.Rtu;
using CommunicationKernel.Plugins.Modbus.Ascii;
using CommunicationKernel.Plugins.Siemens.S7;
using CommunicationKernel.Plugins.Panasonic.MewtocolTcp;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace CommunicationKernel.Tests;

// =============================================================================
// Modbus TCP Driver — BuildReadFrame / BuildWriteFrame
// =============================================================================

[TestClass]
public sealed class ModbusTcpDriverFrameTests {

    private readonly IProtocolDriver _driver =
        new ModbusTcpProtocolDriverFactory().CreateDriver();

    [TestMethod]
    public void BuildReadFrame_HoldingRegister_ReturnsFC03Frame() {
        OperationResult<byte[]> r = _driver.BuildReadFrame("40001", 2);

        Assert.IsTrue(r.Success);
        // MBAP(7) + PDU(5) = 12 bytes
        Assert.HasCount(12, r.Value);
        // 协议标识符 = 0x0000
        Assert.AreEqual(0x00, r.Value[2]);
        Assert.AreEqual(0x00, r.Value[3]);
        // PDU 长度 = 6
        Assert.AreEqual(0x06, r.Value[5]);
        // FC03
        Assert.AreEqual(0x03, r.Value[7]);
        // 寄存器地址 0 (40001 → offset 0)
        Assert.AreEqual(0x00, r.Value[8]);
        Assert.AreEqual(0x00, r.Value[9]);
        // quantity = ceil(2/2) = 1
        Assert.AreEqual(0x00, r.Value[10]);
        Assert.AreEqual(0x01, r.Value[11]);
    }

    [TestMethod]
    public void BuildReadFrame_Coil_ReturnsFC01Frame() {
        OperationResult<byte[]> r = _driver.BuildReadFrame("coil:10", 1);

        Assert.IsTrue(r.Success);
        Assert.AreEqual(0x01, r.Value[7]); // FC01
        Assert.AreEqual(0x00, r.Value[8]);
        Assert.AreEqual(0x0A, r.Value[9]); // 地址 10
    }

    [TestMethod]
    public void BuildReadFrame_InvalidAddress_ReturnsFail() {
        OperationResult<byte[]> r = _driver.BuildReadFrame("INVALID_ADDR_XYZ", 2);
        Assert.IsFalse(r.Success);
    }

    [TestMethod]
    public void BuildWriteFrame_SingleRegister_ReturnsFC06Frame() {
        // 2 字节 payload → FC06
        OperationResult<byte[]> r = _driver.BuildWriteFrame("40001", new byte[] { 0x00, 0x64 });

        Assert.IsTrue(r.Success);
        Assert.AreEqual(0x06, r.Value[7]); // FC06
        Assert.AreEqual(0x00, r.Value[10]); // value hi
        Assert.AreEqual(0x64, r.Value[11]); // value lo = 100
    }

    [TestMethod]
    public void BuildWriteFrame_MultipleRegisters_ReturnsFC10Frame() {
        // 4 字节 payload → FC10
        OperationResult<byte[]> r = _driver.BuildWriteFrame("40001", new byte[] { 0x00, 0x01, 0x00, 0x02 });

        Assert.IsTrue(r.Success);
        Assert.AreEqual(0x10, r.Value[7]); // FC10
    }

    [TestMethod]
    public void BuildWriteFrame_EmptyPayload_ReturnsFail() {
        OperationResult<byte[]> r = _driver.BuildWriteFrame("40001", Array.Empty<byte>());
        Assert.IsFalse(r.Success);
    }
}

// =============================================================================
// Modbus RTU Driver — BuildReadFrame / BuildWriteFrame
// =============================================================================

[TestClass]
public sealed class ModbusRtuDriverFrameTests {

    private readonly IProtocolDriver _driver =
        new ModbusRtuProtocolDriverFactory().CreateDriver();

    [TestMethod]
    public void BuildReadFrame_HoldingRegister_HasFC03AndCrc() {
        OperationResult<byte[]> r = _driver.BuildReadFrame("40001", 4);

        Assert.IsTrue(r.Success);
        // [SlaveId][FC03][AddrHi][AddrLo][QtyHi][QtyLo][CrcLo][CrcHi]
        Assert.HasCount(8, r.Value);
        Assert.AreEqual(0x01, r.Value[0]); // SlaveId = 1
        Assert.AreEqual(0x03, r.Value[1]); // FC03
        Assert.AreEqual(0x00, r.Value[2]); // Addr Hi
        Assert.AreEqual(0x00, r.Value[3]); // Addr Lo = 0
        Assert.AreEqual(0x00, r.Value[4]); // Qty Hi
        Assert.AreEqual(0x02, r.Value[5]); // Qty Lo = ceil(4/2) = 2
    }

    [TestMethod]
    public void BuildReadFrame_WithExplicitSlaveId_UsesCorrectId() {
        OperationResult<byte[]> r = _driver.BuildReadFrame("3:40001", 2);

        Assert.IsTrue(r.Success);
        Assert.AreEqual(0x03, r.Value[0]); // SlaveId = 3
    }

    [TestMethod]
    public void BuildWriteFrame_SingleCoil_ReturnsFC05() {
        // 1 字节 coil payload → FC05
        OperationResult<byte[]> r = _driver.BuildWriteFrame("coil:0", new byte[] { 0xFF });

        Assert.IsTrue(r.Success);
        Assert.AreEqual(0x05, r.Value[1]); // FC05
    }
}

// =============================================================================
// Modbus ASCII Driver — BuildReadFrame / BuildWriteFrame
// =============================================================================

[TestClass]
public sealed class ModbusAsciiDriverFrameTests {

    private readonly IProtocolDriver _driver =
        new ModbusAsciiProtocolDriverFactory().CreateDriver();

    [TestMethod]
    public void BuildReadFrame_StartsWithColonEndsWithCrLf() {
        OperationResult<byte[]> r = _driver.BuildReadFrame("40001", 2);

        Assert.IsTrue(r.Success);
        Assert.AreEqual((byte)':', r.Value[0]);
        Assert.AreEqual(0x0D, r.Value[r.Value.Length - 2]); // CR
        Assert.AreEqual(0x0A, r.Value[r.Value.Length - 1]); // LF
    }

    [TestMethod]
    public void BuildReadFrame_IsAllAsciiPrintable() {
        OperationResult<byte[]> r = _driver.BuildReadFrame("40001", 2);
        Assert.IsTrue(r.Success);

        // 除末尾 CR/LF 外，每个字节应为可打印 ASCII
        for (int i = 1; i < r.Value.Length - 2; i++) {
            Assert.IsTrue(r.Value[i] >= 0x30 && r.Value[i] <= 0x46,
                $"byte[{i}] = 0x{r.Value[i]:X2} is not hex digit or colon");
        }
    }

    [TestMethod]
    public void BuildWriteFrame_ON_ContainsFF00() {
        OperationResult<byte[]> r = _driver.BuildWriteFrame("coil:0", new byte[] { 0x01 });

        Assert.IsTrue(r.Success);
        string ascii = System.Text.Encoding.ASCII.GetString(r.Value);
        Assert.IsTrue(ascii.Contains("FF00"), $"Expected FF00 in: {ascii}");
    }
}

// =============================================================================
// Siemens S7 Driver — BuildReadFrame / BuildWriteFrame
// =============================================================================

[TestClass]
public sealed class SiemensS7DriverFrameTests {

    private readonly IProtocolDriver _driver =
        new SiemensS7_1200ProtocolDriverFactory().CreateDriver();

    [TestMethod]
    public void BuildReadFrame_DB10_DBW0_ReturnsTpktFrame() {
        OperationResult<byte[]> r = _driver.BuildReadFrame("DB10.DBW0", 2);

        Assert.IsTrue(r.Success);
        // TPKT 版本 = 0x03
        Assert.AreEqual(0x03, r.Value[0]);
        // 最小长度：TPKT(4) + COTP DT(3) + S7Header(10) + Param ≥ 31
        Assert.IsTrue(r.Value.Length >= 31, $"frame too short: {r.Value.Length}");
        // S7 function: Read Var = 0x04
        Assert.AreEqual(0x04, r.Value[17]);
    }

    [TestMethod]
    public void BuildReadFrame_MB10_ReturnsTpktFrame() {
        OperationResult<byte[]> r = _driver.BuildReadFrame("MB10", 1);

        Assert.IsTrue(r.Success);
        Assert.AreEqual(0x03, r.Value[0]); // TPKT version
    }

    [TestMethod]
    public void BuildReadFrame_InvalidAddress_ReturnsFail() {
        OperationResult<byte[]> r = _driver.BuildReadFrame("BADADDR%%", 2);
        Assert.IsFalse(r.Success);
    }

    [TestMethod]
    public void BuildWriteFrame_DB10_DBW0_ReturnsTpktWriteFrame() {
        OperationResult<byte[]> r = _driver.BuildWriteFrame("DB10.DBW0", new byte[] { 0x00, 0xFF });

        Assert.IsTrue(r.Success);
        Assert.AreEqual(0x03, r.Value[0]); // TPKT version
        // S7 function: Write Var = 0x05
        Assert.AreEqual(0x05, r.Value[17]);
    }
}

// =============================================================================
// Panasonic Mewtocol TCP Driver — BuildReadFrame / BuildWriteFrame
// =============================================================================

[TestClass]
public sealed class MewtocolTcpDriverFrameTests {

    private readonly IProtocolDriver _driver =
        new MewtocolTcpProtocolDriverFactory().CreateDriver();

    [TestMethod]
    public void BuildReadFrame_DT100_ReturnsMewtocolAsciiFrame() {
        OperationResult<byte[]> r = _driver.BuildReadFrame("DT100", 2);

        Assert.IsTrue(r.Success);
        // MEWTOCOL 帧以 '%' 开头，以 CR 结尾
        Assert.AreEqual((byte)'%', r.Value[0]);
        Assert.AreEqual(0x0D, r.Value[r.Value.Length - 1]); // CR
    }

    [TestMethod]
    public void BuildReadFrame_BitAddress_ReturnsMewtocolContactFrame() {
        // 位地址 (IsBit=true) → RCS 命令
        OperationResult<byte[]> r = _driver.BuildReadFrame("X0", 1);

        Assert.IsTrue(r.Success);
        string ascii = System.Text.Encoding.ASCII.GetString(r.Value);
        Assert.IsTrue(ascii.Contains("RCS") || ascii.Contains("RD"),
            $"Expected contact read command, got: {ascii}");
    }

    [TestMethod]
    public void BuildReadFrame_InvalidAddress_ReturnsFail() {
        OperationResult<byte[]> r = _driver.BuildReadFrame("ZZINVALID999", 2);
        Assert.IsFalse(r.Success);
    }

    [TestMethod]
    public void BuildWriteFrame_DT100_ReturnsMewtocolWriteFrame() {
        OperationResult<byte[]> r = _driver.BuildWriteFrame("DT100", new byte[] { 0x00, 0x64 });

        Assert.IsTrue(r.Success);
        Assert.AreEqual((byte)'%', r.Value[0]);
        string ascii = System.Text.Encoding.ASCII.GetString(r.Value);
        Assert.IsTrue(ascii.Contains("WD"), $"Expected WD command, got: {ascii}");
    }

    [TestMethod]
    public void BuildWriteFrame_EmptyPayload_ReturnsFail() {
        OperationResult<byte[]> r = _driver.BuildWriteFrame("DT100", Array.Empty<byte>());
        Assert.IsFalse(r.Success);
    }
}
